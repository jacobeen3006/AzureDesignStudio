namespace AzureDesignStudio.Discovery.Report;

/// <summary>
/// Framework mapping for the audit export (v2 plan 9.6). Control ids are used only
/// where they are stable and certain; everything else maps to its NIST 800-53
/// control name. Unknown finding kinds export as UNMAPPED rather than a guess.
/// </summary>
public sealed record ControlMapping(string Framework, string Control, string Title);

public static class ControlsMap
{
    private static readonly Dictionary<string, ControlMapping[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vm-public-ip"] =
        [
            new("NIST 800-53", "AC-17", "Remote Access"),
            new("NIST 800-53", "CM-8", "System Component Inventory"),
        ],
        ["lb-frontend-public"] = [new("NIST 800-53", "AC-4", "Information Flow Enforcement")],
        ["nsg-open-all-inbound"] =
        [
            new("CIS Azure v2.0", "3.1", "No unrestricted inbound to the internet"),
            new("NIST 800-53", "AC-4", "Information Flow Enforcement"),
        ],
        ["nsg-open-admin-ports"] =
        [
            new("CIS Azure v2.0", "3.1", "RDP access from the Internet is disabled"),
            new("CIS Azure v2.0", "3.2", "SSH access from the Internet is disabled"),
            new("NIST 800-53", "AC-17", "Remote Access"),
        ],
        ["paas-public-endpoint"] = [new("NIST 800-53", "AC-4", "Information Flow Enforcement")],
        ["pe-pending-approval"] = [new("NIST 800-53", "CM-8", "System Component Inventory")],
        ["sql-firewall-all"] = [new("NIST 800-53", "AC-3", "Access Enforcement")],
        ["unmanaged"] = [new("NIST 800-53", "CM-8", "Unauthorized system components")],
        ["protected"] = [new("NIST 800-53", "CM-2", "Baseline configuration")],
        ["drift-New"] = [new("NIST 800-53", "SI-4", "System monitoring - new component")],
        ["drift-Modified"] = [new("NIST 800-53", "CM-3", "Configuration change control")],
        ["drift-Deleted"] = [new("NIST 800-53", "SI-4", "System monitoring - component removed")],
    };

    /// <summary>All framework rows for one finding kind; unknown kinds map once as UNMAPPED.</summary>
    public static IReadOnlyList<ControlMapping> For(string kind) =>
        Map.TryGetValue(kind, out var rows) ? rows : [new ControlMapping("UNMAPPED", "-", kind)];
}
