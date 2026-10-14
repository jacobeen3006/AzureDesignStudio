using System;
using System.Linq;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 11.2 - the staged gate, preview, and plan identity bound to a full target
/// identity (tenant / management group / subscription / resource group).</summary>
public class DeployTargetScopeTests
{
    private static string Template(string resources) =>
        $$"""{ "contentVersion": "1.0.0.0", "parameters": {}, "variables": {}, "resources": [{{resources}}], "outputs": {} }""";

    private static bool TryApply(string template, DeployTargetScope target, out DeployScopePlan plan, out string message) =>
        DeployScopePlanner.TryStageForApply(template, target, out plan, out message);

    private static DeployTargetScope Rg(string sub = "bbb", string rg = "target-rg") => DeployTargetScope.ResourceGroupScope(sub, rg);
    private static DeployTargetScope Sub(string sub = "bbb") => DeployTargetScope.SubscriptionScope(sub);
    private static DeployTargetScope Mg(string mg = "mg1") => DeployTargetScope.ManagementGroupScope(mg);

    // ---------- target identity ----------

    [Fact]
    public void TryCreate_RejectsHalfBoundTargets_AndMalformedManagementGroups()
    {
        Assert.False(DeployTargetScope.TryCreate(DeployScopeKind.ResourceGroup, "sub", "", "", out _, out var rgError));
        Assert.Contains("resource group", rgError);
        Assert.False(DeployTargetScope.TryCreate(DeployScopeKind.Subscription, "", "", "", out _, out _));
        Assert.False(DeployTargetScope.TryCreate(DeployScopeKind.ManagementGroup, "", "", "mg 1!", out _, out var mgError));
        Assert.Contains("not a valid management group id", mgError);
        Assert.True(DeployTargetScope.TryCreate(DeployScopeKind.ManagementGroup, "", "", "mg-1.ok_x", out var mg, out _));
        Assert.Equal("/providers/Microsoft.Management/managementGroups/mg-1.ok_x", mg.ScopePath);
        Assert.True(DeployTargetScope.TryCreate(DeployScopeKind.Tenant, "", "", "", out var tenant, out _));
        Assert.Equal("/", tenant.ScopePath);
    }

    [Fact]
    public void BatchEndpoints_SubscriptionTargetRidesRgBatch_TenantIsRootOnly()
    {
        Assert.Equal("/subscriptions/bbb", Sub().BatchScopePath(DeployScopeKind.ResourceGroup));
        Assert.Equal("/subscriptions/bbb/resourceGroups/target-rg", Rg().BatchScopePath(DeployScopeKind.ResourceGroup));
        Assert.Equal("/subscriptions/bbb", Rg().BatchScopePath(DeployScopeKind.Subscription));
        Assert.Equal("/providers/Microsoft.Management/managementGroups/mg1", Mg().BatchScopePath(DeployScopeKind.ManagementGroup));
        Assert.Equal("/", DeployTargetScope.TenantScope().BatchScopePath(DeployScopeKind.Tenant));
        Assert.Equal("", Mg().BatchScopePath(DeployScopeKind.ResourceGroup));
        Assert.Equal([DeployScopeKind.Subscription, DeployScopeKind.ResourceGroup], Rg().AllowedBatchKinds);
        Assert.Equal([DeployScopeKind.ManagementGroup], Mg().AllowedBatchKinds);
        Assert.Equal([DeployScopeKind.Tenant], DeployTargetScope.TenantScope().AllowedBatchKinds);
    }

    // ---------- planner gate per target ----------

    [Fact]
    public void ResourceGroupTarget_BehavesLikeTheLegacyGate()
    {
        var offTarget = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/AAA/resourceGroups/other-rg"}""");
        Assert.True(TryApply(offTarget, Rg(), out _, out var mismatch));
        Assert.Contains("ScopeTargetMismatch", mismatch);

        var matching = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/BBB/resourceGroups/TARGET-RG"}""");
        Assert.False(TryApply(matching, Rg(), out _, out _));

        var tenant = Template(
            """{"name":"rp1","type":"Microsoft.Authorization/policyDefinitions","apiVersion":"2023-04-01","scope":"/"}""");
        Assert.True(TryApply(tenant, Rg(), out _, out var tenantMessage));
        Assert.Contains("MultiScopeNotDeployable", tenantMessage);
    }

    [Fact]
    public void SubscriptionTarget_RidesRgBatch_AndRequiresScopedPathsNameTheApprovedSubscription()
    {
        var staged = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/bbb"},"""
            + """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01"}""");
        Assert.False(TryApply(staged, Sub(), out var plan, out var errors));
        Assert.Equal("", errors);
        Assert.Equal([DeployScopeKind.Subscription, DeployScopeKind.ResourceGroup], plan.Batches.Select(b => b.Kind).ToList());

        // Any group of the approved subscription is in identity; a foreign group is not.
        var inSub = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/bbb/resourceGroups/any-rg"}""");
        Assert.False(TryApply(inSub, Sub(), out _, out _));
        var foreignRg = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/ccc/resourceGroups/any-rg"}""");
        Assert.True(TryApply(foreignRg, Sub(), out _, out var foreignMessage));
        Assert.Contains("ScopeTargetMismatch", foreignMessage);

        // Management-group and tenant scopes have no endpoint under a subscription identity.
        var mgScope = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/providers/Microsoft.Management/managementGroups/mg1"}""");
        Assert.True(TryApply(mgScope, Sub(), out _, out var mgMessage));
        Assert.Contains("MultiScopeNotDeployable", mgMessage);
    }

    [Fact]
    public void SubscriptionTarget_NestedRetargetStaysInsideTheApprovedSubscription()
    {
        var otherGroup = Template(
            """{"name":"n1","type":"Microsoft.Resources/deployments","apiVersion":"2022-09-01","resourceGroup":"another-rg","properties":{"mode":"Incremental","template":{"resources":[]}}}""");
        Assert.False(TryApply(otherGroup, Sub(), out _, out _));

        var foreignSub = Template(
            """{"name":"n1","type":"Microsoft.Resources/deployments","apiVersion":"2022-09-01","subscriptionId":"ccc","properties":{"mode":"Incremental","template":{"resources":[]}}}""");
        Assert.True(TryApply(foreignSub, Sub(), out _, out var foreignMessage));
        Assert.Contains("ScopeTargetMismatch", foreignMessage);

        var expressionSub = Template(
            """{"name":"n1","type":"Microsoft.Resources/deployments","apiVersion":"2022-09-01","subscriptionId":"[parameters('s')]","properties":{"mode":"Incremental","template":{"resources":[]}}}""");
        Assert.True(TryApply(expressionSub, Sub(), out _, out var unprovable));
        Assert.Contains("ScopeExpressionNotPlanned", unprovable);
    }

    [Fact]
    public void ManagementGroupTarget_KeepsSingleScope_AndProofsTheApprovedGroup()
    {
        var mgBatch = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/providers/Microsoft.Management/managementGroups/MG1"}""");
        Assert.False(TryApply(mgBatch, Mg(), out var plan, out var errors));
        Assert.Equal("", errors);
        Assert.Equal([DeployScopeKind.ManagementGroup], plan.Batches.Select(b => b.Kind).ToList());

        var foreignMg = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/providers/Microsoft.Management/managementGroups/mg2"}""");
        Assert.True(TryApply(foreignMg, Mg(), out _, out var mismatch));
        Assert.Contains("ScopeTargetMismatch", mismatch);

        // Anything resource-group shaped has no endpoint the MG identity names.
        var storage = Template("""{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01"}""");
        Assert.True(TryApply(storage, Mg(), out _, out var beyond));
        Assert.Contains("MultiScopeNotDeployable", beyond);

        // Nested retargets are unprovable below a management group - blocked, not guessed.
        var retarget = Template(
            """{"name":"n1","type":"Microsoft.Resources/deployments","apiVersion":"2022-09-01","resourceGroup":"rg1","properties":{"mode":"Incremental","template":{"resources":[]}}}""");
        Assert.True(TryApply(retarget, Mg(), out _, out var nested));
        Assert.Contains("MultiScopeNotDeployable", nested);
    }

    [Fact]
    public void TenantTarget_DeploysTenantBatchesOnly()
    {
        var tenantBatch = Template(
            """{"name":"rp1","type":"Microsoft.Authorization/policyDefinitions","apiVersion":"2023-04-01","scope":"/"}""");
        Assert.False(TryApply(tenantBatch, DeployTargetScope.TenantScope(), out var plan, out var errors));
        Assert.Equal("", errors);
        Assert.Equal([DeployScopeKind.Tenant], plan.Batches.Select(b => b.Kind).ToList());

        var subscriptionScoped = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/bbb"}""");
        Assert.True(TryApply(subscriptionScoped, DeployTargetScope.TenantScope(), out _, out var beyond));
        Assert.Contains("MultiScopeNotDeployable", beyond);
    }

    // ---------- preview selection ----------

    [Fact]
    public void Preview_SubscriptionTarget_PreviewsSubscriptionBatchAtTheTarget()
    {
        var text = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/bbb"},"""
            + """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01"}""");
        Assert.False(DeployScopePlanner.TryStageForApply(text, Sub(), out var plan, out _));

        var preview = StagedPreviewPlanner.Select(plan, Sub(), text, "{}");

        Assert.False(preview.Unsupported);
        Assert.Contains("ra1", preview.TemplateJson);
        Assert.DoesNotContain("st1", preview.TemplateJson);
        Assert.Contains("Subscription-scope batch", preview.Caveat);
        Assert.Contains("ResourceGroup", preview.Caveat);
    }

    [Fact]
    public void Preview_TargetWithoutItsOwnBatch_IsUnpreviewable()
    {
        var text = Template(
            """{"name":"ra1","type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","scope":"/subscriptions/bbb"}""");
        Assert.False(DeployScopePlanner.TryStageForApply(text, Sub(), out var plan, out _));

        // Same plan at a tenant target: staged {Subscription} batches deploy nowhere on it.
        var tenantPlan = DeployScopePlanner.Create(text, DeployScopeKind.Tenant);
        var preview = StagedPreviewPlanner.Select(tenantPlan, DeployTargetScope.TenantScope(), text, "{}");
        Assert.True(preview.Unsupported);
        Assert.Contains(StagedPreviewPlanner.UnsupportedCode, preview.Caveat);
    }

    [Fact]
    public void Preview_SingleBatchPlan_PreviewsWholePayloadAtItsTarget()
    {
        var text = Template("""{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01"}""");
        Assert.False(DeployScopePlanner.TryStageForApply(text, Rg(), out var plan, out _));

        var preview = StagedPreviewPlanner.Select(plan, Rg(), text, "{}");
        Assert.False(preview.Unsupported);
        Assert.Equal(text, preview.TemplateJson);
    }

    // ---------- plan identity ----------

    [Fact]
    public void Artifact_ResourceGroupMembers_AreByteIdenticalToLegacyCreate()
    {
        var legacy = DeployPlanArtifact.Create("sub", "rg", """{"resources":[], "a":1}""", """{"p":{"value":1}}""");
        var typed = DeployPlanArtifact.Create(DeployPlanScopeKind.ResourceGroup, "sub", "rg", null, """{"a":1, "resources":[]}""", """{"p":{"value":1}}""");

        Assert.Equal(legacy.PlanHash, typed.PlanHash);
        Assert.Contains("\"scope\":{\"resourceGroup\":\"rg\",\"subscriptionId\":\"sub\"}", legacy.CanonicalJson);
    }

    [Fact]
    public void Artifact_NonResourceGroupTargets_NameExactlyTheirIdentity_AndNeverCollide()
    {
        const string template = """{"resources":[]}""";
        var mg = DeployPlanArtifact.Create(DeployPlanScopeKind.ManagementGroup, null, null, "mg1", template);
        var sub = DeployPlanArtifact.Create(DeployPlanScopeKind.Subscription, "sub", null, null, template);
        var tenant = DeployPlanArtifact.Create(DeployPlanScopeKind.Tenant, null, null, null, template);
        var rg = DeployPlanArtifact.Create("sub", "rg", template);

        Assert.Contains("\"scope\":{\"managementGroupId\":\"mg1\"}", mg.CanonicalJson);
        Assert.Contains("\"scope\":{\"subscriptionId\":\"sub\"}", sub.CanonicalJson);
        Assert.Contains("\"scope\":{}", tenant.CanonicalJson);

        var hashes = new[] { mg.PlanHash, sub.PlanHash, tenant.PlanHash, rg.PlanHash };
        Assert.Equal(hashes.Length, hashes.Distinct().Count());

        // Rehashing a stored body still verifies, and the other management group is a different plan.
        Assert.True(DeployPlanArtifact.Verify(mg.CanonicalJson, mg.PlanHash));
        Assert.NotEqual(mg.PlanHash, DeployPlanArtifact.Create(DeployPlanScopeKind.ManagementGroup, null, null, "mg2", template).PlanHash);
    }

    [Fact]
    public void Artifact_HalfBoundTargetIdentity_Throws()
    {
        Assert.Throws<ArgumentException>(() => DeployPlanArtifact.Create(DeployPlanScopeKind.Subscription, "", null, null, """{"resources":[]}"""));
        Assert.Throws<ArgumentException>(() => DeployPlanArtifact.Create(DeployPlanScopeKind.ManagementGroup, null, null, "", """{"resources":[]}"""));
    }
}
