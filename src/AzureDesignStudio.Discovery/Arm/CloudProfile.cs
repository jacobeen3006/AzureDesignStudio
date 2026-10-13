namespace AzureDesignStudio.Discovery.Arm;

/// <summary>
/// One sovereign cloud as a coherent set (v2 plan 7.6): ARM endpoints + ARG endpoint/scope
/// travel together, so switching clouds can never mix a public-cloud token with a
/// government endpoint. <see cref="ArgSupported"/>=false means the graph-backed scan must
/// refuse rather than silently hit the wrong authority.
/// </summary>
public sealed record CloudProfile(string Name, ArmEndpointSet Arm, string GraphEndpoint, string GraphScope, bool ArgSupported)
{
    public static CloudProfile AzureCloud { get; } = new(
        "AzureCloud",
        ArmEndpointSet.AzureCloud,
        "https://management.azure.com",
        "https://management.azure.com/.default",
        ArgSupported: true);

    public static CloudProfile AzureUSGovernment { get; } = new(
        "AzureUSGovernment",
        ArmEndpointSet.AzureUSGovernment,
        "https://management.usgovcloudapi.net",
        "https://management.usgovcloudapi.net/.default",
        ArgSupported: true);

    /// <summary>ARG is not available in Azure China; ARM-only probes still work.</summary>
    public static CloudProfile AzureChinaCloud { get; } = new(
        "AzureChinaCloud",
        ArmEndpointSet.AzureChinaCloud,
        "https://management.chinacloudapi.cn",
        "https://management.chinacloudapi.cn/.default",
        ArgSupported: false);

    public static IReadOnlyList<CloudProfile> All { get; } = [AzureCloud, AzureUSGovernment, AzureChinaCloud];

    public static CloudProfile ForName(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? AzureCloud;

    public ResourceGraph.ResourceGraphOptions GraphOptions() => new() { Endpoint = GraphEndpoint, Scope = GraphScope };
}
