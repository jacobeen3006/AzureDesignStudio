using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzureDesignStudio.Discovery.Governance;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.ResourceGraph;

namespace AzureDesignStudio.Discovery.Scan;

/// <summary>What a scan covers; written verbatim into the scan manifest (plan 7.5 item 10).
/// <paramref name="Sources"/> names the governance sources to collect as well
/// (<see cref="GovernanceSources"/>; null or empty = none). It is not part of the request key:
/// a resumed scan collects the sources it started with.</summary>
public sealed record ScanRequest(string Scope, string ScopeType, IReadOnlyList<string> SubscriptionIds, IReadOnlyList<string>? Sources = null);

/// <summary>
/// Durable scan state (plan 7.5 item 11): which query groups finished, partial failures,
/// row counts. Kill mid-scan and the next run resumes from here - finished groups never re-query.
/// </summary>
public sealed class ScanCheckpoint
{
    public string ScanId { get; set; } = Guid.NewGuid().ToString();
    /// <summary>Inventory group keys, and governance source groups as <c>"{source}:{groupKey}"</c>.</summary>
    public List<string> CompletedQueries { get; set; } = [];
    public Dictionary<string, string> FailedQueries { get; set; } = [];
    public int Rows { get; set; }
    public string? ContentHash { get; set; }
    public bool AnyTruncated { get; set; }
    /// <summary>Identity of the request this checkpoint was written for (scope type,
    /// subscription set, group size). Null on checkpoints written before it existed.</summary>
    public string? RequestKey { get; set; }

    // Governance sources (Wave B3). Each defaults to empty, so a checkpoint written before they
    // existed still loads - and resumes collecting none.

    /// <summary>The governance sources this scan collects, recorded when it starts and kept on
    /// resume.</summary>
    public List<string> RequestedSources { get; set; } = [];
    /// <summary>Findings stored per source, over its completed groups.</summary>
    public Dictionary<string, int> SourceRows { get; set; } = [];
    /// <summary><c>"{source}:{groupKey}"</c> to why that source group failed or came back
    /// truncated; removed when the group later succeeds. Never makes the scan Partial.</summary>
    public Dictionary<string, string> FailedSourceGroups { get; set; } = [];
    /// <summary>Defender for Cloud plans of the subscriptions whose Defender group completed,
    /// one per subscription and plan.</summary>
    public List<DefenderPlan> DefenderPlans { get; set; } = [];
    /// <summary>How many subscription groups the scan covers, set by every run; what
    /// <see cref="ScanManifest.Build"/> needs to tell a collected source from an incomplete one.
    /// 0 on checkpoints written before it existed.</summary>
    public int TotalGroups { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, ScanJson.Options);

    public static ScanCheckpoint? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var checkpoint = JsonSerializer.Deserialize<ScanCheckpoint>(json, ScanJson.Options);
        if (checkpoint is null)
            return null;
        // An explicit null reads as empty, like a missing member.
        checkpoint.RequestedSources ??= [];
        checkpoint.SourceRows ??= [];
        checkpoint.FailedSourceGroups ??= [];
        checkpoint.DefenderPlans ??= [];
        return checkpoint;
    }
}

/// <summary>Persisted-scan side effects; the host (Desktop) writes to the SQLite workspace.</summary>
public interface IScanSink
{
    Task ScanStartedAsync(ScanRequest request, ScanCheckpoint checkpoint, bool isResume, CancellationToken ct);
    Task PagePersistedAsync(string queryKey, IReadOnlyList<InventoryItem> page, ScanCheckpoint checkpoint, CancellationToken ct);
    Task ScanFinishedAsync(string status, ScanCheckpoint checkpoint, CancellationToken ct);

    /// <summary>One governance source group's findings (normalized, deduped), stored with the
    /// checkpoint that already names <paramref name="queryKey"/> as complete. Throwing takes
    /// that back: the group is recorded as failed and queried again by the next run.</summary>
    Task SourcePagePersistedAsync(string source, string queryKey, IReadOnlyList<GovernanceFinding> findings, ScanCheckpoint checkpoint, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Live scan telemetry for the UI (progress bar, pause button state).</summary>
public sealed record ScanProgress(int QueriesCompleted, int QueriesTotal, int Rows, string? CurrentScope);

/// <summary>Pause/resume gate; cancel flows through the caller's CancellationToken.</summary>
public sealed class ScanControl
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool Paused { get; private set; }

    public async Task PauseAsync()
    {
        if (Paused) return;
        Paused = true;
        // Awaited (not blocking): desktop calls this from the Blazor UI thread,
        // which must keep pumping while the scan parks at the gate.
        await _gate.WaitAsync().ConfigureAwait(false);
    }

    public void Resume()
    {
        if (!Paused) return;
        Paused = false;
        _gate.Release();
    }

    internal async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _gate.Release();
    }
}

/// <summary>
/// Parallel collectors over grouped subscriptions (plan 7.5). Queries are grouped (≤100 subs
/// per query, per ARG guidance), checkpointed per group, and failures are recorded per group
/// rather than killing the scan. Progress/pause/cancel are honored at group boundaries; the
/// ARG client underneath handles quota windows and 429 storms (7.1). Each requested governance
/// source (Wave B3) adds one worker per group, checkpointed the same way; a source that fails
/// is recorded in <see cref="ScanCheckpoint.FailedSourceGroups"/> and never makes the scan
/// Partial.
/// </summary>
public sealed class ScanEngine(ResourceGraphClient graph, int groupSize = 100, int maxParallel = 2)
{
    public async Task<ScanCheckpoint> RunAsync(
        ScanRequest request,
        IScanSink sink,
        ScanCheckpoint? resume = null,
        ScanControl? control = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        // A checkpoint only describes the request it was written for. Resumed against a
        // different subscription set, its "completed" groups no longer mean anything and the
        // result would be a scan row mixing two scopes - so that is refused, not attempted.
        var requestKey = RequestKeyOf(request, groupSize);
        if (resume is { RequestKey: { } savedKey } && !string.Equals(savedKey, requestKey, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The interrupted scan covered a different set of subscriptions and cannot be resumed with this selection. Start a new scan.");
        // Checked on resume too: an unknown name is the caller's mistake, never a source to skip.
        var requestedSources = SourcesOf(request.Sources);

        var checkpoint = resume ?? new ScanCheckpoint();
        checkpoint.RequestKey = requestKey;
        // Recorded when the scan starts. A resumed scan finishes the sources it started with
        // (none, for a checkpoint written before sources existed): its manifest then describes
        // one request, as its inventory does.
        checkpoint.RequestedSources = resume is null
            ? requestedSources
            : [.. GovernanceSources.All.Where(s => checkpoint.RequestedSources.Contains(s, StringComparer.OrdinalIgnoreCase))];
        // Sorted before chunking: group membership (and so each group's key) must not depend
        // on the order the caller happened to list the subscriptions in.
        var groups = request.SubscriptionIds
            .Order(StringComparer.OrdinalIgnoreCase)
            .Chunk(groupSize)
            .Select(chunk => (Key: GroupKey(chunk), Subs: chunk))
            .ToList();
        checkpoint.TotalGroups = groups.Count;

        var pending = groups.Where(g => !checkpoint.CompletedQueries.Contains(g.Key)).ToList();
        // One worker per subscription group and requested source, checkpointed as
        // "{source}:{groupKey}" next to the inventory groups.
        var pendingSources = checkpoint.RequestedSources
            .SelectMany(source => groups.Select(g => (Source: source, Key: SourceKey(source, g.Key), g.Subs)))
            .Where(w => !checkpoint.CompletedQueries.Contains(w.Key))
            .ToList();
        var totalQueries = groups.Count * (1 + checkpoint.RequestedSources.Count);
        var hashParts = new System.Collections.Concurrent.ConcurrentBag<string>();
        var slotGate = new SemaphoreSlim(maxParallel, maxParallel);
        // One group at a time marks itself complete and persists. The sink stores the rows
        // and the checkpoint together, so interleaving two groups let a checkpoint that
        // already named group A commit before A's rows did (a kill there skipped A on
        // resume and lost its resources), or an older snapshot overwrite a newer one (the
        // dropped group was queried again and its rows duplicated).
        var persistGate = new SemaphoreSlim(1, 1);
        // Failures are written under the persist gate too: a sink serializes the checkpoint
        // while it stores a page, and an entry added meanwhile broke that page ("Collection was
        // modified") - with sources running beside the inventory, a failing source could turn an
        // inventory page into a failed group and the scan into Partial.
        async Task RecordFailureAsync(Dictionary<string, string> failures, string key, string reason)
        {
            await persistGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (checkpoint) failures[key] = reason;
            }
            finally
            {
                persistGate.Release();
            }
        }
        var completed = 0;
        var controlOrNone = control ?? new ScanControl();
        await sink.ScanStartedAsync(request, checkpoint, resume is not null, ct).ConfigureAwait(false);


        var workers = pending.Select(g => Task.Run(async () =>
        {
            await slotGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await controlOrNone.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                progress?.Report(new(Volatile.Read(ref completed), totalQueries, checkpoint.Rows, g.Subs[0]));

                var result = await graph.QueryAllAsync(BuildQuery(g.Subs), ct: ct).ConfigureAwait(false);
                var items = result.Rows.Select(InventoryNormalizer.FromArgRow).ToList();

                await persistGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    lock (checkpoint)
                    {
                        checkpoint.CompletedQueries.Add(g.Key);
                        checkpoint.Rows += items.Count;
                        checkpoint.AnyTruncated |= result.Truncated;
                        checkpoint.FailedQueries.Remove(g.Key); // a retried group that now succeeded
                    }

                    try
                    {
                        // Checkpoint travels with every page: a kill between pages resumes here.
                        await sink.PagePersistedAsync(g.Key, items, checkpoint, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Not stored, so not complete: the next run must query this group again.
                        lock (checkpoint)
                        {
                            checkpoint.CompletedQueries.Remove(g.Key);
                            checkpoint.Rows -= items.Count;
                        }
                        throw;
                    }
                }
                finally
                {
                    persistGate.Release();
                }

                foreach (var i in items) hashParts.Add($"{i.Id}\u001f{i.Type}\u001f{i.Name}\u001f{i.Location}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ResourceGraphException e)
            {
                // partial failure belongs in the manifest, not in a dead scan (plan 7.5)
                await RecordFailureAsync(checkpoint.FailedQueries, g.Key, $"{e.Code}: {e.Message}").ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Transport failures, an HttpClient timeout (a TaskCanceledException the caller
                // never asked for), credential errors, a sink that could not store the page:
                // each is this group's failure. Escaping, they ended the run before
                // ScanFinishedAsync and left the scan recorded as Running forever.
                await RecordFailureAsync(checkpoint.FailedQueries, g.Key, $"{e.GetType().Name}: {e.Message}").ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Increment(ref completed);
                slotGate.Release();
            }
        })).ToList();

        // A governance source group runs and persists like an inventory group, but what goes
        // wrong with it is recorded in FailedSourceGroups only: the scan's status, rows, content
        // hash, FailedQueries and AnyTruncated describe the inventory, which sources never change.
        workers.AddRange(pendingSources.Select(w => Task.Run(async () =>
        {
            await slotGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await controlOrNone.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                progress?.Report(new(Volatile.Read(ref completed), totalQueries, checkpoint.Rows, w.Subs[0]));

                var collected = await CollectSourceAsync(w.Source, w.Subs, ct).ConfigureAwait(false);
                if (collected.TruncatedListing is { } listing)
                {
                    // Part of a listing is not stored as the whole of it: the group stays
                    // pending, so the next run reads it again.
                    await RecordFailureAsync(checkpoint.FailedSourceGroups, w.Key, $"Truncated: Resource Graph cut the {listing} listing short.").ConfigureAwait(false);
                    return;
                }

                await persistGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    int? rowsBefore;
                    List<DefenderPlan> plansBefore;
                    string? failureBefore;
                    lock (checkpoint)
                    {
                        rowsBefore = checkpoint.SourceRows.TryGetValue(w.Source, out var rows) ? rows : null;
                        plansBefore = checkpoint.DefenderPlans;
                        failureBefore = checkpoint.FailedSourceGroups.GetValueOrDefault(w.Key);

                        checkpoint.CompletedQueries.Add(w.Key);
                        checkpoint.SourceRows[w.Source] = (rowsBefore ?? 0) + collected.Findings.Count;
                        if (collected.Plans.Count > 0)
                            checkpoint.DefenderPlans = MergePlans(checkpoint.DefenderPlans, collected.Plans);
                        checkpoint.FailedSourceGroups.Remove(w.Key); // a retried group that now succeeded
                    }

                    try
                    {
                        await sink.SourcePagePersistedAsync(w.Source, w.Key, collected.Findings, checkpoint, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Not stored, so not complete: the next run must query this group again.
                        lock (checkpoint)
                        {
                            checkpoint.CompletedQueries.Remove(w.Key);
                            if (rowsBefore is { } before)
                                checkpoint.SourceRows[w.Source] = before;
                            else
                                checkpoint.SourceRows.Remove(w.Source);
                            checkpoint.DefenderPlans = plansBefore;
                            if (failureBefore is not null)
                                checkpoint.FailedSourceGroups[w.Key] = failureBefore;
                        }
                        throw;
                    }
                }
                finally
                {
                    persistGate.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ResourceGraphException e)
            {
                await RecordFailureAsync(checkpoint.FailedSourceGroups, w.Key, $"{e.Code}: {e.Message}").ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // As for an inventory group: a transport failure, a timeout the caller never
                // asked for or a sink that could not store the findings is this group's failure.
                await RecordFailureAsync(checkpoint.FailedSourceGroups, w.Key, $"{e.GetType().Name}: {e.Message}").ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Increment(ref completed);
                slotGate.Release();
            }
        })));

        var status = "Completed";
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "Canceled";
        }

        // A scan with a failed query group, or a listing Resource Graph cut short, saw only
        // part of the scope. Recorded as Completed it became the baseline later scans are
        // compared with, and every resource it missed showed up as deleted. Partial keeps the
        // data browsable while it never serves as that baseline.
        if (status == "Completed" && (checkpoint.FailedQueries.Count > 0 || checkpoint.AnyTruncated))
            status = "Partial";

        checkpoint.ContentHash = ScanHash(hashParts);
        // The terminal record is written whatever happened to the caller's token: after a
        // cancel that token is already cancelled, and a sink honouring it would never store
        // "Canceled".
        await sink.ScanFinishedAsync(status, checkpoint, CancellationToken.None).ConfigureAwait(false);
        return checkpoint;
    }

    private static string RequestKeyOf(ScanRequest request, int groupSize) =>
        "r" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            request.ScopeType + "\n" + groupSize + "\n" +
            string.Join(',', request.SubscriptionIds.Select(s => s.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal)))))[..16];

    // Subscription ids are GUIDs and compare case-insensitively; lower-cased and ordinally
    // sorted so a group keeps its key whatever casing or culture the caller runs under.
    // (For the canonical lower-case ids Azure returns this is the key it always had.)
    private static string GroupKey(string[] subs) =>
        "g" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            string.Join(',', subs.Select(s => s.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal)))))[..12];

    // Grouped query per ARG throttling guidance; ordered by id so pagination is deterministic.
    private static string BuildQuery(string[] subs)
    {
        var list = string.Join(",", subs.Select(s => $"'{s}'"));
        // managedBy is a top-level column in Resource Graph, not part of properties; without
        // it no disk could be seen as attached (InventoryNormalizer folds it into Raw). sku is
        // top-level likewise and is how retired Basic SKUs are recognised.
        return "Resources | where subscriptionId in (" + list + ") | order by id asc | project id, name, type, location, resourceGroup, subscriptionId, tags, managedBy, sku, properties";
    }

    /// <summary>The requested sources in <see cref="GovernanceSources.All"/> order, each once,
    /// matched case-insensitively; a name that is not a source is refused.</summary>
    private static List<string> SourcesOf(IReadOnlyList<string>? requested)
    {
        if (requested is null)
            return [];
        var unknown = requested.Where(s => s is null || !GovernanceSources.All.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"Unknown governance source: {string.Join(", ", unknown.Select(s => $"'{s}'"))}. Known sources: {string.Join(", ", GovernanceSources.All)}.",
                nameof(requested));
        return [.. GovernanceSources.All.Where(s => requested.Any(r => string.Equals(r.Trim(), s, StringComparison.OrdinalIgnoreCase)))];
    }

    private static string SourceKey(string source, string groupKey) => source + ":" + groupKey;

    /// <summary>One source group's findings, and for Defender its plans too; or, when Resource
    /// Graph cut one of its listings short, which listing.</summary>
    private sealed record SourceResult(IReadOnlyList<GovernanceFinding> Findings, IReadOnlyList<DefenderPlan> Plans, string? TruncatedListing);

    private async Task<SourceResult> CollectSourceAsync(string source, string[] subs, CancellationToken ct)
    {
        switch (source)
        {
            case GovernanceSources.Advisor:
            {
                var result = await graph.QueryAllAsync(GovernanceQueries.Advisor(subs), ct: ct).ConfigureAwait(false);
                return result.Truncated
                    ? Cut("Advisor recommendations")
                    : new(Findings(result, GovernanceNormalizer.FromAdvisorRow), [], null);
            }
            case GovernanceSources.Policy:
            {
                var result = await graph.QueryAllAsync(GovernanceQueries.Policy(subs), ct: ct).ConfigureAwait(false);
                return result.Truncated
                    ? Cut("policy states")
                    : new(Findings(result, GovernanceNormalizer.FromPolicyRow), [], null);
            }
            case GovernanceSources.Defender:
            {
                // Both Defender queries run in this one worker: the plans say what the
                // assessments can cover, so the group is complete only with both.
                var assessments = await graph.QueryAllAsync(GovernanceQueries.DefenderAssessments(subs), ct: ct).ConfigureAwait(false);
                if (assessments.Truncated)
                    return Cut("Defender assessments");
                var plans = await graph.QueryAllAsync(GovernanceQueries.DefenderPlans(subs), ct: ct).ConfigureAwait(false);
                if (plans.Truncated)
                    return Cut("Defender plans");
                return new(
                    Findings(assessments, GovernanceNormalizer.FromDefenderRow),
                    MergePlans([], plans.Rows.Select(GovernanceNormalizer.FromPlanRow).OfType<DefenderPlan>()),
                    null);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, "Not a governance source.");
        }

        static SourceResult Cut(string listing) => new([], [], listing);

        static IReadOnlyList<GovernanceFinding> Findings(ResourceQueryResult result, Func<JsonElement, GovernanceFinding?> normalize) =>
            GovernanceNormalizer.Dedupe(result.Rows.Select(normalize).OfType<GovernanceFinding>());
    }

    /// <summary>One plan per subscription and plan name (case-insensitive), the newest winning;
    /// ordered by subscription, then plan. A new list: the old one is kept for rollback.</summary>
    private static List<DefenderPlan> MergePlans(IEnumerable<DefenderPlan> existing, IEnumerable<DefenderPlan> newest)
    {
        var merged = new Dictionary<string, DefenderPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in existing.Concat(newest))
            merged[plan.SubscriptionId + "\u001f" + plan.Plan] = plan;
        return [.. merged.Values
            .OrderBy(p => p.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Plan, StringComparer.OrdinalIgnoreCase)];
    }

    private static string ScanHash(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001e', parts.Order(StringComparer.OrdinalIgnoreCase)))));
}

internal static class ScanJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
