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
}
