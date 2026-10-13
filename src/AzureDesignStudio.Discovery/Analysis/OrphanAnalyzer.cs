using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Analysis;

public enum FindingKind
{
    /// <summary>Nothing references it and it references nothing useful (NIC with no VM).</summary>
    Orphan,
    /// <summary>Configured but unused (NSG with no rules, LB with no listeners).</summary>
    Idle,
    /// <summary>Its configuration points at a resource id that is not in the inventory.</summary>
    BrokenReference,
}

/// <summary>One analysis finding; resource ids point back into the snapshot rows.</summary>
public sealed record AnalysisFinding(string ResourceId, string ResourceType, FindingKind Kind, string Detail);

/// <summary>
/// Orphan/idle/broken-reference detection over a typeless inventory (v2 plan 7.7). Pure
/// function over snapshot rows - no network, no per-service mapping code required for the
/// generic rules; the known-type rules only read raw ARM properties.
/// </summary>
public static class OrphanAnalyzer
{
    public static IReadOnlyList<AnalysisFinding> Analyze(IReadOnlyList<InventoryItem> items)
    {
        var byId = new HashSet<string>(items.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
        var referenced = CollectReferencedIds(items, byId);
        var findings = new List<AnalysisFinding>();

        foreach (var item in items)
        {
            var props = item.Raw;
            switch (item.Type.ToLowerInvariant())
            {
                case "microsoft.network/networkinterfaces"
                    when props is null || !props.Value.TryGetProperty("virtualMachine", out _):
                    findings.Add(new(item.Id, item.Type, FindingKind.Orphan,
                        "network interface is not attached to any virtual machine"));
                    break;

                case "microsoft.compute/disks"
                    when props is null || !props.Value.TryGetProperty("managedBy", out _) ||
                         (props.Value.TryGetProperty("diskState", out var ds) && ds.GetString()?.Equals("Unattached", StringComparison.OrdinalIgnoreCase) == true):
                    findings.Add(new(item.Id, item.Type, FindingKind.Orphan,
                        "managed disk is unattached (paying for storage nothing mounts)"));
                    break;

                case "microsoft.network/publicipaddresses"
                    when props is null || (!HasActiveConfig(props.Value, "ipConfiguration") && !HasActiveConfig(props.Value, "ipConfigurations")
                         && !props.Value.TryGetProperty("publicIPPrefix", out _) && !HasActiveConfig(props.Value, "natGateway")):
                    findings.Add(new(item.Id, item.Type, FindingKind.Orphan,
                        "public IP is not assigned to any resource"));
                    break;

                case "microsoft.network/networksecuritygroups"
                    when !referenced.Contains(item.Id):
                    findings.Add(new(item.Id, item.Type, FindingKind.Orphan,
                        "NSG is not associated with any subnet or network interface"));
                    break;

                case "microsoft.compute/loadbalancers":
                    if (props is null || !HasActiveConfig(props.Value, "loadBalancingRules") && !HasActiveConfig(props.Value, "frontendIPConfigurations"))
                        findings.Add(new(item.Id, item.Type, FindingKind.Idle,
                            "load balancer has no listeners or frontends"));
                    break;
            }

            // NSG with zero rules is idle regardless of association
            if (item.Type.Equals("microsoft.network/networksecuritygroups", StringComparison.OrdinalIgnoreCase)
                && props is not null && !HasActiveConfig(props.Value, "securityRules"))
                findings.Add(new(item.Id, item.Type, FindingKind.Idle, "NSG has no security rules (allow-all if attached)"));

            // vnet peering whose remote side is absent from the inventory is broken
            if (item.Type.Equals("microsoft.network/virtualnetworks", StringComparison.OrdinalIgnoreCase)
                && props is not null && props.Value.TryGetProperty("subnets", out var subnets) && subnets.ValueKind == JsonValueKind.Array)
                foreach (var subnet in subnets.EnumerateArray())
                    foreach (var peering in EnumeratePeeringTargets(subnet))
                        if (!byId.Contains(peering))
                            findings.Add(new(item.Id, item.Type, FindingKind.BrokenReference,
                                $"peered virtual network {peering} is not present in the inventory"));
        }

        return findings;
    }

    /// <summary>Ids referenced from raw properties (ARM id strings inside any JSON string value).</summary>
    private static HashSet<string> CollectReferencedIds(IReadOnlyList<InventoryItem> items, HashSet<string> knownIds)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.Raw is null) continue;
            foreach (var candidate in EnumerateArmIdStrings(item.Raw.Value))
                if (knownIds.Contains(candidate) && !candidate.Equals(item.Id, StringComparison.OrdinalIgnoreCase))
                    referenced.Add(candidate);
        }
        // parent/child structure implies reference too
        foreach (var item in items)
            if (item.ParentId is { } parent && knownIds.Contains(parent))
                referenced.Add(parent);
        return referenced;
    }

    /// <summary>Every string property value that looks like an ARM resource id.</summary>
    private static IEnumerable<string> EnumerateArmIdStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                    foreach (var id in EnumerateArmIdStrings(p.Value)) yield return id;
                break;
            case JsonValueKind.Array:
                foreach (var e in element.EnumerateArray())
                    foreach (var id in EnumerateArmIdStrings(e)) yield return id;
                break;
            case JsonValueKind.String when element.GetString() is { } s
                && s.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase):
                yield return s;
                break;
        }
    }

    private static IEnumerable<string> EnumeratePeeringTargets(JsonElement subnet)
    {
        if (!subnet.TryGetProperty("virtualNetworkPeerings", out var peerings) || peerings.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var p in peerings.EnumerateArray())
        {
            // remote address space is often absent for foreign or retired peerings
            if (p.TryGetProperty("remoteVirtualNetwork", out var remote) &&
                remote.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { } rid)
                yield return rid;
        }
    }
    /// <summary>A child config that exists and is non-empty (single object or array).</summary>
    private static bool HasActiveConfig(JsonElement props, string name) =>
        props.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.Object => true,
            JsonValueKind.Null => false,
            _ => true,
        };
}
