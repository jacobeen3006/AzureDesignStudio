using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.ResourceGraph;

namespace AzureDesignStudio.Discovery.Scan;

/// <summary>What a scan covers; written verbatim into the scan manifest (plan 7.5 item 10).</summary>
public sealed record ScanRequest(string Scope, string ScopeType, IReadOnlyList<string> SubscriptionIds);

/// <summary>
/// Durable scan state (plan 7.5 item 11): which query groups finished, partial failures,
/// row counts. Kill mid-scan and the next run resumes from here - finished groups never re-query.
/// </summary>
public sealed class ScanCheckpoint
{
    public string ScanId { get; set; } = Guid.NewGuid().ToString();
    public List<string> CompletedQueries { get; set; } = [];
    public Dictionary<string, string> FailedQueries { get; set; } = [];
    public int Rows { get; set; }
    public string? ContentHash { get; set; }
    public bool AnyTruncated { get; set; }
    /// <summary>Identity of the request this checkpoint was written for (scope type,
    /// subscription set, group size). Null on checkpoints written before it existed.</summary>
    public string? RequestKey { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, ScanJson.Options);
    public static ScanCheckpoint? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ScanCheckpoint>(json, ScanJson.Options);
}

/// <summary>Persisted-scan side effects; the host (Desktop) writes to the SQLite workspace.</summary>
public interface IScanSink
{
    Task ScanStartedAsync(ScanRequest request, ScanCheckpoint checkpoint, bool isResume, CancellationToken ct);
    Task PagePersistedAsync(string queryKey, IReadOnlyList<InventoryItem> page, ScanCheckpoint checkpoint, CancellationToken ct);
    Task ScanFinishedAsync(string status, ScanCheckpoint checkpoint, CancellationToken ct);
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
/// ARG client underneath handles quota windows and 429 storms (7.1).
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

        var checkpoint = resume ?? new ScanCheckpoint();
        checkpoint.RequestKey = requestKey;
        // Sorted before chunking: group membership (and so each group's key) must not depend
        // on the order the caller happened to list the subscriptions in.
        var groups = request.SubscriptionIds
            .Order(StringComparer.OrdinalIgnoreCase)
            .Chunk(groupSize)
            .Select(chunk => (Key: GroupKey(chunk), Subs: chunk))
            .ToList();

        var pending = groups.Where(g => !checkpoint.CompletedQueries.Contains(g.Key)).ToList();
        var hashParts = new System.Collections.Concurrent.ConcurrentBag<string>();
        var slotGate = new SemaphoreSlim(maxParallel, maxParallel);
        // One group at a time marks itself complete and persists. The sink stores the rows
        // and the checkpoint together, so interleaving two groups let a checkpoint that
        // already named group A commit before A's rows did (a kill there skipped A on
        // resume and lost its resources), or an older snapshot overwrite a newer one (the
        // dropped group was queried again and its rows duplicated).
        var persistGate = new SemaphoreSlim(1, 1);
        var completed = 0;
        var controlOrNone = control ?? new ScanControl();
        await sink.ScanStartedAsync(request, checkpoint, resume is not null, ct).ConfigureAwait(false);


        var workers = pending.Select(g => Task.Run(async () =>
        {
            await slotGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await controlOrNone.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                progress?.Report(new(Volatile.Read(ref completed), groups.Count, checkpoint.Rows, g.Subs[0]));

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
                lock (checkpoint) checkpoint.FailedQueries[g.Key] = $"{e.Code}: {e.Message}";
            }
            catch (Exception e)
            {
                // Transport failures, an HttpClient timeout (a TaskCanceledException the caller
                // never asked for), credential errors, a sink that could not store the page:
                // each is this group's failure. Escaping, they ended the run before
                // ScanFinishedAsync and left the scan recorded as Running forever.
                lock (checkpoint) checkpoint.FailedQueries[g.Key] = $"{e.GetType().Name}: {e.Message}";
            }
            finally
            {
                Interlocked.Increment(ref completed);
                slotGate.Release();
            }
        })).ToList();

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
        // it no disk could be seen as attached (InventoryNormalizer folds it into Raw).
        return "Resources | where subscriptionId in (" + list + ") | order by id asc | project id, name, type, location, resourceGroup, subscriptionId, tags, managedBy, properties";
    }

    private static string ScanHash(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001e', parts.Order(StringComparer.OrdinalIgnoreCase)))));
}

internal static class ScanJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
