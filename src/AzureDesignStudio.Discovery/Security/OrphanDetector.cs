using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Security;

public enum OrphanKind { UnattachedDisk, UnassociatedIp, NicWithoutVm, NsgUnreferenced }

/// <summary>One orphan candidate with the ARM evidence that made it a candidate - every row
/// names what the scan could see, so a human decides, the engine never deletes.</summary>
public sealed record OrphanRow(
 string Id,
 string Name,
 string Type,
 string ResourceGroup,
 string SubscriptionId,
 OrphanKind Kind,
 string Evidence);

public sealed record OrphanAudit(
 IReadOnlyList<OrphanRow> Rows,
 int ScannedResources,
 IReadOnlyList<string> Caveats);

/// <summary>
/// Orphan detection, structural half (v2 plan 7): resources still billing while attached
/// to nothing, judged ONLY from what a Reader scan's own properties carry -
/// disks without <c>managedBy</c>, public IPs with an empty or missing
/// <c>ipConfiguration</c> (prefix members excepted), NICs with no <c>virtualMachine</c>
/// reference, NSGs referenced by no scanned vnet subnet or NIC. Prefix matching over ARM
/// ids is case-insensitive. Metrics-level idleness (a disk attached but unread, an IP
/// attached but silent) needs activity data this scan never has and is a permanent caveat -
/// these rows are candidates for a human to verify, never auto-delete input.
/// </summary>
public static class OrphanDetector
{
    private static readonly string[] Caveats =
    [
        "NICs backing private endpoints or delegated services carry no virtualMachine reference - verify before treating one as orphan",
        "NSG usage is computed only from vnet subnets and NICs inside this scan - references from scopes outside the scan are invisible",
        "structural attachment only: a connected disk nobody reads or an IP nobody connects to is idle, and that needs metrics this scan never has",
    ];

    public static OrphanAudit Analyze(IReadOnlyList<InventoryItem> items)
    {
        var nsgUsed = NsgReferences(items);
        var rows = new List<OrphanRow>();

        foreach (var item in items)
        {
            var raw = item.Raw;
            switch (item.Type.ToLowerInvariant())
            {
                case "microsoft.compute/disks":
                    JudgeDisk(item, raw, rows);
                    break;
                case "microsoft.network/publicipaddresses":
                    JudgeIp(item, raw, rows);
                    break;
                case "microsoft.network/networkinterfaces":
                    JudgeNic(item, raw, rows);
                    break;
                case "microsoft.network/networksecuritygroups":
                    if (!nsgUsed.Contains(item.Id))
                        rows.Add(Row(item, OrphanKind.NsgUnreferenced,
                            "no scanned vnet subnet or NIC references this NSG (references from outside the scan scope stay invisible)"));
                    break;
            }
        }

        var distinct = rows
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        return new OrphanAudit(distinct, items.Count, Caveats);
    }

    private static void JudgeDisk(InventoryItem item, JsonElement? raw, List<OrphanRow> rows)
    {
        if (raw is not { } r) return;
        var managedBy = Str(r, "managedBy");
        if (!string.IsNullOrWhiteSpace(managedBy)) return; // attached: owned by something
        var diskState = Str(r, "diskState");
        if (string.Equals(diskState, "Attached", StringComparison.OrdinalIgnoreCase)) return; // contradiction toward attached - never claim
        rows.Add(Row(item, OrphanKind.UnattachedDisk,
            diskState is null ? "no managedBy and no diskState in the scan properties" : $"diskState: {diskState}, no managedBy"));
    }

    private static void JudgeIp(InventoryItem item, JsonElement? raw, List<OrphanRow> rows)
    {
        if (raw is not { } r) return;
        var props = r;
        if (props.TryGetProperty("publicIPPrefix", out var prefix) && prefix.ValueKind == JsonValueKind.Object
            && Str(prefix, "id") is { Length: > 0 }) return; // prefix member: the prefix is its attachment
        if (props.TryGetProperty("ipConfiguration", out var cfg))
        {
            if (cfg.ValueKind == JsonValueKind.Object && Str(cfg, "id") is { Length: > 0 }) return;
            if (cfg.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in cfg.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Object && Str(el, "id") is { Length: > 0 }) return;
                rows.Add(Row(item, OrphanKind.UnassociatedIp, "ipConfiguration present but empty - attached to nothing"));
                return;
            }
            return; // unusable shape: say nothing rather than guess
        }
        rows.Add(Row(item, OrphanKind.UnassociatedIp, "no ipConfiguration in the scan properties"));
    }

    private static void JudgeNic(InventoryItem item, JsonElement? raw, List<OrphanRow> rows)
    {
        if (raw is not { } r) return;
        var props = r;
        if (props.TryGetProperty("virtualMachine", out var vm) && vm.ValueKind == JsonValueKind.Object
            && Str(vm, "id") is { Length: > 0 }) return;
        rows.Add(Row(item, OrphanKind.NicWithoutVm,
            "no virtualMachine.id - orphan unless it backs a private endpoint or delegated service"));
    }

    /// <summary>NSG ids referenced by any scanned vnet subnet or NIC, compared case-insensitively.</summary>
    private static HashSet<string> NsgReferences(IReadOnlyList<InventoryItem> items)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            switch (item.Type.ToLowerInvariant())
            {
                case "microsoft.network/virtualnetworks":
                    if (item.Raw is { } vnet && vnet.TryGetProperty("subnets", out var subnets)
                        && subnets.ValueKind == JsonValueKind.Array)
                        foreach (var subnet in subnets.EnumerateArray())
                            CollectRef(subnet, "networkSecurityGroup", used);
                    break;
                case "microsoft.network/networkinterfaces":
                    if (item.Raw is { } nic) CollectRef(nic, "networkSecurityGroup", used);
                    break;
            }
        }
        return used;
    }

    private static void CollectRef(JsonElement node, string key, HashSet<string> into)
    {
        if (node.TryGetProperty(key, out var grp) && grp.ValueKind == JsonValueKind.Object
            && Str(grp, "id") is { Length: > 0 } id)
            into.Add(id);
    }

    private static OrphanRow Row(InventoryItem item, OrphanKind kind, string evidence) =>
        new(item.Id, item.Name, item.Type, item.ResourceGroup, item.SubscriptionId, kind, evidence);

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
