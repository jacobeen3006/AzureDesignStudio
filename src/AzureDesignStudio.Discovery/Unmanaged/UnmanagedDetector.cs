using System.Text.Json;

namespace AzureDesignStudio.Discovery.Unmanaged;

public enum ManagementStatus { Managed, Unmanaged, Protected }

/// <summary>One audited resource with the signal that decided its status.</summary>
/// <param name="Reason">Stable code: `managed-by-property`, `tag:<key>`, `vmss-member`, `no-orchestration-marker`,
/// or the matched guardrail (`guardrail-scope:...` / `guardrail-tag:...` / `guardrail-type:...` / `tag:do-not-touch`).</param>
public sealed record ManagementFinding(string Id, string Type, string ResourceGroup, ManagementStatus Status, string Reason);

/// <summary>9.3 v1 verdicts. Managed resources stay silent; the two lists are what consumers flag everywhere.</summary>
public sealed record ManagementAudit(
    IReadOnlyList<ManagementFinding> Unmanaged,
    IReadOnlyList<ManagementFinding> ProtectedResources)
{
    /// <summary>Fast membership check for any consumer view (scan table, topology, export).</summary>
    public IReadOnlySet<string> UnmanagedIds { get; } =
        Unmanaged.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Unmanaged-resource detection over the typeless inventory (v2 plan 9.3, first cut):
/// an item is Managed when an orchestration marker owns it (ARM `managedBy`, orchestrator
/// tags, VMSS membership); Protected when the guardrail list claims it; Unmanaged otherwise.
/// </summary>
public static class UnmanagedDetector
{
    /// <summary>Conventional tags where a non-empty value means "an orchestrator owns this".</summary>
    private static readonly string[] OwnershipTagKeys = ["providers/managedby", "managed-by", "provisioned-by"];

    public static ManagementAudit Audit(IReadOnlyList<Inventory.InventoryItem> inventory, GuardrailPolicy policy)
    {
        var unmanaged = new List<ManagementFinding>();
        var protectedList = new List<ManagementFinding>();

        foreach (var item in inventory)
        {
            // Guardrail outranks everything: a protected resource is never reclassified by markers.
            var reason = policy.ProtectionReason(item);
            if (reason is not null)
            {
                protectedList.Add(Finding(item, ManagementStatus.Protected, reason));
                continue;
            }

            var marker = ManagedReason(item);
            if (marker is not null) continue; // managed: silent

            unmanaged.Add(Finding(item, ManagementStatus.Unmanaged, "no-orchestration-marker"));
        }

        unmanaged.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        protectedList.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return new ManagementAudit(unmanaged, protectedList);
    }

    private static string? ManagedReason(Inventory.InventoryItem item)
    {
        // ARM's own delegation pointer (disks -> VM, app-gateway subresources -> portal/aks...).
        if (item.Raw is { } raw && raw.TryGetProperty("managedBy", out var by)
            && by.ValueKind == JsonValueKind.String && by.GetString() is { Length: > 0 })
            return "managed-by-property";

        foreach (var key in OwnershipTagKeys)
            if (item.Tags.TryGetValue(key, out var value) && value.Length > 0)
                return "tag:" + key;

        // Uniform-VM scale set members: the model manages them, not per-resource IaC.
        if (item.Id.Contains("/virtualMachineScaleSets/", StringComparison.OrdinalIgnoreCase))
            return "vmss-member";

        return null;
    }

    private static ManagementFinding Finding(Inventory.InventoryItem item, ManagementStatus status, string reason) =>
        new(item.Id, item.Type, item.ResourceGroup, status, reason);
}
