using System.Linq;
using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Preview truth for staged plans: the RG-scope batch is what ARM can honestly what-if;
/// the other batches deploy under the same approval with the caveat recorded, and a plan
/// ARM cannot preview at all never earns an approval token.
/// </summary>
public class StagedPreviewPlannerTests
{
    private static string Template(string resources, string parameters = "{}") =>
        $$"""{ "contentVersion": "1.0.0.0", "parameters": {{parameters}}, "resources": [{{resources}}] }""";

    [Fact]
    public void SingleResourceGroupPlan_PreviewsTheOriginalTemplateUnchanged()
    {
        var text = Template(
            """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","location":"westeurope"}""");
        var plan = DeployScopePlanner.Create(text);

        var preview = StagedPreviewPlanner.Select(plan, text, "{\"a\":{\"value\":1}}");

        Assert.False(preview.Unsupported);
        Assert.Null(preview.Caveat);
        Assert.Equal(text, preview.TemplateJson);
        Assert.Equal("{\"a\":{\"value\":1}}", preview.ParametersJson);
    }

    [Fact]
    public void StagedPlan_PreviewsOnlyTheResourceGroupBatchAndDisclosesTheRest()
    {
        var text = Template(
            """{"name":"rg1","type":"Microsoft.Resources/resourceGroups","apiVersion":"2022-09-01","location":"westeurope"},"""
            + """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","dependsOn":["[resourceId('Microsoft.Resources/resourceGroups','rg1')]"]}""");
        var plan = DeployScopePlanner.Create(text);

        var preview = StagedPreviewPlanner.Select(plan, text, "{}");

        Assert.False(preview.Unsupported);
        Assert.Contains("Staged apply", preview.Caveat);
        Assert.Contains("ResourceGroup", preview.Caveat);
        Assert.DoesNotContain("resourceGroups\",", preview.TemplateJson);   // the RG-creating batch is not previewed
        Assert.Contains("storageAccounts", preview.TemplateJson);
    }

    [Fact]
    public void SubscriptionOnlyPlan_CannotBePreviewedAndNeverEarnsAToken()
    {
        var text = Template(
            """{"name":"rg1","type":"Microsoft.Resources/resourceGroups","apiVersion":"2022-09-01","location":"westeurope"}""");
        var plan = DeployScopePlanner.Create(text);

        var preview = StagedPreviewPlanner.Select(plan, text, "{}");

        Assert.True(preview.Unsupported);
        Assert.Contains(StagedPreviewPlanner.UnsupportedCode, preview.Caveat);
    }

    [Fact]
    public void StagedPlan_ParametersAreSubsetToThePreviewedBatch()
    {
        var text = Template(
            """{"name":"rg1","type":"Microsoft.Resources/resourceGroups","apiVersion":"2022-09-01","location":"[parameters('rgLocation')]"},"""
            + """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","location":"[parameters('rgLocation')]","dependsOn":["[resourceId('Microsoft.Resources/resourceGroups','rg1')]"]}""",
            parameters: """{ "rgLocation": { "type": "string" }, "unused": { "type": "string" } }""");
        var plan = DeployScopePlanner.Create(text);

        var preview = StagedPreviewPlanner.Select(plan, text,
            """{ "rgLocation": { "value": "westeurope" }, "unused": { "value": "x" } }""");

        Assert.Contains("rgLocation", preview.ParametersJson);
        Assert.DoesNotContain("unused", preview.ParametersJson);
    }
}
