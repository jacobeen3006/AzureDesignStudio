using AzureDesignStudio.Discovery.Analysis;

namespace AzureDesignStudio.Discovery.Analysis;

/// <summary>A node's placement in canvas coordinates. Containers lists the frame chain,
/// innermost → outermost (e.g. subnet: [vnet, rg:…, sub:…]).</summary>
public sealed record PlacedNode(TopologyNode Node, double X, double Y, double W, double H, IReadOnlyList<string> Containers);

/// <summary>A nested frame box: subscription, resource group, a resource that holds children
/// (vnet over its subnets) or the External lane for cross-scope ghosts.</summary>
public sealed record PlacedContainer(string Id, string Label, string? Parent, double X, double Y, double W, double H);

public sealed class TopologyLayoutResult
{
    public required IReadOnlyList<PlacedNode> Nodes { get; init; }
    public required IReadOnlyList<PlacedContainer> Containers { get; init; }
    public required IReadOnlyDictionary<string, PlacedContainer> ContainersById { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

/// <summary>
/// Canonical nested framing (v2 plan 8.2): canvas → subscription → resource group → resource
/// nesting (vnet → subnet); cross-scope ghosts park in an External lane, never in a real group.
/// Layout is deterministic (everything sorted by ARM id) and identity-stable: persisted local
/// offsets win, new members flow into grid slots after them, so a rescan highlights the change
/// instead of shuffling the picture. Saved coordinates are LOCAL to the frame container —
/// exactly what NodePositionRecord persists.
/// </summary>
public static class TopologyLayout
{
    public const double NodeW = 190, NodeH = 54;
    private const double Pad = 14, Title = 26, Gap = 18, CellW = 208, CellH = 76;
    private const int Cols = 3;
    public const string ExternalLane = "external";
    private const double LaneWidth = 1400;

    /// <summary>Node id → (frame container id, local x, local y) from the last session.</summary>
    public sealed record SavedPos(string ContainerId, double X, double Y);

    public static TopologyLayoutResult Layout(TopologyGraph graph, IReadOnlyDictionary<string, SavedPos>? saved = null)
    {
        saved ??= new Dictionary<string, SavedPos>();
        var nodes = graph.Nodes;

        var members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parentOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var nodeFrame = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var isContainer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Ensure(string containerId, string label, string? parent)
        {
            isContainer.Add(containerId);
            labels.TryAdd(containerId, label);
            if (!parentOf.ContainsKey(containerId)) parentOf[containerId] = parent;
            if (!members.ContainsKey(containerId)) members[containerId] = [];
        }

        void AddMember(string containerId, string memberId)
        {
            if (!members.TryGetValue(containerId, out var list)) members[containerId] = list = [];
            if (!list.Contains(memberId, StringComparer.OrdinalIgnoreCase)) list.Add(memberId);
        }

        string SubContainer(string subscription)
        {
            var id = $"sub:{subscription}";
            Ensure(id, subscription.Length > 8 ? subscription[..8] + "…" : subscription, null);
            return id;
        }

        string RgContainer(string subscription, string rg)
        {
            var id = $"rg:{subscription}|{rg}";
            Ensure(id, rg, SubContainer(subscription));
            AddMember(SubContainer(subscription), id);
            return id;
        }

        // --- membership ---
        foreach (var n in nodes.Values.OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase))
        {
            string frame;
            var pid = n.ParentId;
            if (pid is not null && nodes.ContainsKey(pid) && !AncestryLoop(pid, n.Id, nodes))
            {
                frame = pid; // nests inside its parent resource (subnet → vnet, sql db → server)
                Ensure(pid, nodes[pid].Name, null); // parent doubles as container; real parent set below
            }
            else if (n.External)
            {
                Ensure(ExternalLane, "External references", null);
                frame = ExternalLane;
            }
            else
            {
                var sub = SubscriptionOf(n.Id);
                frame = n.ResourceGroup is { Length: > 0 } rg
                    ? RgContainer(sub, rg)
                    : SubContainer(sub);
            }

            nodeFrame[n.Id] = frame;
            AddMember(frame, n.Id);
        }

        // a resource that holds children is a container: give it a real frame parent now
        foreach (var cid in isContainer.Where(id => nodes.ContainsKey(id)).ToList())
        {
            var owner = nodes[cid];
            parentOf[cid] = owner.External ? ExternalLane : nodeFrame.TryGetValue(cid, out var f) && f != cid ? f : FrameOfOwner(owner, nodeFrame);
        }

        bool AncestryLoop(string pid, string childId, IReadOnlyDictionary<string, TopologyNode> all)
        {
            var seen = 0;
            for (var cursor = pid; cursor is not null && seen++ < 64;
                 cursor = all.TryGetValue(cursor, out var pn) ? pn.ParentId : null)
                if (string.Equals(cursor, childId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        string FrameOfOwner(TopologyNode owner, Dictionary<string, string> frameMap)
        {
            var pid = owner.ParentId;
            if (pid is not null && frameMap.TryGetValue(pid, out var pf)) return pf;
            if (owner.External) { Ensure(ExternalLane, "External references", null); return ExternalLane; }
            var sub = SubscriptionOf(owner.Id);
            return owner.ResourceGroup is { Length: > 0 } rg2 ? RgContainer(sub, rg2) : SubContainer(sub);
        }

        // any container whose parent was set to null above and isn't top-level needs a home
        foreach (var cid in isContainer.Where(id => parentOf[id] is null && id != ExternalLane && !id.StartsWith("sub:")).ToList())
        {
            if (nodes.TryGetValue(cid, out var owner))
            {
                parentOf[cid] = FrameOfOwner(owner, nodeFrame);
                AddMember(parentOf[cid]!, cid);
            }
        }

        // --- sizes: children before parents ---
        var localPos = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
        var size = new Dictionary<string, (double W, double H)>(StringComparer.OrdinalIgnoreCase);
        var childFirst = Order(true);
        var parentFirst = Order(false);

        foreach (var cid in childFirst)
        {
            var childIds = members[cid].OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            double maxX = 0, maxY = 0;
            // saved members already own their grid slots; new ones flow in after them
            long pending = childIds.Count(id => saved.TryGetValue(id, out var s)
                && string.Equals(s.ContainerId, cid, StringComparison.OrdinalIgnoreCase));
            foreach (var childId in childIds)
            {
                var (w, h) = size.TryGetValue(childId, out var cs) ? cs : (NodeW, NodeH);
                double x, y;
                if (saved.TryGetValue(childId, out var sp)
                    && string.Equals(sp.ContainerId, cid, StringComparison.OrdinalIgnoreCase))
                {
                    (x, y) = (sp.X, sp.Y); // identity-stable: persisted offset inside this frame
                }
                else
                {
                    x = Pad + pending % Cols * CellW;
                    y = Pad + Title + pending / Cols * CellH;
                    pending++;
                }

                localPos[childId] = (x, y);
                maxX = Math.Max(maxX, x + w);
                maxY = Math.Max(maxY, y + h);
            }

            var boxWidth = Math.Max(maxX + Pad, Cols * CellW + 2 * Pad);
            var boxHeight = Math.Max(maxY + Pad, Title + CellH + Pad);
            if (childIds.Count == 0) (boxWidth, boxHeight) = (280, 110);
            size[cid] = (boxWidth, boxHeight);
        }

        // --- global placement: parents before children; top-level frames flow on the canvas ---
        var boxes = new Dictionary<string, PlacedContainer>(StringComparer.OrdinalIgnoreCase);
        double canvasX = 0, canvasY = 0, rowH = 0;
        foreach (var cid in parentFirst)
        {
            var (w, h) = size[cid];
            double x, y;
            var parent = parentOf[cid];
            if (parent is null || !boxes.TryGetValue(parent, out var parentBox))
            {
                if (cid == ExternalLane) continue; // parked after the loop, below all frames
                else
                {
                    if (canvasX + w > LaneWidth)
                    {
                        canvasX = 0;
                        canvasY += rowH + Gap;
                        rowH = 0;
                    }
                    (x, y) = (canvasX, canvasY);
                    canvasX += w + Gap;
                    rowH = Math.Max(rowH, h);
                }
            }
            else
            {
                var (lx, ly) = localPos[cid];
                (x, y) = (parentBox.X + lx, parentBox.Y + ly);
            }

            boxes[cid] = new PlacedContainer(cid, labels[cid], parent, x, y, w, h);
        }

        // external lane last: it sits below every scanned frame
        if (isContainer.Contains(ExternalLane))
        {
            var (w, h) = size[ExternalLane];
            boxes[ExternalLane] = new PlacedContainer(ExternalLane, labels[ExternalLane], null, 0, canvasY + rowH + 140, w, h);
            rowH = 0;
        }

        // --- nodes: container-nodes take their box geometry, plain nodes ride their frame ---
        var placedNodes = new List<PlacedNode>();
        foreach (var n in nodes.Values.OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (isContainer.Contains(n.Id) && boxes.TryGetValue(n.Id, out var own))
            {
                placedNodes.Add(new PlacedNode(n, own.X, own.Y, own.W, NodeH, ChainUp(n.Id)));
                continue;
            }

            var frame = nodeFrame[n.Id];
            if (!boxes.TryGetValue(frame, out var frameBox)) continue;
            var (lx, ly) = localPos[n.Id];
            placedNodes.Add(new PlacedNode(n, frameBox.X + lx, frameBox.Y + ly, NodeW, NodeH, ChainUp(frame)));
        }

        double height = Math.Max(canvasY + rowH, boxes.Count > 0 ? boxes.Values.Max(b => b.Y + b.H) : 0) + 120;
        double width = Math.Max(canvasX, boxes.Count > 0 ? boxes.Values.Max(b => b.X + b.W) : 0);

        return new TopologyLayoutResult
        {
            Nodes = placedNodes,
            Containers = boxes.Values.ToList(),
            ContainersById = boxes,
            Width = width,
            Height = height,
        };

        List<string> ChainUp(string from)
        {
            var chain = new List<string>();
            var guard = 0;
            for (string? cursor = from; cursor is not null && guard++ < 32;
                 cursor = parentOf.TryGetValue(cursor, out var p) ? p : null)
                chain.Add(cursor);
            return chain;
        }

        // depth order over the container graph
        List<string> Order(bool childrenFirst)
        {
            var result = new List<string>();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in isContainer.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                Visit(id);
            return childrenFirst ? Enumerable.Reverse(result).ToList() : result;

            void Visit(string id)
            {
                if (!done.Add(id)) return;
                var parent = parentOf.TryGetValue(id, out var p) ? p : null;
                if (parent is not null && isContainer.Contains(parent)) Visit(parent);
                result.Add(id);
            }
        }
    }

    private static string SubscriptionOf(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && string.Equals(parts[0], "subscriptions", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : "unknown";
    }
}
