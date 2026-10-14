using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// The staged apply submits each batch at its own scope, so monitor coverage must span both
/// listings: a subscription batch never appears in the resource-group deployment listing.
/// </summary>
public class StagedDeploymentFamilyTests
{
    private static JsonElement Entry(string? name, string? state)
    {
        var entry = new JsonObject();
        if (name is not null)
        {
            entry["name"] = name;
        }

        if (state is not null)
        {
            entry["properties"] = new JsonObject { ["provisioningState"] = state };
        }

        return JsonDocument.Parse(entry.ToJsonString()).RootElement;
    }

    [Fact]
    public void SubscriptionBatch_InvisibleInResourceGroupListing_StillJoinsTheFamily()
    {
        var family = StagedDeploymentFamily.Collect(
            [Entry("ads-deployment-b1", "Running")],
            []);

        var member = Assert.Single(family);
        Assert.Equal("ads-deployment-b1", member.Name);
        Assert.Equal(StagedDeploymentScope.Subscription, member.Scope);
        Assert.Equal("Running", member.ProvisioningState);
        Assert.Equal("Running", StagedDeploymentFamily.Fold(family));
    }

    [Fact]
    public void BothScopesMergeWithDeterministicOrder()
    {
        var family = StagedDeploymentFamily.Collect(
            [Entry("ads-deployment-b1", "Succeeded")],
            [Entry("ads-deployment-b2", "Running"), Entry("ads-deployment", "Failed")]);

        Assert.Equal(
            ["ads-deployment", "ads-deployment-b1", "ads-deployment-b2"],
            family.Select(m => m.Name).ToList());
        Assert.Equal("Failed", StagedDeploymentFamily.Fold(family)); // worst state wins across scopes
    }

    [Fact]
    public void SameNameAtBothScopes_StaysTwoMembers()
    {
        // Deployment names are unique per scope, not globally; the scopes are distinct deployments.
        var family = StagedDeploymentFamily.Collect(
            [Entry("ads-deployment-b1", "Running")],
            [Entry("ads-deployment-b1", "Succeeded")]);

        Assert.Equal(2, family.Count);
        Assert.Equal([StagedDeploymentScope.Subscription, StagedDeploymentScope.ResourceGroup],
            family.Select(m => m.Scope).ToList());
    }

    [Fact]
    public void ForeignAndNamelessEntries_NeverJoinTheFamily()
    {
        var family = StagedDeploymentFamily.Collect(
            [Entry("other-deployment", "Failed"), Entry(null, "Failed")],
            [Entry("ads-deployments-b1", "Failed"), Entry("ads-deployment-b1", "Succeeded")]);

        var member = Assert.Single(family);
        Assert.Equal("ads-deployment-b1", member.Name);
    }

    [Fact]
    public void OperationsUrl_UsesTheMembersOwnScope()
    {
        var subscriptionMember = new StagedDeploymentMember("ads-deployment-b1", StagedDeploymentScope.Subscription, "Running");
        var resourceGroupMember = new StagedDeploymentMember("ads-deployment-b2", StagedDeploymentScope.ResourceGroup, "Running");

        var subscriptionUrl = subscriptionMember.OperationsUrl("https://management.azure.com", "sub-1", "rg-1", "2025-04-01");
        var resourceGroupUrl = resourceGroupMember.OperationsUrl("https://management.azure.com", "sub-1", "rg-1", "2025-04-01");

        Assert.Equal(
            "https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Resources/deployments/ads-deployment-b1/operations?api-version=2025-04-01",
            subscriptionUrl);
        Assert.Equal(
            "https://management.azure.com/subscriptions/sub-1/resourcegroups/rg-1/providers/Microsoft.Resources/deployments/ads-deployment-b2/operations?api-version=2025-04-01",
            resourceGroupUrl);
    }
}
