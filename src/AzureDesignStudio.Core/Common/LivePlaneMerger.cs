using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Core.DTO;
using Blazor.Diagrams.Core.Geometry;

namespace AzureDesignStudio.Core.Common;

/// <summary>What a live-plane refresh decided; the merged graph plus per-plane honesty counts.</summary>
/// <param name="Graph">Merged graph: intent nodes never moved, live truth folded in.</param>
/// <param name="Updated">Nodes whose live name/location replaced the design's (identity matched).</param>
/// <param name="Added">Ids created on the canvas from live-only resources, ordinal order.</param>
/// <param name="MissingInLive">Design ids with no live counterpart - kept on the canvas, only reported.</param>
public sealed record PlaneMergeResult(
    DiagramGraph Graph,
    int Updated,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> MissingInLive);

/// <summary>
/// Live-plane refresh (v2 plan 12.1): merge live inventory into an existing design without
/// destroying edits - the failure Eraser avoids and Cloudcraft's convert-on-edit causes.
/// Identity is the trimmed ARM id adoption stamps on <see cref="AzureNodeDto.ArmId"/>
/// (legacy designs carry it in <see cref="AzureNodeDto.Id"/>; canvas GUIDs never collide).
/// membership and links are intent and are never touched. Live-only resources are added:
/// group children continue the host frame's child grid, top-levels continue the slot grid
/// one column past the current bounding box. Design-only nodes are KEPT and only reported -
/// refresh never deletes intent, that decision belongs to reconcile [58].
/// Deterministic: additions in ordinal id order (parents prefix-precede children), same
/// inventory in any input order merges to the same graph; nothing that exists is moved.
/// The input graph is never mutated; the returned graph is a fresh copy.
/// </summary>
public static class LivePlaneMerger
{
    public static PlaneMergeResult Merge(DiagramGraph intent, IEnumerable<AdoptableResource> live,
        Func<string, string?>? iconPathByStencilKey = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(live);

        var graph = Clone(intent);

        // Identity index across both node kinds; frames additionally host children.
        // Nodes/Groups are IList<object> (polymorphic JSON); non-DTO members are passed
        // through untouched and take no part in matching.
        var byId = new Dictionary<string, AzureNodeDto>(StringComparer.OrdinalIgnoreCase);
        var frames = new Dictionary<string, AzureNodeDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes.OfType<AzureNodeDto>())
            if (Identity(node).Length > 0) byId[Identity(node)] = node;
        foreach (var frame in graph.Groups.OfType<AzureNodeDto>())
        {
            if (Identity(frame).Length == 0) continue;
            byId[Identity(frame)] = frame;
            frames[Identity(frame)] = frame;
        }

        var items = live
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Type))
            .Select(r => r with { Id = r.Id.Trim().TrimStart('/') })
            .Where(r => r.Id.Length > 0)
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).First())
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        var liveIds = new HashSet<string>(items.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        var updated = 0;
        var nextTopSlot = 0;
        var startX = TopLevelStartX(graph);

        foreach (var resource in items)
        {
            if (byId.TryGetValue(resource.Id, out var existing))
            {
                // Live plane: name and location are facts, not intent. Everything the user
                // chose (position, size, frame membership, links, type) stays.
                existing.Name = resource.Name;
                existing.Location = resource.Location ?? string.Empty;
                // Self-heal legacy designs: identity migrates onto ArmId so it survives the
                // next save/load, where Id becomes a fresh canvas GUID.
                existing.ArmId = resource.Id;
                updated++;
                continue;
            }

            var mapped = AdoptEngine.StencilKeyByArmType.TryGetValue(resource.Type, out var key);
            var typeKey = mapped ? key! : resource.Type;
            var isGroup = mapped && AdoptEngine.IsGroupStencil(typeKey);
            Point position;
            string? groupId = null;

            // Longest live-or-already-placed group-frame ancestor hosts the child; ordinal
            // order guarantees a new parent is placed before its children are visited.
            var parent = FindFrame(resource.Id, frames);
            if (parent is not null)
            {
                groupId = parent.Id;
                var siblings = graph.Nodes.OfType<AzureNodeDto>().Count(n => string.Equals(n.GroupId, parent.Id, StringComparison.OrdinalIgnoreCase))
                    + graph.Groups.OfType<AzureNodeDto>().Count(g => !ReferenceEquals(g, parent) && string.Equals(g.GroupId, parent.Id, StringComparison.OrdinalIgnoreCase));
                position = new Point(
                    parent.Position.X + AdoptEngine.ChildInset + siblings % AdoptEngine.ChildColumns * AdoptEngine.ChildStepX,
                    parent.Position.Y + AdoptEngine.ChildInset + siblings / AdoptEngine.ChildColumns * AdoptEngine.ChildStepY);
            }
            else
            {
                position = new Point(
                    startX + nextTopSlot % AdoptEngine.Columns * AdoptEngine.CellWidth,
                    nextTopSlot / AdoptEngine.Columns * AdoptEngine.CellHeight);
                nextTopSlot++;
            }

            var dto = new AzureNodeDto
            {
                Id = resource.Id,
                ArmId = resource.Id,
                TypeKey = typeKey,
                Name = resource.Name,
                Location = resource.Location ?? string.Empty,
                ImagePath = mapped
                    ? iconPathByStencilKey?.Invoke(typeKey) ?? $"assets/azure/{typeKey}.svg"
                    : string.Empty, // unknown type: canvas fallback icon, never a broken path
                GroupId = groupId ?? string.Empty,
                Position = position,
                Size = isGroup ? new Size(141, 141) : new Size(81, 81),
            };

            if (isGroup)
            {
                graph.Groups.Add(dto);
                frames[dto.Id] = dto;
            }
            else
                graph.Nodes.Add(dto);
            byId[dto.Id] = dto;
            added.Add(dto.Id);
        }

        var missing = byId.Keys.Where(id => !liveIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        return new PlaneMergeResult(graph, updated, added, missing);
    }

    /// <summary>First free column past the right edge of all top-level content; empty canvas starts at 0.</summary>
    private static double TopLevelStartX(DiagramGraph graph)
    {
        double maxRight = 0;
        foreach (var node in graph.Nodes.Concat(graph.Groups).OfType<AzureNodeDto>().Where(n => n.GroupId.Length == 0))
            maxRight = Math.Max(maxRight, node.Position.X + node.Size.Width);
        return maxRight == 0 ? 0 : Math.Floor(maxRight / AdoptEngine.CellWidth + 0.999) * AdoptEngine.CellWidth;
    }

    /// <summary>Reconcile identity: durable ArmId, falling back to Id for pre-ArmId designs.</summary>
    internal static string Identity(AzureNodeDto node) =>
        node.ArmId.Length > 0 ? node.ArmId : node.Id;

    private static AzureNodeDto? FindFrame(string id, Dictionary<string, AzureNodeDto> frames)
    {
        AzureNodeDto? best = null;
        for (var slash = id.LastIndexOf('/'); slash > 0; slash = id.LastIndexOf('/', slash - 1))
        {
            var candidate = id[..slash];
            if (candidate.Length == 0) break;
            if (frames.TryGetValue(candidate, out var frame)
                && (best is null || frame.Id.Length > best.Id.Length))
                best = frame;
        }
        return best;
    }

    private static DiagramGraph Clone(DiagramGraph source) => new()
    {
        Nodes = source.Nodes.Select(o => o is AzureNodeDto d ? Clone(d) : o).ToList(),
        Links = source.Links.ToList(),
        Groups = source.Groups.Select(o => o is AzureNodeDto d ? Clone(d) : o).ToList(),
    };

    private static AzureNodeDto Clone(AzureNodeDto n) => new()
    {
        TypeKey = n.TypeKey,
        ImagePath = n.ImagePath,
        Id = n.Id,
        ArmId = n.ArmId,
        Locked = n.Locked,
        Position = n.Position,
        Size = n.Size,
        GroupId = n.GroupId,
        Name = n.Name,
        Location = n.Location,
        UseResourceGroupLocation = n.UseResourceGroupLocation,
    };
}
