using AzureDesignStudio.Discovery.Decommission;
using AzureDesignStudio.Discovery.Exposure;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Iac;

namespace AzureDesignStudio.Discovery.Analysis;

/// <summary>Where a proven path starts: the internet, a peered vnet, the same subnet, or a private endpoint.</summary>
public enum ReachOrigin
{
    Internet,
    PeerVnet,
    SameSubnet,
    PrivateEndpoint,
}

/// <summary>
/// One answered reachability path. <see cref="Via"/> names the resource that makes the path
/// exist (public IP, NSG, gateway, peer vnet, endpoint); <see cref="Hops"/> is the human chain
/// from origin to target; <see cref="Evidence"/> is the exposure finding kinds and edge facts
/// the claim is built from - every path is positive evidence, never an inference from proximity.
/// </summary>
public sealed record ReachPath(ReachOrigin Origin, string Via, IReadOnlyList<string> Hops, IReadOnlyList<string> Evidence);

/// <summary>
/// The [16] answer for one target. <see cref="InternetReachable"/> is true only while a proven
/// internet path exists; a false is NOT proof of closure (the caveats say which layers were
/// never inspected). Principals answer from supplied RBAC evidence only - never guessed.
/// </summary>
public sealed record ReachAnswer(
    string TargetId,
    bool InternetReachable,
    IReadOnlyList<ReachPath> Paths,
    IReadOnlyList<string> Principals,
    IReadOnlyList<string> Caveats,
    IReadOnlyList<ArmDiagnostic> Diagnostics);

/// <summary>
/// Reachability queries (v2 plan [16]): "what can reach this resource?" answered purely from a
/// scanned inventory - the proven 8.1 edge derivation (<see cref="TopologyDeriver"/>) plus the
/// exposure findings (<see cref="ExposureAnalyzer"/>) the Exposure tab already shows. Nothing is
/// re-parsed and nothing is queried: paths exist only where edges and findings prove them, and
/// every layer the snapshot cannot answer (UDRs, firewall/WAF policy, NSG deny precedence,
/// publicNetworkAccess flags, RBAC without supplied assignments) rides the caveat list instead
/// of becoming a silent verdict. Pure, deterministic, ordinal-stable.
/// </summary>
public static class ReachabilityEngine
{
    /// <summary>Finding kinds that make the resource ITSELF internet-facing.</summary>
    private static readonly HashSet<string> DirectInternet = new(StringComparer.OrdinalIgnoreCase)
    {
        "vm-public-ip", "paas-public-endpoint", "lb-frontend-public", "sql-firewall-all",
    };

    private static readonly HashSet<string> NsgOpenInternet = new(StringComparer.OrdinalIgnoreCase)
    {
        "nsg-open-all-inbound", "nsg-open-admin-ports",
    };

    private const int PeerResourcesShown = 6;
    private const int SameSubnetPeersShown = 8;

    public static ReachAnswer Analyze(
        string targetId,
        IReadOnlyList<InventoryItem> inventory,
        IReadOnlyList<ScopeRoleAssignment>? roleAssignments = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var target = (targetId ?? string.Empty).Trim().TrimEnd('/');
        var graph = TopologyDeriver.Derive(inventory);
        var findings = ExposureAnalyzer.Analyze(inventory);
        var diagnostics = new List<ArmDiagnostic>();
        var caveats = new List<string>();

        var targetNode = graph.Nodes.TryGetValue(target, out var tn) ? tn : null;
        if (target.Length == 0 || targetNode is null || targetNode.External)
        {
            diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "ReachTargetNotInScan",
                $"target '{(target.Length == 0 ? "(empty)" : target)}' is not a resource in this scan - no path can be proven from a scan that does not contain it"));
            caveats.AddRange(BaseCaveats(internetNotProven: true));
            return new ReachAnswer(target, false, [], PrincipalsOf(target, roleAssignments, caveats), caveats, diagnostics);
        }

        var findingsBy = findings
            .GroupBy(f => (f.ResourceId ?? string.Empty).Trim().TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var outgoing = graph.Edges.GroupBy(e => e.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var incoming = graph.Edges.GroupBy(e => e.To, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var paths = new List<ReachPath>();
        string Name(string id) => graph.Nodes.TryGetValue(id, out var n) ? n.Name : id[(id.LastIndexOf('/') + 1)..];

        // R1 - the target itself carries a proven public fact.
        if (findingsBy.TryGetValue(target, out var ownFindings))
            foreach (var f in ownFindings.Where(f => DirectInternet.Contains(f.Kind)).OrderBy(f => f.Kind, StringComparer.Ordinal))
                paths.Add(new ReachPath(ReachOrigin.Internet, f.ResourceId, [Name(target)], [$"{f.Kind}: {f.Detail}"]));

        // R2 - an attached public IP on the target or a NIC it uses.
        var nics = Neighbors(target, "Uses").Append(target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var nic in nics)
            foreach (var e in EdgeList(outgoing, nic).Where(e =>
                         e.Kind == TopologyEdgeKind.LoadBalances && e.Detail == "public IP"))
                if (!paths.Any(p => p.Origin == ReachOrigin.Internet &&
                                    string.Equals(p.Via, e.To, StringComparison.OrdinalIgnoreCase)))
                {
                    var hops = new List<string> { "public IP " + Name(e.To) };
                    if (!nic.Equals(target, StringComparison.OrdinalIgnoreCase)) hops.Add("NIC " + Name(nic));
                    hops.Add(Name(target));
                    paths.Add(new ReachPath(ReachOrigin.Internet, e.To, hops,
                        ["attached public IP (NIC ipConfigurations.publicIPAddress)"]));
                }

        // R3 - a public LB/app-gateway frontend whose backend pool contains the target or its NIC.
        foreach (var gw in findingsBy.Where(kv => kv.Value.Any(f =>
                     string.Equals(f.Kind, "lb-frontend-public", StringComparison.OrdinalIgnoreCase))).Select(kv => kv.Key))
            foreach (var back in EdgeList(outgoing, gw).Where(e =>
                         e.Kind == TopologyEdgeKind.LoadBalances && e.Detail != "public IP" && e.Detail != "frontend IP"))
            {
                var reachesTarget = string.Equals(back.To, target, StringComparison.OrdinalIgnoreCase) || nics.Contains(back.To);
                if (!reachesTarget) continue;
                var viaFindings = findingsBy[gw].Where(f => NsgOrFront(f.Kind)).OrderBy(f => f.Kind, StringComparer.Ordinal)
                    .Select(f => $"{f.Kind}: {f.Detail}").ToList();
                var hops = new List<string> { "public frontend " + Name(gw) };
                if (!string.Equals(back.To, target, StringComparison.OrdinalIgnoreCase)) hops.Add("backend " + Name(back.To));
                hops.Add(Name(target));
                paths.Add(new ReachPath(ReachOrigin.Internet, gw, hops, viaFindings));
            }

        // R4 - an NSG that protects the target (directly, via NIC, or via its subnet) and opens the internet.
        var protectedDomains = new List<(string Domain, string Label)> { (target, "") };
        protectedDomains.AddRange(nics.Select(n => (n, "NIC " + Name(n))));
        foreach (var subnet in SubnetsOf(nics, target))
            protectedDomains.Add((subnet, "subnet " + Name(subnet)));
        foreach (var (domain, label) in protectedDomains)
            foreach (var e in EdgeList(outgoing, domain).Where(e => e.Kind == TopologyEdgeKind.ProtectedBy))
            {
                if (!findingsBy.TryGetValue(e.To, out var nsgFindings)) continue;
                var opening = nsgFindings.Where(f => NsgOpenInternet.Contains(f.Kind))
                    .OrderBy(f => f.Kind, StringComparer.Ordinal).Select(f => $"{f.Kind}: {f.Detail}").ToList();
                if (opening.Count == 0) continue;
                var hops = new List<string> { "NSG " + Name(e.To) };
                if (label.Length > 0) hops.Add(label);
                hops.Add(Name(target));
                paths.Add(new ReachPath(ReachOrigin.Internet, e.To, hops, opening));
            }

        // R5/R6 - the target's subnets: peered vnets and same-subnet neighbours.
        var subnets = SubnetsOf(nics, target).ToList();
        foreach (var subnet in subnets)
        {
            var vnetId = graph.Nodes.TryGetValue(subnet, out var sn) ? sn.ParentId : null;
            if (vnetId is null) continue;
            foreach (var peer in EdgeList(outgoing, vnetId).Where(e => e.Kind == TopologyEdgeKind.PeersWith))
            {
                var peerId = string.Equals(peer.From, vnetId, StringComparison.OrdinalIgnoreCase) ? peer.To : peer.From;
                var hops = new List<string> { "peer vnet " + Name(peerId) };
                var evidence = new List<string>();
                if (peer.Detail is { Length: > 0 } peeringName) evidence.Add($"peering '{peeringName}'");
                if (graph.Nodes.TryGetValue(peerId, out var pn) && pn.External)
                    evidence.Add("peer-vnet-outside-scan: its resources are unknown");
                else
                {
                    var peerResources = ResourcesInVnet(graph, outgoing, peerId)
                        .OrderBy(r => r, StringComparer.Ordinal).ToList();
                    if (peerResources.Count > 0)
                    {
                        var shown = peerResources.Take(PeerResourcesShown).Select(Name).ToList();
                        if (peerResources.Count > PeerResourcesShown) shown.Add($"… {peerResources.Count} total");
                        evidence.Add("in the peer vnet: " + string.Join(", ", shown));
                    }
                    else evidence.Add("no resources of the peer vnet in this scan");
                }
                hops.Add("vnet " + Name(vnetId));
                hops.Add("subnet " + Name(subnet));
                hops.Add(Name(target));
                paths.Add(new ReachPath(ReachOrigin.PeerVnet, peerId, hops, evidence));
            }

            var neighbours = EdgeList(incoming, subnet)
                .Where(e => e.Kind == TopologyEdgeKind.InSubnet && !string.Equals(e.From, target, StringComparison.OrdinalIgnoreCase) &&
                            !(e.From.Contains("/subnets/", StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.From).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.Ordinal).ToList();
            if (neighbours.Count > 0)
            {
                var shown = neighbours.Take(SameSubnetPeersShown).Select(Name).ToList();
                if (neighbours.Count > SameSubnetPeersShown) shown.Add($"… {neighbours.Count} total");
                paths.Add(new ReachPath(ReachOrigin.SameSubnet, subnet,
                    ["subnet " + Name(subnet), Name(target)], ["shares the subnet with: " + string.Join(", ", shown)]));
            }
        }

        // R7 - private endpoints connected to the target.
        foreach (var pe in EdgeList(incoming, target).Where(e => e.Kind == TopologyEdgeKind.ConnectsTo)
                     .Select(e => e.From).Where(id => graph.Nodes.TryGetValue(id, out var n) &&
                                 n.Type.Contains("privateendpoints", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.Ordinal))
        {
            var peSubnet = EdgeList(outgoing, pe).FirstOrDefault(e => e.Kind == TopologyEdgeKind.InSubnet);
            var hops = new List<string> { "private endpoint " + Name(pe) };
            if (peSubnet is not null) hops.Add("subnet " + Name(peSubnet.To));
            hops.Add(Name(target));
            paths.Add(new ReachPath(ReachOrigin.PrivateEndpoint, pe, hops,
                ["private link (privateLinkServiceConnections.privateLinkServiceId)"]));
        }

        // App-gateway backend honesty: the deriver gives app gateways no backend edges.
        if (inventory.Any(i => i.Type.Contains("applicationgateways", StringComparison.OrdinalIgnoreCase)) &&
            findings.Any(f => string.Equals(f.Kind, "lb-frontend-public", StringComparison.OrdinalIgnoreCase) &&
                              graph.Nodes.TryGetValue((f.ResourceId ?? string.Empty).Trim().TrimEnd('/'), out var gn) &&
                              gn.Type.Contains("applicationgateways", StringComparison.OrdinalIgnoreCase)))
            caveats.Add("application-gateway-backends-not-derived: a public app-gateway frontend is reported, but which backend the front reaches is not in the derived graph");

        var internetProven = paths.Any(p => p.Origin == ReachOrigin.Internet);
        caveats.AddRange(BaseCaveats(internetNotProven: !internetProven));

        var principals = PrincipalsOf(target, roleAssignments, caveats);

        return new ReachAnswer(targetNode.Id, internetProven,
            paths.OrderBy(p => p.Origin).ThenBy(p => p.Via, StringComparer.Ordinal).ToList()
                 .DistinctBy(p => (p.Origin, p.Via, string.Join(">", p.Hops))).ToList(),
            principals, caveats, diagnostics);

        IEnumerable<string> Neighbors(string from, string kind) =>
            EdgeList(outgoing, from).Where(e => e.Kind.ToString() == kind).Select(e => e.To);
        IEnumerable<string> SubnetsOf(IEnumerable<string> nics_, string target_)
        {
            foreach (var nic in nics_)
                foreach (var s in EdgeList(outgoing, nic).Where(e => e.Kind == TopologyEdgeKind.InSubnet).Select(e => e.To))
                    yield return s;
            foreach (var s in EdgeList(outgoing, target_).Where(e => e.Kind == TopologyEdgeKind.InSubnet).Select(e => e.To))
                yield return s;
        }
    }

    private static List<TopologyEdge> EdgeList(IReadOnlyDictionary<string, List<TopologyEdge>> index, string id) =>
        index.TryGetValue(id, out var l) ? l : [];

    private static bool NsgOrFront(string kind) =>
        string.Equals(kind, "lb-frontend-public", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ResourcesInVnet(TopologyGraph graph, IReadOnlyDictionary<string, List<TopologyEdge>> outgoing, string vnetId)
    {
        foreach (var (id, node) in graph.Nodes)
        {
            if (node.External || id.Contains("/subnets/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!EdgeList(outgoing, id).Any(e => e.Kind == TopologyEdgeKind.InSubnet &&
                    graph.Nodes.TryGetValue(e.To, out var sn) &&
                    string.Equals(sn.ParentId ?? "", vnetId, StringComparison.OrdinalIgnoreCase))) continue;
            yield return id;
        }
    }

    private static IReadOnlyList<string> PrincipalsOf(string target, IReadOnlyList<ScopeRoleAssignment>? roleAssignments, List<string> caveats)
    {
        if (roleAssignments is null)
        {
            caveats.Add("role-assignments-not-supplied: which principals can read the resource was not evaluated (no RBAC evidence)");
            return [];
        }
        var hits = roleAssignments
            .Where(a => string.Equals((a.Scope ?? string.Empty).Trim().TrimEnd('/'), target, StringComparison.OrdinalIgnoreCase))
            .Select(a => $"{a.RoleName} - {a.PrincipalName}")
            .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        caveats.Add("rbac-exact-scope-only: only assignments scoped exactly to the resource are counted; parent-scope assignments were not expanded");
        return hits;
    }

    private static IEnumerable<string> BaseCaveats(bool internetNotProven)
    {
        if (internetNotProven)
            yield return "internet-path-not-proven: no attached public IP, public endpoint, public frontend or internet-open NSG was found in this scan - this is NOT proof of closure";
        yield return "public-flags-not-read: publicNetworkAccess-style flags are not part of the derived facts";
        yield return "nsg-precedence-not-simulated: NSG Allow rules are reported as found; effective deny precedence was not simulated";
        yield return "policy-layers-not-evaluated: user-defined routes, NAT gateways, Azure Firewall and WAF/application rules were not inspected";
    }
}
