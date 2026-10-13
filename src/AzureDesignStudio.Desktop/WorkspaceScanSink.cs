using System.Text.Json;
using System.Text.Json.Serialization;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Scan;
using AzureDesignStudio.Workspace;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Workspace-backed scan sink (plan 7.5): pages land as snapshots + durable checkpoint in
/// SQLite; the finished scan keeps a manifest (scope, group counts, partial failures) so
/// history answers "what did this snapshot cover, and what was missing".
/// </summary>
public sealed class WorkspaceScanSink(WorkspaceDb db) : IScanSink
{
    private readonly WorkspaceScans _scans = new(db);

    public string? ScanId { get; private set; }

    public async Task ScanStartedAsync(ScanRequest request, ScanCheckpoint checkpoint, bool isResume, CancellationToken ct)
    {
        var existing = isResume ? await _scans.GetResumableAsync() : null;
        var scan = await _scans.StartAsync(request.Scope, request.ScopeType, existing?.Id);
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

    public async Task ScanFinishedAsync(string status, ScanCheckpoint checkpoint, CancellationToken ct)
    {
        var manifest = JsonSerializer.Serialize(new ScanManifestView(
            checkpoint.CompletedQueries.Count,
            checkpoint.FailedQueries.Count,
            checkpoint.Rows,
            checkpoint.AnyTruncated), JsonOptions);
        var error = checkpoint.FailedQueries.Count == 0
            ? null
            : string.Join("; ", checkpoint.FailedQueries.Select(f => $"{f.Key}: {f.Value}"));
        await _scans.FinishAsync(ScanId!, status, checkpoint.ToJson(), manifest, error);
    }

    private sealed record ScanManifestView(int GroupsCompleted, int GroupsFailed, int Rows, bool Truncated);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
