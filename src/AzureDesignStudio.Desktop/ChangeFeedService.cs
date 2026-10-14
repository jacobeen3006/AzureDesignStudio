using AzureDesignStudio.Discovery.ChangeFeed;
using AzureDesignStudio.Discovery.ResourceGraph;
using AzureDesignStudio.Workspace;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Desktop-side change feed service (plan 9.1): collects the ARG change history with
/// caller attribution and persists it into the workspace, so the changes view and the
/// 9.2 drift work render offline. Collection is additive and idempotent - overlapping
/// windows never duplicate rows.
/// </summary>
public sealed class ChangeFeedService(ResourceGraphClient graph, WorkspaceChanges changes)
{
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
