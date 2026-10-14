using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Core.Common;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 11.3 pre-work - plan identity = scope+template+parameters: canonical form, one hash, tamper visible.</summary>
public class DeployPlanArtifactTests
{
    private const string Template = """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","sku":{"name":"Standard_LRS"}}]}""";

    private const string TemplateSameReordered = """{ "resources" : [ { "sku": {"name":"Standard_LRS"}, "apiVersion": "2023-01-01", "type":"Microsoft.Storage/storageAccounts", "name":"st1"} ], "$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#", "contentVersion": "1.0.0.0" }""";

    [Fact]
    public void Canonicalization_IgnoresKeyOrderAndWhitespace()
    {
        var a = DeployPlanArtifact.Create("sub-1", "rg1", Template, "{}");
        var b = DeployPlanArtifact.Create("sub-1", "rg1", TemplateSameReordered, "{   }");

        Assert.Equal(a.CanonicalJson, b.CanonicalJson);
        Assert.Equal(a.PlanHash, b.PlanHash);
    }

    [Fact]
    public void AnyDeployRelevantChange_ChangesHash()
    {
        var baseline = DeployPlanArtifact.Create("sub-1", "rg1", Template);

        var skuEdited = DeployPlanArtifact.Create("sub-1", "rg1", Template.Replace("Standard_LRS", "Standard_GRS"));
        var resourceRenamed = DeployPlanArtifact.Create("sub-1", "rg1", Template.Replace("\"name\":\"st1\"", "\"name\":\"st2\""));
        var otherScope = DeployPlanArtifact.Create("sub-1", "rg2", Template);
        var otherSubscription = DeployPlanArtifact.Create("sub-2", "rg1", Template);
        var withParameter = DeployPlanArtifact.Create("sub-1", "rg1", Template, """{"location":{"value":"westeurope"}}""");

        AssertDistinct(baseline.PlanHash, skuEdited.PlanHash, resourceRenamed.PlanHash, otherScope.PlanHash, otherSubscription.PlanHash, withParameter.PlanHash);
    }

    [Fact]
    public void ArrayOrderIsSemantics_NotNormalized()
    {
        var twoResources = """{"resources":[{"name":"a","type":"T/a"},{"name":"b","type":"T/b"}]}""";
        var swapped = """{"resources":[{"name":"b","type":"T/b"},{"name":"a","type":"T/a"}]}""";

        Assert.NotEqual(
            DeployPlanArtifact.Create("sub-1", "rg1", twoResources).PlanHash,
            DeployPlanArtifact.Create("sub-1", "rg1", swapped).PlanHash);
    }

    [Fact]
    public void Verify_AcceptsUnchangedAndPrettyPrinted_Bodies_RejectsEdited()
    {
        var plan = DeployPlanArtifact.Create("sub-1", "rg1", Template, """{"location":{"value":"westeurope"}}""");

        Assert.True(DeployPlanArtifact.Verify(plan.CanonicalJson, plan.PlanHash));

        var pretty = JsonSerializer.Serialize(JsonDocument.Parse(plan.CanonicalJson).RootElement, new JsonSerializerOptions { WriteIndented = true });
        Assert.True(DeployPlanArtifact.Verify(pretty, plan.PlanHash)); // store may pretty-print; the hash is over canonical form

        Assert.False(DeployPlanArtifact.Verify(plan.CanonicalJson.Replace("Standard_LRS", "Standard_GRS"), plan.PlanHash));
        Assert.False(DeployPlanArtifact.Verify(plan.CanonicalJson, ""));
        Assert.False(DeployPlanArtifact.Verify("not json", plan.PlanHash));
    }

    [Fact]
    public void Create_RejectsGarbageJson_AndEmptyScope()
    {
        Assert.Throws<System.ArgumentException>(() => DeployPlanArtifact.Create("sub-1", "rg1", "not json"));
        Assert.Throws<System.ArgumentException>(() => DeployPlanArtifact.Create("", "rg1", Template));
        Assert.Throws<System.ArgumentException>(() => DeployPlanArtifact.Create("sub-1", "  ", Template));
    }

    [Fact]
    public void Hash_IsTaggedLowercaseSha256Hex_DefaultParametersEqualExplicitEmpty()
    {
        var plan = DeployPlanArtifact.Create("sub-1", "rg1", Template);

        Assert.StartsWith(DeployPlanArtifact.HashPrefix, plan.PlanHash);
        var hex = plan.PlanHash[DeployPlanArtifact.HashPrefix.Length..];
        Assert.Equal(64, hex.Length);
        Assert.Matches("^[0-9a-f]{64}$", hex);
        Assert.Equal(plan.PlanHash, DeployPlanArtifact.Create("sub-1", "rg1", Template, "{}").PlanHash);
    }

    private static void AssertDistinct(params string[] hashes) =>
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
}
