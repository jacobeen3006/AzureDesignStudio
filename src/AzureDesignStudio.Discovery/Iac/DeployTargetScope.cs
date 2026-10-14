namespace AzureDesignStudio.Discovery.Iac;

/// <summary>
/// The approved deployment target the execution pipeline binds to (v2 plan 11.2): one of the four
/// ARM deployment scopes, fully identified. The approval identity names this scope, so every batch
/// the staged plan submits must be provably deployable at it - a scope the identity cannot name
/// (a management group the approval never mentions, a foreign subscription) is rejected before any
/// ARM round-trip, never discovered at deploy time.
/// </summary>
public sealed record DeployTargetScope
{
    public DeployScopeKind Kind { get; }
    public string SubscriptionId { get; }
    public string ResourceGroup { get; }
    public string ManagementGroupId { get; }

    private DeployTargetScope(DeployScopeKind kind, string subscriptionId, string resourceGroup, string managementGroupId)
    {
        Kind = kind;
        SubscriptionId = subscriptionId;
        ResourceGroup = resourceGroup;
        ManagementGroupId = managementGroupId;
    }

    public static DeployTargetScope ResourceGroupScope(string subscriptionId, string resourceGroup) =>
        new(DeployScopeKind.ResourceGroup, subscriptionId, resourceGroup, "");

    public static DeployTargetScope SubscriptionScope(string subscriptionId) =>
        new(DeployScopeKind.Subscription, subscriptionId, "", "");

    public static DeployTargetScope ManagementGroupScope(string managementGroupId) =>
        new(DeployScopeKind.ManagementGroup, "", "", managementGroupId);

    public static DeployTargetScope TenantScope() =>
        new(DeployScopeKind.Tenant, "", "", "");

    /// <summary>Canonical ARM scope path: what a plan hash names as "scope" and what deployment
    /// URLs are built under. Tenant is the bare root.</summary>
    public string ScopePath => Kind switch
    {
        DeployScopeKind.Tenant => "/",
        DeployScopeKind.ManagementGroup => $"/providers/Microsoft.Management/managementGroups/{ManagementGroupId}",
        DeployScopeKind.Subscription => $"/subscriptions/{SubscriptionId}",
        _ => $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}",
    };

    /// <summary>
    /// The scope path a batch of this kind is submitted under, given the approved target. Under a
    /// subscription target the resource-group batch rides the subscription endpoint: its resources
    /// carry explicit resource-group scope paths that the gate proved name the approved
    /// subscription, which is exactly how ARM deploys scoped resources at subscription scope.
    /// </summary>
    public string BatchScopePath(DeployScopeKind batchKind) => (Kind, batchKind) switch
    {
        (DeployScopeKind.ResourceGroup, DeployScopeKind.Subscription) => $"/subscriptions/{SubscriptionId}",
        (DeployScopeKind.ResourceGroup, DeployScopeKind.ResourceGroup) => ScopePath,
        (DeployScopeKind.Subscription, DeployScopeKind.Subscription) => ScopePath,
        (DeployScopeKind.Subscription, DeployScopeKind.ResourceGroup) => ScopePath,
        (DeployScopeKind.ManagementGroup, DeployScopeKind.ManagementGroup) => ScopePath,
        (DeployScopeKind.Tenant, DeployScopeKind.Tenant) => ScopePath,
        _ => "",
    };

    /// <summary>Batch kinds the staged apply may submit for this target. Everything else is
    /// <c>MultiScopeNotDeployable</c>: the identity names no endpoint for it.</summary>
    public IReadOnlyList<DeployScopeKind> AllowedBatchKinds => Kind switch
    {
        DeployScopeKind.ResourceGroup => [DeployScopeKind.Subscription, DeployScopeKind.ResourceGroup],
        DeployScopeKind.Subscription => [DeployScopeKind.Subscription, DeployScopeKind.ResourceGroup],
        DeployScopeKind.ManagementGroup => [DeployScopeKind.ManagementGroup],
        _ => [DeployScopeKind.Tenant],
    };

    /// <summary>Validated construction from wire fields. <paramref name="error"/> names the first
    /// reason the combination cannot bind an identity; nothing half-built is ever returned.</summary>
    public static bool TryCreate(DeployScopeKind kind, string subscriptionId, string resourceGroup,
        string managementGroupId, out DeployTargetScope scope, out string error)
    {
        scope = TenantScope();
        error = "";
        switch (kind)
        {
            case DeployScopeKind.ResourceGroup:
                if (IsBlank(subscriptionId) || IsBlank(resourceGroup))
                {
                    error = "A resource-group target needs both a subscription id and a resource group.";
                    return false;
                }

                scope = ResourceGroupScope(subscriptionId, resourceGroup);
                return true;
            case DeployScopeKind.Subscription:
                if (IsBlank(subscriptionId))
                {
                    error = "A subscription target needs a subscription id.";
                    return false;
                }

                scope = SubscriptionScope(subscriptionId);
                return true;
            case DeployScopeKind.ManagementGroup:
                if (IsBlank(managementGroupId))
                {
                    error = "A management-group target needs a management group id.";
                    return false;
                }

                if (!IsValidManagementGroupId(managementGroupId))
                {
                    error = $"'{managementGroupId}' is not a valid management group id (letters, digits, '.', '-', '_').";
                    return false;
                }

                scope = ManagementGroupScope(managementGroupId);
                return true;
            case DeployScopeKind.Tenant:
                scope = TenantScope();
                return true;
            default:
                error = $"Unknown target scope kind '{kind}'.";
                return false;
        }
    }

    /// <summary>ARM management group name grammar (managementGroups name: alphanumeric, '.', '-', '_').</summary>
    private static bool IsValidManagementGroupId(string id) =>
        id.Length is > 0 and <= 90 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);
}
