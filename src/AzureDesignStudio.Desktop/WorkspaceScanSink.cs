using System.Text.Json;
using System.Text.Json.Serialization;
using AzureDesignStudio.Discovery.Governance;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Scan;
using AzureDesignStudio.Workspace;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Workspace-backed scan sink (plan 7.5): pages land as snapshots + durable checkpoint in
/// SQLite; the finished scan keeps a manifest (scope, group counts, partial failures) so
/// history answers "what did this snapshot cover, and what was missing". Governance source
/// groups (Wave B3) land the same way, as findings stored with the scan.
/// </summary>
public sealed class WorkspaceScanSink(WorkspaceDb db) : IScanSink
{
    private readonly WorkspaceScans _scans = new(db);

    public string? ScanId { get; private set; }

    public async Task ScanStartedAsync(ScanRequest request, ScanCheckpoint checkpoint, bool isResume, CancellationToken ct)
    {
        var existing = isResume ? await _scans.GetResumableAsync() : null;
        var scan = await _scans.StartAsync(request.Scope, request.ScopeType, existing?.Id, checkpoint.ToJson());
        ScanId = scan.Id;
    }

    public async Task PagePersistedAsync(string queryKey, IReadOnlyList<InventoryItem> page, ScanCheckpoint checkpoint, CancellationToken ct)
    {
        var rows = page.Select(i => new InventorySnapshotRow(
            i.Id, i.Name, i.Type, i.Location, i.ResourceGroup, i.SubscriptionId,
            i.Tags.Count == 0 ? null : JsonSerializer.Serialize(i.Tags, JsonOptions),
            i.Raw?.GetRawText())).ToList();
        await _scans.PersistPageAsync(ScanId!, rows, checkpoint.ToJson());
    }

    public async Task SourcePagePersistedAsync(string source, string queryKey, IReadOnlyList<GovernanceFinding> findings, ScanCheckpoint checkpoint, CancellationToken ct)
    {
        // Enum values by name: the workspace stores plain strings, never Discovery types.
        var rows = findings.Select(f => new GovernanceFindingRow(
            f.Source.ToString(), f.RecordId, f.ResourceId, f.SubscriptionId, f.Severity.ToString(),
            f.Category, f.Title, f.Detail, f.PolicyAssignmentId, f.PolicyDefinitionId)).ToList();
        await _scans.PersistSourcePageAsync(ScanId!, rows, checkpoint.ToJson(), ct);
    }

    public async Task ScanFinishedAsync(string status, ScanCheckpoint checkpoint, CancellationToken ct)
    {
        // TotalGroups is set by the engine from the request on every run, resumes included.
        var manifest = ScanManifest.Build(status, checkpoint, checkpoint.TotalGroups).ToJson();
        // The error describes the inventory only. A governance source that failed is in the
        // manifest (that source unavailable, with its reason), never in the scan's Error.
        var error = checkpoint.FailedQueries.Count == 0
            ? null
            : string.Join("; ", checkpoint.FailedQueries.Select(f => $"{f.Key}: {f.Value}"));
        await _scans.FinishAsync(ScanId!, status, checkpoint.ToJson(), manifest, error);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
