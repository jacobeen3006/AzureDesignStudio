using System.Text.Json;
using AzureDesignStudio.Discovery.ChangeFeed;
using AzureDesignStudio.Discovery.Drift;
using AzureDesignStudio.Discovery.Exposure;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.ResourceGraph;
using AzureDesignStudio.Workspace;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Desktop-side change feed service (plan 9.1): collects the ARG change history with
/// caller attribution and persists it into the workspace, so the changes view and the
/// 9.2 drift work render offline. Collection is additive and idempotent - overlapping
/// windows never duplicate rows.
/// </summary>
public sealed class ChangeFeedService(ResourceGraphClient graph, WorkspaceChanges changes, WorkspaceScans scans)
{
    private static readonly ScanDiffer Differ = new();
    public async Task<int> CollectAsync(
        IReadOnlyList<string> subscriptionIds,
        DateTimeOffset since,
        string? scanId = null,
        CancellationToken ct = default)
    {
        var entries = await new ChangeFeedCollector(graph).CollectAsync(subscriptionIds, since, ct);
        return await changes.SaveBatchAsync(scanId, entries.Select(ToRecord).ToList());
    }

    public Task<IReadOnlyList<ChangeFeedRecord>> FeedAsync(
        DateTimeOffset since, IReadOnlySet<string>? resourceIds = null, int take = 500) =>
        changes.ListSinceAsync(since, resourceIds, take);

    /// <summary>"What moved since <paramref name="since"/>" keyed by scanned resource id.</summary>
    public Task<IReadOnlyDictionary<string, ChangeFeedRecord>> MovedAsync(
        DateTimeOffset since, IEnumerable<string> resourceIds) =>
        changes.LatestByResourceAsync(since, resourceIds);

    /// <summary>Drift between two persisted scans (v2 plan 9.2), newest inventory last.</summary>
    public async Task<ScanDiff> DiffScansAsync(string scanIdBefore, string scanIdAfter)
    {
        var before = ToItems(await scans.GetSnapshotsAsync(scanIdBefore));
        var after = ToItems(await scans.GetSnapshotsAsync(scanIdAfter));
        return Differ.Diff(before, after);
    }

    /// <summary>Scan↔now: resources that moved after the scan, with the change-feed caller.</summary>
    public async Task<IReadOnlyList<LiveDriftItem>> DriftSinceScanAsync(string scanId, DateTimeOffset scannedAt)
    {
        var snapshot = ToItems(await scans.GetSnapshotsAsync(scanId));
        var feed = await changes.ListSinceAsync(scannedAt, take: int.MaxValue);
        return LiveDrift.Overlay(snapshot, feed.Select(ToEntry).ToList(), scannedAt);
    }

    /// <summary>Internet-exposure findings for a persisted scan (v2 plan 9.4).</summary>
    public async Task<IReadOnlyList<ExposureFinding>> ExposureAsync(string scanId) =>
        ExposureAnalyzer.Analyze(ToItems(await scans.GetSnapshotsAsync(scanId)));

    /// <summary>Snapshot rows → typeless inventory rows, re-run duplicates folded (last wins).</summary>
    private static IReadOnlyList<InventoryItem> ToItems(IReadOnlyList<ResourceSnapshotRecord> rows)
    {
        var byId = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (r.TagsJson is { Length: > 0 } t)
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(t) ?? [])
                    tags[k] = v;
            JsonElement? raw = null;
            if (r.PropertiesJson is { Length: > 0 } p)
                raw = JsonDocument.Parse(p).RootElement.Clone();
            byId[r.ResourceId] = new InventoryItem(r.ResourceId, r.Name, r.ResourceType,
                r.Location ?? "", r.ResourceGroup ?? "", r.SubscriptionId ?? "", tags, raw);
        }
        return byId.Values.ToList();
    }

    private static ChangeFeedEntry ToEntry(ChangeFeedRecord r) => new(
        r.ChangeKey, r.ChangeTime, r.TargetResourceId, r.TargetResourceType, r.ChangeType,
        r.CorrelationId, r.ChangedBy, r.ChangedByType, r.ClientType, r.Operation, r.ChangesCount, null);

    private static ChangeFeedRecord ToRecord(ChangeFeedEntry e) => new()
    {
        ChangeKey = e.ChangeKey,
        ChangeTime = e.ChangeTime,
        TargetResourceId = e.TargetResourceId,
        TargetResourceType = e.TargetResourceType,
        ChangeType = e.ChangeType,
        CorrelationId = e.CorrelationId,
        ChangedBy = e.ChangedBy,
        ChangedByType = e.ChangedByType,
        ClientType = e.ClientType,
        Operation = e.Operation,
        ChangesCount = e.ChangesCount,
        ChangedPropertiesJson = e.ChangedProperties?.GetRawText(),
    };
}
