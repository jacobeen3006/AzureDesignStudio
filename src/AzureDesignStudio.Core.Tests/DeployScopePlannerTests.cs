using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 11.2 - multi-scope staging: batch ordering, scope classification, fail-closed conflicts.</summary>
public class DeployScopePlannerTests
{
    private static string Template(string resources, string parameters = "{}", string variables = "{}", string outputs = "{}") =>
        $$"""{ "contentVersion": "1.0.0.0", "parameters": {{parameters}}, "variables": {{variables}}, "resources": [{{resources}}], "outputs": {{outputs}} }""";

    private static bool Has(IReadOnlyList<ArmDiagnostic> diagnostics, string code, ArmDiagnosticSeverity severity) =>
        diagnostics.Any(d => d.Code == code && d.Severity == severity);

    private static IEnumerable<string> Keys(DeployScopePlan plan) => plan.Batches.SelectMany(b => b.ResourceKeys);

    [Fact]
    public void SingleScopeTemplate_StagesOneBatchAndDropsOutputs()
    {
        var plan = DeployScopePlanner.Create(Template(
            """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","location":"[parameters('location')]"},"""
            + """{"name":"vnet1","type":"Microsoft.Network/virtualNetworks","apiVersion":"2023-01-01"}""",
            parameters: """{ "location": { "type": "string" }, "unused": { "type": "string" } }""",
            outputs: """{ "o": { "type": "string", "value": "x" } }"""));

        Assert.True(plan.Success);
        var batch = Assert.Single(plan.Batches);
        Assert.Equal(DeployScopeKind.ResourceGroup, batch.Kind);
        Assert.Contains("microsoft.storage/storageaccounts/st1", batch.ResourceKeys);
        Assert.Contains("microsoft.network/virtualnetworks/vnet1", batch.ResourceKeys);
        Assert.Contains("\"location\"", batch.TemplateJson);
        Assert.DoesNotContain("\"unused\"", batch.TemplateJson);
        Assert.Contains("OutputsDropped", plan.Diagnostics.Select(d => d.Code));
        Assert.Contains("contentVersion", batch.TemplateJson);
    }

    [Fact]
    public void MultiScopeTemplate_OrdersBatchesTenantFirstResourceGroupLast()
    {
        var plan = DeployScopePlanner.Create(Template(
            """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","dependsOn":["[resourceId('Microsoft.Resources/resourceGroups','rg1')]"]},"""
            + """{"name":"rg1","type":"Microsoft.Resources/resourceGroups","apiVersion":"2022-09-01","location":"westeurope","dependsOn":["[resourceId('Microsoft.Management/managementGroups','g1')]"]},"""
            + """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/00000000-0000-0000-0000-000000000000"},"""
            + """{"name":"g1","type":"Microsoft.Management/managementGroups","apiVersion":"2023-04-01"}"""));

        Assert.True(plan.Success);
        Assert.Equal([DeployScopeKind.ManagementGroup, DeployScopeKind.Subscription, DeployScopeKind.ResourceGroup],
            plan.Batches.Select(b => b.Kind).ToList());

        // The explicit subscription-scope role assignment stages with the subscription batch.
        Assert.Contains("microsoft.authorization/roleassignments/ra1", plan.Batches[1].ResourceKeys);
        Assert.Contains("microsoft.resources/resourcegroups/rg1", plan.Batches[1].ResourceKeys);
        Assert.Contains("microsoft.storage/storageaccounts/st1", plan.Batches[2].ResourceKeys);
    }

    [Fact]
    public void ReverseScopeDependency_FailsClosedWithScopeOrderConflict()
    {
        // A management-group-batch resource depending on a resource-group resource cannot be
        // staged: the MG batch deploys first, so the edge could never be satisfied.
        var plan = DeployScopePlanner.Create(Template(
            """{"name":"g1","type":"Microsoft.Management/managementGroups","apiVersion":"2023-04-01","dependsOn":["[resourceId('Microsoft.Storage/storageAccounts','st1')]"]},"""
            + """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01"}"""));

        Assert.False(plan.Success);
        Assert.True(Has(plan.Diagnostics, "ScopeOrderConflict", ArmDiagnosticSeverity.Error));
        Assert.Empty(plan.Batches);
    }

    [Fact]
    public void RoleAssignmentWithoutScope_InheritsAmbientScope()
    {
        var text = Template("""{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01"}""");

        Assert.Equal(DeployScopeKind.ResourceGroup, Assert.Single(DeployScopePlanner.Create(text).Batches).Kind);
        Assert.Equal(DeployScopeKind.ManagementGroup,
            Assert.Single(DeployScopePlanner.Create(text, DeployScopeKind.ManagementGroup).Batches).Kind);
    }

    [Fact]
    public void ExpressionScope_FailsClosed()
    {
        var plan = DeployScopePlanner.Create(Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"[parameters('s')]"}""",
            parameters: """{ "s": { "type": "string" } }"""));

        Assert.False(plan.Success);
        Assert.True(Has(plan.Diagnostics, "ScopeExpressionNotPlanned", ArmDiagnosticSeverity.Error));
        Assert.Empty(plan.Batches);
    }

    [Fact]
    public void TenantRootScope_StagesAtTenant()
    {
        var plan = DeployScopePlanner.Create(Template(
            """{"name":"pa1","type":"Microsoft.Management/managementGroups","apiVersion":"2023-04-01"},"""
            + """{"name":"alias1","type":"Microsoft.Subscription/aliases","apiVersion":"2021-10-01"}"""));

        Assert.Equal([DeployScopeKind.Tenant, DeployScopeKind.ManagementGroup], plan.Batches.Select(b => b.Kind).ToList());
    }

    [Fact]
    public void CrossSubscriptionProperty_StagesAtSubscription()
    {
        var plan = DeployScopePlanner.Create(Template(
            """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","location":"westeurope","subscriptionId":"11111111-1111-1111-1111-111111111111"}"""));

        Assert.Equal(DeployScopeKind.Subscription, Assert.Single(plan.Batches).Kind);
    }

    [Fact]
    public void ParameterClosure_TravelsWithItsOwnBatch()
    {
        var plan = DeployScopePlanner.Create(Template(
            """{"name":"g1","type":"Microsoft.Management/managementGroups","apiVersion":"2023-04-01","displayName":"[parameters('mgName')]"},"""
            + """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","tags":{"t":"[variables('v')]"}}""",
            parameters: """{ "mgName": { "type": "string" }, "rgName": { "type": "string" } }""",
            variables: """{ "v": "prod", "w": "dev" }"""));

        Assert.True(plan.Success);
        var mg = plan.Batches.Single(b => b.Kind == DeployScopeKind.ManagementGroup);
        var rg = plan.Batches.Single(b => b.Kind == DeployScopeKind.ResourceGroup);
        Assert.Contains("mgName", mg.TemplateJson);
        Assert.DoesNotContain("rgName", mg.TemplateJson);
        Assert.Contains("\"v\"", rg.TemplateJson);
        Assert.DoesNotContain("\"w\"", rg.TemplateJson);
    }

    [Fact]
    public void EmptyResources_ProduceEmptySuccessfulPlan()
    {
        var plan = DeployScopePlanner.Create("""{"contentVersion":"1.0.0.0","resources":[]}""");

        Assert.True(plan.Success);
        Assert.Empty(plan.Batches);
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void InvalidInput_ProducesDiagnosticsNotExceptions()
    {
        Assert.True(Has(DeployScopePlanner.Create("not json").Diagnostics, "InvalidTemplate", ArmDiagnosticSeverity.Error));
        Assert.True(Has(DeployScopePlanner.Create("""{"contentVersion":"1.0.0.0"}""").Diagnostics, "InvalidTemplate", ArmDiagnosticSeverity.Error));
    }

    [Fact]
    public void NullOrNonObjectResourceMembers_FailClosedInsteadOfSilentlyDropping()
    {
        // A dropped member would produce a "successful" plan that deploys less than the template says.
        foreach (var text in new[] { """{"contentVersion":"1.0.0.0","resources":[null,{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01"}]}""",
                                     """{"contentVersion":"1.0.0.0","resources":["oops"]}""" })
        {
            var plan = DeployScopePlanner.Create(text);
            Assert.False(plan.Success);
            Assert.True(Has(plan.Diagnostics, "InvalidTemplate", ArmDiagnosticSeverity.Error));
            Assert.Empty(plan.Batches);
        }
    }

    [Fact]
    public void ExplicitResourceGroupScope_MustMatchTheApprovedTarget()
    {
        // A role assignment scoped to a different RG shares the ResourceGroup kind with the
        // approved target, so a kind-only gate would let it ride the approved token.
        var offTarget = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/AAA/resourceGroups/other-rg"}""");
        Assert.True(DeployScopePlanner.TryFindBlockingErrors(offTarget, "bbb", "target-rg", out var mismatch));
        Assert.Contains("ScopeTargetMismatch", mismatch);

        // Same shape when the path names the target: clears, ARM ids are case-insensitive.
        var matching = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/BBB/resourceGroups/TARGET-RG"}""");
        Assert.False(DeployScopePlanner.TryFindBlockingErrors(matching, "bbb", "target-rg", out _));

        // A deeper provider path inside the target RG still matches the target.
        var nested = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/bbb/resourceGroups/target-rg/providers/Microsoft.Network/virtualNetworks/vn1"}""");
        Assert.False(DeployScopePlanner.TryFindBlockingErrors(nested, "bbb", "target-rg", out _));

        // Unparseable literals - which the classifier's fallback reads as resource-group scope -
        // are rejected, never treated as ambient. A spoofed path hiding a second resource-group
        // behind a provider segment parses as malformed too.
        foreach (var junkScope in new[] { "/nonsense/", "garbage", "/resourceGroups/relative",
                                          "/subscriptions/bbb/resourceGroups/target-rg/providers/x/resourceGroups/y" })
        {
            var junk = Template(
                $$"""{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"{{junkScope}}"}""");
            Assert.True(DeployScopePlanner.TryFindBlockingErrors(junk, "bbb", "target-rg", out var junkMessage));
            Assert.Contains("ScopePathMalformed", junkMessage);
        }

        // Subscription-level provider paths parse as Subscription scope: beyond this pipeline.
        var subLevel = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/bbb/providers/Microsoft.Authorization/policyDefinitions/x"}""");
        Assert.True(DeployScopePlanner.TryFindBlockingErrors(subLevel, "bbb", "target-rg", out var subMessage));
        Assert.Contains("MultiScopeNotDeployable", subMessage);

        // Without a bound target identity the gate cannot prove the path, so it blocks unbound.
        Assert.True(DeployScopePlanner.TryFindBlockingErrors(offTarget, DeployScopeKind.ResourceGroup, out var unbound));
        Assert.Contains("ScopeTargetUnbound", unbound);
    }

    [Fact]
    public void NestedDeploymentRetarget_MustMatchTheApprovedTarget()
    {
        // Microsoft Learn: an RG-scoped template retargets a nested deployment to another
        // resource group in the same subscription via top-level resourceGroup - invisible
        // to kind-only classification.
        var offRg = Template(
            """{"name":"n1","type":"Microsoft.Resources/deployments","apiVersion":"2022-09-01","resourceGroup":"other-rg","properties":{"mode":"Incremental","template":{"resources":[]}}}""");
        Assert.True(DeployScopePlanner.TryFindBlockingErrors(offRg, "bbb", "target-rg", out var retarget));
        Assert.Contains("ScopeTargetMismatch", retarget);

        // Retargeting to the approved group itself is the approved scope: clears.
        var onRg = Template(
            """{"name":"n1","type":"Microsoft.Resources/deployments","apiVersion":"2022-09-01","resourceGroup":"TARGET-RG","properties":{"mode":"Incremental","template":{"resources":[]}}}""");
        Assert.False(DeployScopePlanner.TryFindBlockingErrors(onRg, "bbb", "target-rg", out _));

        // An expression target cannot be proven, so it fails closed.
        var expression = Template(
            """{"name":"n1","type":"Microsoft.Resources/deployments","apiVersion":"2022-09-01","resourceGroup":"[parameters('rg')]","properties":{"mode":"Incremental","template":{"resources":[]}}}""");
        Assert.True(DeployScopePlanner.TryFindBlockingErrors(expression, "bbb", "target-rg", out var unprovable));
        Assert.Contains("ScopeExpressionNotPlanned", unprovable);

        // Overrides hidden inside the nested template are seen too.
        var inner = Template(
            """{"name":"n1","type":"Microsoft.Resources/deployments","apiVersion":"2022-09-01","properties":{"mode":"Incremental","template":{"resources":[{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/AAA/resourceGroups/other-rg"}]}}}""");
        Assert.True(DeployScopePlanner.TryFindBlockingErrors(inner, "bbb", "target-rg", out var hidden));
        Assert.Contains("ScopeTargetMismatch", hidden);

        // An explicit subscriptionId retarget is beyond the single-scope pipeline even when
        // it names the approved subscription: conservative by design.
        var crossSub = Template(
            """{"name":"n1","type":"Microsoft.Resources/deployments","apiVersion":"2022-09-01","subscriptionId":"bbb","resourceGroup":"target-rg","properties":{"mode":"Incremental","template":{"resources":[]}}}""");
        Assert.True(DeployScopePlanner.TryFindBlockingErrors(crossSub, "bbb", "target-rg", out var subScope));
        Assert.Contains("MultiScopeNotDeployable", subScope);
    }

    [Fact]
    public void SingleScopeGate_BlocksOnlyErrorsAndBeyondScopeBatches()
    {
        // Single-RG template clears; outputs warning alone never blocks.
        Assert.False(DeployScopePlanner.TryFindBlockingErrors(
            Template("""{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01"}""",
                outputs: """{ "o": { "type": "string", "value": "x" } }"""),
            DeployScopeKind.ResourceGroup, out _));

        // A management-group batch cannot be deployed by the single-scope pipeline: blocked.
        Assert.True(DeployScopePlanner.TryFindBlockingErrors(Template(
                """{"name":"g1","type":"Microsoft.Management/managementGroups","apiVersion":"2023-04-01"}"""),
            DeployScopeKind.ResourceGroup, out var scopeMessage));
        Assert.Contains("MultiScopeNotDeployable", scopeMessage);

        // The same template is fine when the pipeline deploys at management-group scope.
        Assert.False(DeployScopePlanner.TryFindBlockingErrors(
            """{"contentVersion":"1.0.0.0","resources":[{"name":"g1","type":"Microsoft.Management/managementGroups","apiVersion":"2023-04-01"}]}""",
            DeployScopeKind.ManagementGroup, out _));

        // Staging errors block the gate too.
        Assert.True(DeployScopePlanner.TryFindBlockingErrors("not json", DeployScopeKind.ResourceGroup, out var errorMessage));
        Assert.Contains("InvalidTemplate", errorMessage);
    }

    [Fact]
    public void Staging_IsDeterministic()
    {
        var text = Template(
            """{"name":"rg1","type":"Microsoft.Resources/resourceGroups","apiVersion":"2022-09-01","location":"westeurope"},"""
            + """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","dependsOn":["[resourceId('Microsoft.Resources/resourceGroups','rg1')]"]}""",
            parameters: """{ "location": { "type": "string" } }""");

        var first = DeployScopePlanner.Create(text);
        var second = DeployScopePlanner.Create(text);
        Assert.Equal(first.Batches.Select(b => b.TemplateJson), second.Batches.Select(b => b.TemplateJson));
        Assert.Equal(first.Batches.Select(b => b.Kind), second.Batches.Select(b => b.Kind));
    }
}
