using System.Text.Json;
using AzureDesignStudio.Discovery.Arm;

namespace AzureDesignStudio.Discovery.Iac;

/// <summary>
/// Runs a deployment-stacks what-if: PUT a temporary <c>deploymentStacksWhatIfResults</c> resource,
/// follow its LRO, read the resource-level changes, then delete the result (best effort - it
/// expires after <see cref="StackWhatIf.RetentionInterval"/> anyway). Nothing is deployed.
/// </summary>
public sealed class StackWhatIfClient(ArmTransport transport, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private const int MaxPolls = 60;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

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
        try
        {
            var body = await transport.PutAsyncPolled(resultUrl, request, maxPolls: MaxPolls, ct).ConfigureAwait(false);
            // The PUT may answer 200 or 201 with provisioningState "initializing" while ARM is still
            // computing: read the result until it reaches a terminal state (bounded).
            for (var poll = 0; !IsComplete(body) && poll < MaxPolls; poll++)
            {
                await _delay(PollInterval, ct).ConfigureAwait(false);
                body = await transport.GetAsync(resultUrl, ct).ConfigureAwait(false);
            }

            return StackWhatIf.Parse(body);
        }
        finally
        {
            try
            {
                await transport.DeleteAsync(resultUrl, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The result expires on its own; a failed clean-up must not fail the preview.
            }
        }
    }

    /// <summary>True when the result reached a terminal <c>provisioningState</c>. Without a state
    /// member, a body carrying <c>changes</c> or an <c>error</c> counts as complete.</summary>
    private static bool IsComplete(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("properties", out var props)
                || props.ValueKind != JsonValueKind.Object)
                return false;
            if (props.TryGetProperty("provisioningState", out var state) && state.ValueKind == JsonValueKind.String)
                return state.GetString() is { } s
                    && (s.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
                        || s.Equals("failed", StringComparison.OrdinalIgnoreCase)
                        || s.Equals("canceled", StringComparison.OrdinalIgnoreCase));
            return props.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object
                || props.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
