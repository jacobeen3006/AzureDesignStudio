using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.ResourceGraph;

namespace AzureDesignStudio.Discovery.Preflight;

/// <summary>
/// One probed data source (v2 plan 7.2): availability measured, not assumed - a missing
/// permission disables exactly its features with the role to request, never the whole app.
/// </summary>
public sealed record DataSourceProbe(
    string Source,
    string DisplayName,
    string RequiredRole,
    string[] Unlocks,
    ProbeStatus Status,
    string Detail);

public enum ProbeStatus { Available, AvailableWithCaveat, NotAvailable }

/// <summary>
/// Runs every probe once, in parallel, without retry pressure (preflight must stay polite).
/// Result feeds the Role → unlocked-features matrix in the UI.
/// </summary>
public sealed class PermissionPreflight(ArmTransport transport, ResourceGraphClient graph)
{
    public async Task<IReadOnlyList<DataSourceProbe>> RunAsync(string sampleSubscriptionId, CancellationToken ct = default)
    {
        var ep = transport.Endpoints.ManagementEndpoint;
        var tasks = new[]
        {
            ProbeAsync("resourceGraph", "Resource Graph inventory",
                "Reader on the scanned scopes (resource group / subscription / management group)",
                ["inventory", "topology", "tag & owner facets", "orphan report"],
                () => graph.QueryPageAsync("Resources | take 1", ct: ct), ct),

            ProbeAsync("subscriptions", "Subscription list",
                "Directory Readers (Entra) or Reader on each subscription",
                ["scope tree"],
                () => transport.GetAsync($"{ep}/subscriptions?$top=1&api-version={transport.Endpoints.ApiVersion}", ct), ct),

            ProbeAsync("managementGroups", "Management group hierarchy",
                "Reader on management groups (Microsoft.Management/managementGroups/read)",
                ["tenant grouping in the scope tree", "unenrolled subscription detection"],
                () => transport.GetAsync($"{ep}/providers/Microsoft.Management/managementGroups?api-version=2023-04-01", ct), ct),

            ProbeAsync("activityLog", "Activity log / change feed",
                "Monitoring Reader (Microsoft.Insights/eventtypes/values/read)",
                ["change feed + caller attribution (phase 9)"],
                () => transport.GetAsync($"{ep}/subscriptions/{sampleSubscriptionId}/providers/Microsoft.Insights/eventtypes/values?api-version=2015-04-01", ct), ct),

            ProbeAsync("costManagement", "Actual cost (Cost Management)",
                "Cost Management Reader",
                ["actual spend instead of retail estimate (phase 9)"],
                () => transport.PostAsync(
                    $"{ep}/subscriptions/{sampleSubscriptionId}/providers/Microsoft.CostManagement/query?api-version=2023-08-01",
                    """{"type":"Usage","timeframe":"TheLast7Days","dataset":{"granularity":"None","aggregation":{"TotalCost":{"name":"Cost","function":"SUM"}},"aggregationTotality":"Full"}}""",
                    ct), ct),
        };

        return await Task.WhenAll(tasks);
    }

    private static async Task<DataSourceProbe> ProbeAsync(
        string source, string displayName, string role, string[] unlocks, Func<Task> probe, CancellationToken ct)
    {
        try
        {
            await probe().ConfigureAwait(false);
            return new DataSourceProbe(source, displayName, role, unlocks, ProbeStatus.Available, "reachable");
        }
        catch (ResourceGraphException e) when (e.Throttled)
        {
            return new DataSourceProbe(source, displayName, role, unlocks, ProbeStatus.AvailableWithCaveat,
                "reachable but throttled during the probe");
        }
        catch (ResourceGraphException e)
        {
            return new DataSourceProbe(source, displayName, role, unlocks, ProbeStatus.NotAvailable, Explain(e, role));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException and not OperationCanceledException)
        {
            return new DataSourceProbe(source, displayName, role, unlocks, ProbeStatus.AvailableWithCaveat,
                $"network problem: {e.Message}");
        }
    }

    private static string Explain(ResourceGraphException e, string role) => e.StatusCode switch
    {
        401 => "token rejected - sign in again",
        403 => $"missing permission ({e.Code}) - request role: {role}",
        _ => $"{e.Code}: {e.Message}",
    };
}
