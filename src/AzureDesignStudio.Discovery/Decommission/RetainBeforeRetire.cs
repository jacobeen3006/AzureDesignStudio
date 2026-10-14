using System.Text.Json;
using AzureDesignStudio.Discovery.Analysis;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Unmanaged;

namespace AzureDesignStudio.Discovery.Decommission;

/// <summary>One retain-before-retire line [66]: what dies, whether a copy exists anywhere,
/// and what to capture before the delete.</summary>
/// <param name="BackupCovered">True/false only when backup evidence was supplied; null means unknown.</param>
/// <param name="ConfigExportJson">ARM-shaped evidence copy of the scanned row (evidence, not a
/// deployable template - the inventory columns carry no apiVersion/sku/kind).</param>
/// <param name="DataLossKinds">Irreversibility codes from the planner's impact rows (same oracle,
/// so a manifest can never promise less than the refusal engine would refuse).</param>
/// <param name="DiagnosticsSources">For a workspace candidate: resources streaming logs into it -
/// their data must be exported before the workspace dies.</param>
public sealed record RetentionRow(
    string ResourceId,
    string Name,
    string Type,
    bool? BackupCovered,
    string? ConfigExportJson,
    IReadOnlyList<string> DataLossKinds,
    IReadOnlyList<string> DiagnosticsSources,
    bool NeedsExport);

public sealed record RetentionManifest(IReadOnlyList<RetentionRow> Rows, IReadOnlyList<string> Caveats);

/// <summary>[66] Decommissioning without an evidence copy is how data gets lost. The manifest
/// flags every candidate without backup coverage and carries the configuration copy plus the
/// irreversibility and diagnostics notes the operator needs before the first delete.</summary>
public static class RetainBeforeRetire
{
    private static readonly JsonSerializerOptions ConfigJson = new() { WriteIndented = true };

    public static RetentionManifest Build(
        IReadOnlyList<InventoryItem> inventory,
        IEnumerable<string> candidateIds,
        IReadOnlyList<BackupProtectionItem>? backupItems,
        GuardrailPolicy? guardrails = null,
        IReadOnlyList<ArmLock>? locks = null,
        IReadOnlyList<ScopeRoleAssignment>? roleAssignments = null)
    {
        var caveats = new List<string>();
        if (backupItems is null)
            caveats.Add("backup-coverage-unknown: no backup evidence supplied; NeedsExport may under-report covered resources");

        // Fold the planner's destruction facts in: the manifest and the refusal oracle never diverge.
        var plan = DecommissionPlanner.Plan(inventory, candidateIds,
            guardrails ?? GuardrailPolicy.Empty, locks ?? [], roleAssignments ?? [], backupItems);
        var dataLoss = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var impact in plan.Impacts)
        {
            if (!impact.Kind.EndsWith("-destroyed", StringComparison.Ordinal) &&
                impact.Kind != "keyvault-hard-delete") continue;
            if (!dataLoss.TryGetValue(impact.ResourceId, out var list))
                dataLoss[impact.ResourceId] = list = new List<string>();
            ((List<string>)list).Add(impact.Kind);
        }

        var graph = TopologyDeriver.Derive(inventory);
        var diagnostics = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind != TopologyEdgeKind.DiagnosticsTo) continue;
            var target = edge.To.Trim();
            if (!diagnostics.TryGetValue(target, out var list))
                diagnostics[target] = list = new List<string>();
            ((List<string>)list).Add(edge.From);
        }

        var known = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inventory)
            known.TryAdd(item.Id.Trim(), item);

        var rows = new List<RetentionRow>();
        foreach (var raw in candidateIds)
        {
            var id = raw.Trim();
            if (!known.TryGetValue(id, out var item))
            {
                caveats.Add($"candidate-not-in-scan: {raw}");
                continue;
            }

            bool? covered = backupItems is null ? null
                : backupItems.Any(b => string.Equals(b.ProtectedResourceId.Trim(), id, StringComparison.OrdinalIgnoreCase));
            rows.Add(new RetentionRow(
                item.Id, item.Name, item.Type, covered, ConfigExport(item),
                dataLoss.TryGetValue(id, out var kinds)
                    ? kinds.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList()
                    : Array.Empty<string>(),
                diagnostics.TryGetValue(id, out var sources)
                    ? sources.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList()
                    : Array.Empty<string>(),
                NeedsExport: covered != true));
        }

        if (rows.Count > 0)
            caveats.Add("config-export-is-evidence: scan inventory carries no apiVersion/sku/kind, so the copy records identity, tags and properties - not a redeployable template");

        rows.Sort((a, b) => string.CompareOrdinal(
            a.ResourceId.ToLowerInvariant(), b.ResourceId.ToLowerInvariant()));
        return new RetentionManifest(rows, caveats);
    }

    private static string? ConfigExport(InventoryItem item)
    {
        var doc = new Dictionary<string, object?>
        {
            ["name"] = item.Name,
            ["type"] = item.Type,
            ["location"] = item.Location,
            ["resourceGroup"] = item.ResourceGroup,
            ["subscriptionId"] = item.SubscriptionId,
            ["tags"] = item.Tags,
            ["properties"] = item.Raw,
        };
        try
        {
            return JsonSerializer.Serialize(doc, ConfigJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
