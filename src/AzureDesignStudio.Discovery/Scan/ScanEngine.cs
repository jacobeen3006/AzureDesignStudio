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

    public void Pause()
    {
        if (Paused) return;
        Paused = true;
        _gate.Wait();
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
        var checkpoint = resume ?? new ScanCheckpoint();
        var groups = request.SubscriptionIds
            .Chunk(groupSize)
            .Select(chunk => (Key: GroupKey(chunk), Subs: chunk))
            .ToList();

        var pending = groups.Where(g => !checkpoint.CompletedQueries.Contains(g.Key)).ToList();
        var hashParts = new System.Collections.Concurrent.ConcurrentBag<string>();
        var slotGate = new SemaphoreSlim(maxParallel, maxParallel);
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

                lock (checkpoint)
                {
                    checkpoint.CompletedQueries.Add(g.Key);
                    checkpoint.Rows += items.Count;
                    checkpoint.AnyTruncated |= result.Truncated;
                }
                foreach (var i in items) hashParts.Add($"{i.Id}\u001f{i.Type}\u001f{i.Name}\u001f{i.Location}");

                // Checkpoint travels with every page: a kill between pages resumes here.
                await sink.PagePersistedAsync(g.Key, items, checkpoint, ct).ConfigureAwait(false);
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

        checkpoint.ContentHash = ScanHash(hashParts);
        await sink.ScanFinishedAsync(status, checkpoint, ct).ConfigureAwait(false);
        return checkpoint;
    }

    private static string GroupKey(string[] subs) =>
        "g" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join(',', subs.Order()))))[..12];

    // Grouped query per ARG throttling guidance; ordered by id so pagination is deterministic.
    private static string BuildQuery(string[] subs)
    {
        var list = string.Join(",", subs.Select(s => $"'{s}'"));
        return "Resources | where subscriptionId in (" + list + ") | order by id asc | project id, name, type, location, resourceGroup, subscriptionId, tags, properties";
    }

    private static string ScanHash(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001e', parts.Order(StringComparer.OrdinalIgnoreCase)))));
}

internal static class ScanJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
