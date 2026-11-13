using System.Text.Json;
using AzureDesignStudio.Discovery.Arm;

namespace AzureDesignStudio.Discovery.Iac;

/// <summary>
/// Runs a deployment-stacks what-if: PUT a temporary <c>deploymentStacksWhatIfResults</c> resource,
/// follow its LRO, read the resource-level changes, then delete the result (best effort - it
/// expires after <see cref="StackWhatIf.RetentionInterval"/> anyway). Nothing is deployed.
/// </summary>
public sealed class StackWhatIfClient(ArmTransport transport)
{
    public async Task<StackWhatIfSummary> RunAsync(string subscriptionId, string resourceGroupName,
        string stackName, string stackBodyJson, string planHash, CancellationToken ct = default)
    {
        var resultName = StackWhatIf.ResultName(stackName, planHash); // also validates the stack name
        var scope = $"{transport.Endpoints.ManagementEndpoint}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
            + "/providers/Microsoft.Resources";
        var stackId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
            + $"/providers/Microsoft.Resources/deploymentStacks/{stackName}";
        var resultUrl = $"{scope}/deploymentStacksWhatIfResults/{resultName}?api-version={StackWhatIf.ApiVersion}";

        var request = StackWhatIf.BuildRequest(stackId, stackBodyJson);
        var body = await transport.PutAsyncPolled(resultUrl, request, maxPolls: 60, ct).ConfigureAwait(false);
        try
        {
            if (!IsComplete(body))
                body = await transport.GetAsync(resultUrl, ct).ConfigureAwait(false);
            return StackWhatIf.Parse(body);
        }
        finally
        {
            try
            {
                await transport.DeleteAsync(resultUrl, ct).ConfigureAwait(false);
            }
            catch
            {
                // The result expires on its own; a failed clean-up must not fail the preview.
            }
        }
    }

    private static bool IsComplete(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("properties", out var props)
                && props.ValueKind == JsonValueKind.Object
                && (props.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object
                    || props.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
