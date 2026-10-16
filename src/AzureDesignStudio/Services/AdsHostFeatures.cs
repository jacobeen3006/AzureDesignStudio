namespace AzureDesignStudio.Services;

/// <summary>
/// Optional host capabilities the shared UI can surface (desktop injects tenant scan;
/// the browser host registers the empty default). A null member = feature not present;
/// the drawer templates themselves live in the host assembly, opened through the seam.
/// </summary>
public class AdsHostFeatures
{
    /// <summary>Menu label when the host offers tenant discovery; null hides the menu item.</summary>
    public string? TenantScanLabel { get; set; }

    /// <summary>Opens the host's scan drawer using the current circuit's services.</summary>
    public Func<IServiceProvider, Task>? OpenTenantScan { get; set; }

    /// <summary>One live name-availability answer: true = available, false = taken or
    /// refused (reason in <paramref name="Detail"/>), null = not checked (why in Detail).</summary>
    public sealed record AdsNameCheckResult(bool? Available, string? Detail);

    /// <summary>Subscriptions the host can run live name checks against; null list member =
    /// the feature (and its per-row affordances) is absent.</summary>
    public Func<IServiceProvider, Task<IReadOnlyList<(string SubscriptionId, string DisplayName)>>>? ListNameCheckSubscriptions { get; set; }

    /// <summary>Live checkNameAvailability against the host's ARM transport (desktop only;
    /// the browser host leaves both delegates null and renders no affordance).</summary>
    public Func<IServiceProvider, string, string, string, string?, Task<AdsNameCheckResult>>? CheckNameAvailability { get; set; }
}
