using System.Globalization;
using AzureDesignStudio.Discovery.Governance;
using AzureDesignStudio.Discovery.Scan;

namespace AzureDesignStudio.Discovery.Report;

/// <summary>
/// The words for how far one governance source got in a scan (Wave B3), shared by the scan
/// drawer, the Markdown report and report.html so the three never disagree. <c>sources</c> is
/// the scan manifest's <see cref="ScanManifest.Sources"/>: null means the scan predates
/// governance collection ("not collected"), which never reads as zero findings; a source the
/// manifest does not mention is "unknown". <c>findings</c> is how many findings of that source
/// the caller shows.
/// </summary>
public static class GovernanceStatus
{
    /// <summary>The display name of a <see cref="GovernanceSources"/> key.</summary>
    public static string Label(string source) => source.ToLowerInvariant() switch
    {
        GovernanceSources.Advisor => "Advisor",
        GovernanceSources.Policy => "Policy",
        GovernanceSources.Defender => "Defender",
        _ => source,
    };

    /// <summary>One status line, e.g. "Collected: 42 findings" or "Unavailable: {reason}".</summary>
    public static string Line(IReadOnlyDictionary<string, SourceState>? sources, string source, int findings)
    {
        if (sources is null)
            return "Not collected: this scan predates governance collection";
        if (!sources.TryGetValue(source, out var state))
            return "Unknown: the scan manifest does not mention this source";

        return state.State switch
        {
            SourceState.Collected => $"Collected: {Findings(findings)}",
            SourceState.Incomplete => "Incomplete: the scan stopped before this source finished"
                + (string.IsNullOrWhiteSpace(state.Detail) ? "" : "; " + state.Detail),
            SourceState.Unavailable => "Unavailable: " + (string.IsNullOrWhiteSpace(state.Detail) ? "no reason was recorded" : state.Detail),
            SourceState.NotRequested => "Not requested for this scan",
            _ => $"Unknown: the scan recorded the state '{state.State}'",
        };
    }

    /// <summary>The short form for a summary table: the count when collected, else the state
    /// ("incomplete (3 stored)", "unavailable", "not requested", "not collected", "unknown").</summary>
    public static string Brief(IReadOnlyDictionary<string, SourceState>? sources, string source, int findings)
    {
        if (sources is null)
            return "not collected";
        if (!sources.TryGetValue(source, out var state))
            return "unknown";

        var stored = findings > 0 ? $" ({findings.ToString(CultureInfo.InvariantCulture)} stored)" : "";
        return state.State switch
        {
            SourceState.Collected => findings.ToString(CultureInfo.InvariantCulture),
            SourceState.Incomplete => "incomplete" + stored,
            SourceState.Unavailable => "unavailable" + stored,
            SourceState.NotRequested => "not requested",
            _ => "unknown",
        };
    }

    /// <summary>The note beside findings shown without status lines (the topology's badges):
    /// each source that was not collected, in its <see cref="Brief"/> form; null when every
    /// source was collected. <paramref name="findings"/> are the findings shown.</summary>
    public static string? PartialNote(IReadOnlyDictionary<string, SourceState>? sources, IReadOnlyList<GovernanceFinding> findings)
    {
        if (sources is null)
            return "Governance: not collected (this scan predates governance collection)";

        var missing = GovernanceSources.All
            .Where(s => !(sources.TryGetValue(s, out var state) && state.State == SourceState.Collected))
            .Select(s => Label(s) + ": " + Brief(sources, s, findings.Count(f => KeyOf(f.Source) == s)))
            .ToList();
        return missing.Count == 0 ? null : "Governance badges cover only collected sources - " + string.Join(" · ", missing);
    }

    /// <summary>The note for stored governance rows this build cannot read; null when there are none.</summary>
    public static string? MalformedNote(int rows) =>
        rows <= 0
            ? null
            : $"{rows.ToString(CultureInfo.InvariantCulture)} stored governance row(s) could not be read (unknown source or severity) and are not shown.";

    /// <summary>
    /// What to say above the Defender plan tiers (<see cref="ScanManifest.DefenderPlans"/>); null
    /// when the list speaks for itself. The scan keeps a subscription group's plans only when both
    /// Defender queries of that group succeeded, so the list is complete only when the Defender
    /// source was collected: only then does an empty list mean Resource Graph listed none.
    /// Otherwise the plans were not read in full and the note gives the source's status line.
    /// </summary>
    public static string? DefenderPlansNote(IReadOnlyDictionary<string, SourceState>? sources, IReadOnlyList<DefenderPlan>? plans)
    {
        if (plans is null)
            return "Defender plans were not read for this scan.";

        var collected = sources is not null
            && sources.TryGetValue(GovernanceSources.Defender, out var state)
            && state.State == SourceState.Collected;
        if (collected)
            return plans.Count == 0 ? "Resource Graph listed no Defender plans for the scanned subscriptions." : null;

        return "Defender plans were not read in full"
            + (plans.Count > 0 ? "; the plans listed are only those that were read" : "")
            + ". " + Line(sources, GovernanceSources.Defender, 0);
    }

    /// <summary>The stored source key (<see cref="GovernanceSources"/>) of a finding's source.</summary>
    public static string KeyOf(GovernanceSource source) => source switch
    {
        GovernanceSource.Advisor => GovernanceSources.Advisor,
        GovernanceSource.Policy => GovernanceSources.Policy,
        GovernanceSource.Defender => GovernanceSources.Defender,
        _ => source.ToString().ToLowerInvariant(),
    };

    /// <summary>Most severe first, then resource id (case-insensitive), source, title and record
    /// id - the order every surface lists findings in, whatever order they were stored in.</summary>
    public static IReadOnlyList<GovernanceFinding> Ordered(IEnumerable<GovernanceFinding> findings) =>
        findings
            .Where(f => f is not null)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Source)
            .ThenBy(f => f.Title, StringComparer.Ordinal)
            .ThenBy(f => f.ResourceId, StringComparer.Ordinal)
            .ThenBy(f => f.RecordId, StringComparer.Ordinal)
            .ToList();

    private static string Findings(int n) =>
        n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " finding" : " findings");
}
