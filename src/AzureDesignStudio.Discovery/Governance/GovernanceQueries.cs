namespace AzureDesignStudio.Discovery.Governance;

/// <summary>
/// Resource Graph queries for the governance sources (Wave B3), one subscription group at a
/// time. Resource Graph pages a result on <c>$skipToken</c> only when some output column is
/// not dynamic, so every projected column is a string column or wrapped in <c>tostring</c>;
/// each query ends <c>order by id asc</c> so the pages are deterministic, and none caps its
/// rows (<c>take</c>/<c>limit</c>). Rows come back flat for <see cref="GovernanceNormalizer"/>.
/// </summary>
public static class GovernanceQueries
{
    public static string Advisor(IReadOnlyList<string> subscriptionIds) =>
        "advisorresources | where type =~ 'microsoft.advisor/recommendations'"
        + SubscriptionFilter(subscriptionIds)
        + " | project id, subscriptionId,"
        + " resourceId = tostring(properties.resourceMetadata.resourceId),"
        + " source = tostring(properties.resourceMetadata.source),"
        + " category = tostring(properties.category),"
        + " impact = tostring(properties.impact),"
        + " problem = tostring(properties.shortDescription.problem),"
        + " solution = tostring(properties.shortDescription.solution),"
        + " status = tostring(properties.recommendationStatus),"
        + " suppressions = tostring(properties.suppressionIds)"
        + " | order by id asc";

    public static string Policy(IReadOnlyList<string> subscriptionIds) =>
        "policyresources | where type =~ 'microsoft.policyinsights/policystates'"
        + SubscriptionFilter(subscriptionIds)
        + " | where tostring(properties.complianceState) =~ 'NonCompliant'"
        + " | project id, subscriptionId,"
        + " resourceId = tostring(properties.resourceId),"
        + " assignmentId = tostring(properties.policyAssignmentId),"
        + " assignmentName = tostring(properties.policyAssignmentName),"
        + " definitionId = tostring(properties.policyDefinitionId),"
        + " referenceId = tostring(properties.policyDefinitionReferenceId)"
        + " | order by id asc";

    /// <summary>The target-resource field's casing in Resource Graph is not documented, so both
    /// spellings are read.</summary>
    public static string DefenderAssessments(IReadOnlyList<string> subscriptionIds) =>
        "securityresources | where type =~ 'microsoft.security/assessments'"
        + SubscriptionFilter(subscriptionIds)
        + " | where tostring(properties.status.code) =~ 'Unhealthy'"
        + " | project id, subscriptionId,"
        + " targetId = tostring(coalesce(properties.resourceDetails.Id, properties.resourceDetails.id)),"
        + " name = tostring(properties.displayName),"
        + " severity = tostring(properties.metadata.severity)"
        + " | order by id asc";

    public static string DefenderPlans(IReadOnlyList<string> subscriptionIds) =>
        "securityresources | where type =~ 'microsoft.security/pricings'"
        + SubscriptionFilter(subscriptionIds)
        + " | project id, subscriptionId, plan = tostring(name), tier = tostring(properties.pricingTier)"
        + " | order by id asc";

    // Written exactly as ScanEngine writes its inventory query's filter.
    private static string SubscriptionFilter(IReadOnlyList<string> subscriptionIds) =>
        " | where subscriptionId in (" + string.Join(",", subscriptionIds.Select(s => $"'{s}'")) + ")";
}
