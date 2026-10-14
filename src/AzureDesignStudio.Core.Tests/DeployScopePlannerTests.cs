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
