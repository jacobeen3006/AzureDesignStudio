using System.Text.Json;

namespace AzureDesignStudio.Discovery.Iac;

public enum StagedDeploymentScope
{
    Subscription,
    ResourceGroup,
    ManagementGroup,
    Tenant,
}

/// <summary>
/// The staged apply submits each scope batch at the scope it deploys: the subscription batch
/// lands under /subscriptions/{id}/providers/.../deployments, the resource-group batch under
/// the group. The monitor must therefore enumerate BOTH scopes - a subscription batch is
/// invisible in the resource-group listing, and vice versa (Microsoft Learn: deployments are
/// scoped resources; list operations return only the request's scope).
/// </summary>
public sealed record StagedDeploymentMember(string Name, StagedDeploymentScope Scope, string? ProvisioningState)
{
    /// <summary>Operations endpoint for this member at exactly the scope the deployment lives in.</summary>
    public string OperationsUrl(string managementEndpoint, string subscriptionId, string resourceGroup, string apiVersion) =>
        $"{managementEndpoint}/subscriptions/{subscriptionId}" +
        (Scope == StagedDeploymentScope.ResourceGroup ? $"/resourcegroups/{resourceGroup}" : "") +
        $"/providers/Microsoft.Resources/deployments/{Name}/operations?api-version={apiVersion}";

    /// <summary>
    /// Operations endpoint at an arbitrary canonical scope path (v2 11.2): management-group and
    /// tenant deployments live outside any subscription listing, so the monitor must read them
    /// where they were created. <paramref name="scopePath"/> is the target's canonical path;
    /// the tenant root ("/" or "") addresses the management endpoint directly.
    /// </summary>
    public string OperationsUrl(string managementEndpoint, string apiVersion, string scopePath)
    {
        var prefix = scopePath.Trim('/');
        return $"{managementEndpoint}{(prefix.Length > 0 ? "/" + prefix : "")}" +
            $"/providers/Microsoft.Resources/deployments/{Name}/operations?api-version={apiVersion}";
    }
}

public static class StagedDeploymentFamily
{
    /// <summary>
    /// Merges the two scope listings into the one staged family: only anchored ads-deployment
    /// names, deterministic order (name ordinal-ignoring-case, then scope). Same name at both
    /// scopes stays two members - they are distinct ARM deployments.
    /// </summary>
    public static IReadOnlyList<StagedDeploymentMember> Collect(
        IEnumerable<JsonElement> subscriptionScopeEntries, IEnumerable<JsonElement> resourceGroupScopeEntries) =>
        Collect(
        [
            (StagedDeploymentScope.Subscription, subscriptionScopeEntries),
            (StagedDeploymentScope.ResourceGroup, resourceGroupScopeEntries),
        ]);

    /// <summary>
    /// Merges one listing per scope the target deploys under (v2 11.2). Anchoring, ordering and
    /// member identity work exactly as for the legacy two-scope overload.
    /// </summary>
    public static IReadOnlyList<StagedDeploymentMember> Collect(
        IEnumerable<(StagedDeploymentScope Scope, IEnumerable<JsonElement> Entries)> listings)
    {
        var members = listings.SelectMany(l => Read(l.Entries, l.Scope))
            .Distinct()
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Scope)
            .ToList();

        return members;
    }

    /// <summary>Folded provisioning state over the whole family (worst state wins).</summary>
    public static string Fold(IReadOnlyList<StagedDeploymentMember> family) =>
        DeploymentStatusFolding.Fold(family.Select(m => m.ProvisioningState));

    private static IEnumerable<StagedDeploymentMember> Read(IEnumerable<JsonElement> entries, StagedDeploymentScope scope)
    {
        foreach (var entry in entries)
        {
            if (!entry.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var name = nameElement.GetString();
            if (!DeploymentStatusFolding.IsAdsDeployment(name))
            {
                continue;
            }

            string? state = null;
            if (entry.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("provisioningState", out var stateElement)
                && stateElement.ValueKind == JsonValueKind.String)
            {
                state = stateElement.GetString();
            }

            yield return new StagedDeploymentMember(name!, scope, state);
        }
    }
}
