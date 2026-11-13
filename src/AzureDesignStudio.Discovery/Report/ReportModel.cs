using AzureDesignStudio.Discovery.Cost;
using AzureDesignStudio.Discovery.Drift;
using AzureDesignStudio.Discovery.Exposure;
using AzureDesignStudio.Discovery.Governance;
using AzureDesignStudio.Discovery.Scan;
using AzureDesignStudio.Discovery.Unmanaged;

namespace AzureDesignStudio.Discovery.Report;

/// <summary>
/// Everything a shareable static report needs (v2 plan 9.6). Sections the run did not
/// produce stay null and their sections are simply absent - a report never lies with
/// empty tables about work that did not happen.
/// </summary>
public sealed record ReportModel(
    string Title,
    string Scope,
    DateTimeOffset AsOf,
    IReadOnlyList<ExposureFinding>? Exposure,
    ManagementAudit? Unmanaged,
    IReadOnlyList<LiveDriftItem>? Drift,
    IReadOnlyList<SpendBucket>? SpendByServiceFamily,
    SpendProvenance? SpendProvenance,
    string? TopologySvg)
{
    /// <summary>The governance findings stored with the scan (Wave B3), read together with
    /// <see cref="GovernanceSources"/>. Both null = not collected: the scan predates governance
    /// collection, which the report says instead of showing zero findings.</summary>
    public IReadOnlyList<GovernanceFinding>? Governance { get; init; }

    /// <summary>The state of each governance source from the scan manifest, keyed by
    /// <see cref="AzureDesignStudio.Discovery.Governance.GovernanceSources"/> name (case-insensitive);
    /// null = not collected.</summary>
    public IReadOnlyDictionary<string, SourceState>? GovernanceSources { get; init; }

    public int ExposureCount => Exposure?.Count ?? 0;

    public int UnmanagedCount => Unmanaged?.Unmanaged.Count ?? 0;

    public int ProtectedCount => Unmanaged?.ProtectedResources.Count ?? 0;

    public int DriftCount => Drift?.Count ?? 0;

    /// <summary>Whole-window actual spend in the provenance currency.</summary>
    public decimal? SpendTotal => SpendByServiceFamily is { Count: > 0 } s ? s.Sum(b => b.Cost) : null;
}
