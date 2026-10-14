using AzureDesignStudio.Discovery.Arm;

namespace AzureDesignStudio.Discovery.Iac;

/// <summary>
/// ARM execution transport for <c>Microsoft.Resources/deploymentStacks</c> (v2 plan 12.3):
/// idempotent PUT create/update following the resource LRO, and honest GET reads, over the
/// shared <see cref="ArmTransport"/> (same 429 budget, same credential, same cloud set).
/// Policy validation lives in <see cref="DeploymentStack"/> - callers build the body there
/// and send exactly those bytes here; this class never reshapes a payload.
/// </summary>
public sealed class DeploymentStackClient(ArmTransport transport)
{
    public string StackUrl(string subscriptionId, string resourceGroupName, string stackName) =>
        $"{transport.Endpoints.ManagementEndpoint}/subscriptions/{subscriptionId}/resourcegroups/{resourceGroupName}"
        + $"/providers/Microsoft.Resources/deploymentStacks/{stackName}?api-version={DeploymentStack.ApiVersion}";

    /// <summary>Creates or updates one stack; follows the ARM LRO and returns the terminal stack document.</summary>
    public Task<string> CreateOrUpdateAsync(string subscriptionId, string resourceGroupName, string stackName,
        string bodyJson, CancellationToken ct = default) =>
        transport.PutAsyncPolled(StackUrl(subscriptionId, resourceGroupName, stackName), bodyJson, maxPolls: 60, ct);

    /// <summary>GETs one stack document; a missing stack surfaces as <see cref="ResourceGraph.ResourceGraphException"/> (404).</summary>
    public Task<string> GetAsync(string subscriptionId, string resourceGroupName, string stackName, CancellationToken ct = default) =>
        transport.GetAsync(StackUrl(subscriptionId, resourceGroupName, stackName), ct);
}
