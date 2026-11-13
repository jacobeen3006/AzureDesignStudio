using System.Text.Json;
using System.Text.Json.Serialization;
using AzureDesignStudio.Discovery.Governance;

namespace AzureDesignStudio.Discovery.Scan;

/// <summary>
/// How far one governance source got in a scan. <see cref="State"/> is one of
/// <see cref="Collected"/> (every subscription group read - zero <see cref="Rows"/> then means
/// none were found), <see cref="Incomplete"/> (some groups not read yet, e.g. a cancelled scan),
/// <see cref="Unavailable"/> (a group failed or came back truncated; <see cref="Detail"/> says
/// why) or <see cref="NotRequested"/>. <see cref="Rows"/> counts the findings stored.
/// </summary>
public sealed record SourceState(string State, int Rows, string? Detail)
{
    public const string Collected = "collected", Incomplete = "incomplete", Unavailable = "unavailable", NotRequested = "not-requested";
}

/// <summary>
/// What a finished scan covered (plan 7.5 item 10), the one shape the drawer, the reports and
/// MCP read. The four original members keep their JSON names and meaning:
/// <see cref="GroupsCompleted"/> counts inventory groups only, <see cref="GroupsFailed"/> the
/// inventory groups that failed, <see cref="Rows"/> the resources stored and
/// <see cref="Truncated"/> whether Resource Graph cut an inventory listing short.
/// <see cref="Sources"/> holds one state per governance source; it is null only on a manifest
/// written before sources existed, which readers show as "not collected (scan predates
/// governance collection)" - never as zero findings. <see cref="DefenderPlans"/> is null unless
/// the scan requested Defender.
/// </summary>
public sealed class ScanManifest
{
    public int GroupsCompleted { get; init; }
    public int GroupsFailed { get; init; }
    public int Rows { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyDictionary<string, SourceState>? Sources { get; init; }
    public IReadOnlyList<DefenderPlan>? DefenderPlans { get; init; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The manifest of a scan that ended with <paramref name="status"/>, from its checkpoint.
    /// <paramref name="totalGroups"/> is how many subscription groups the scan covers
    /// (<see cref="ScanCheckpoint.TotalGroups"/>). Per requested source: any failed group makes
    /// it unavailable (the reason of the first failed group by key); else every group read makes
    /// it collected; else it is incomplete.
    /// </summary>
    public static ScanManifest Build(string status, ScanCheckpoint checkpoint, int totalGroups)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var requested = new HashSet<string>(checkpoint.RequestedSources, StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, SourceState>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in GovernanceSources.All)
            sources[source] = requested.Contains(source)
                ? StateOf(source, status, checkpoint, totalGroups)
                : new SourceState(SourceState.NotRequested, 0, null);

        return new ScanManifest
        {
            GroupsCompleted = checkpoint.CompletedQueries.Count(k => !k.Contains(':')),
            GroupsFailed = checkpoint.FailedQueries.Count,
            Rows = checkpoint.Rows,
            Truncated = checkpoint.AnyTruncated,
            Sources = sources,
            DefenderPlans = requested.Contains(GovernanceSources.Defender) ? [.. checkpoint.DefenderPlans] : null,
        };
    }

    private static SourceState StateOf(string source, string status, ScanCheckpoint checkpoint, int totalGroups)
    {
        var prefix = source + ":";
        var rows = checkpoint.SourceRows.TryGetValue(source, out var stored) ? stored : 0;

        var failure = checkpoint.FailedSourceGroups
            .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => f.Value)
            .FirstOrDefault();
        if (failure is not null)
            return new SourceState(SourceState.Unavailable, rows, failure);

        var read = checkpoint.CompletedQueries.Count(k => k.StartsWith(prefix, StringComparison.Ordinal));
        if (read >= totalGroups)
            return new SourceState(SourceState.Collected, rows, null);

        var canceled = string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase);
        return new SourceState(SourceState.Incomplete, rows,
            $"{read} of {totalGroups} subscription groups were read{(canceled ? " before the scan was canceled" : "")}.");
    }

    /// <summary>The stored manifest, or null when there is none or it is not a manifest. A
    /// manifest written before governance sources existed reads with <see cref="Sources"/> and
    /// <see cref="DefenderPlans"/> null.</summary>
    public static ScanManifest? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        ScanManifest? read;
        try
        {
            read = JsonSerializer.Deserialize<ScanManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        if (read is null)
            return null;

        Dictionary<string, SourceState>? sources = null;
        if (read.Sources is not null)
        {
            sources = new Dictionary<string, SourceState>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, state) in read.Sources)
                if (state is { State: not null })
                    sources[name] = state;
        }

        return new ScanManifest
        {
            GroupsCompleted = read.GroupsCompleted,
            GroupsFailed = read.GroupsFailed,
            Rows = read.Rows,
            Truncated = read.Truncated,
            Sources = sources,
            DefenderPlans = read.DefenderPlans?
                .Where(p => p is { SubscriptionId: not null, Plan: not null, Tier: not null })
                .ToList(),
        };
    }

    /// <summary>camelCase JSON with null members left out.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);
}
