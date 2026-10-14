using System.Linq;
using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Targeted retry (v2 plan 11.2/[56]): the retry subset is exactly the failed monitor rows
/// (type/name key match, case-insensitive), dependsOn is pruned so ARM validation cannot
/// reject the subset, and anything unverifiable fails closed - never a widened fallback.
/// </summary>
public class RetryTemplateFilterTests
{
    private const string Template = """
    {
      "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
      "contentVersion": "1.0.0.0",
      "parameters": { "sku": { "type": "string" } },
      "variables": { "loc": "westeurope" },
      "resources": [
        { "type": "Microsoft.Network/virtualNetworks", "name": "vnet1", "apiVersion": "2023-09-01" },
        { "type": "Microsoft.Storage/storageAccounts", "name": "st1", "apiVersion": "2023-01-01",
          "dependsOn": ["vnet1", "vnet2"] },
        { "type": "Microsoft.Web/sites", "name": "app1", "apiVersion": "2022-09-01",
          "dependsOn": ["st1", "[resourceId('Microsoft.Storage/storageAccounts','st1')]", "[resourceId('Microsoft.Network/virtualNetworks','vnet2')]"] },
        { "type": "Microsoft.DocumentDB/databaseAccounts", "name": "db1", "apiVersion": "2023-11-15",
          "dependsOn": ["[parameters('dep')]"] }
      ],
      "outputs": { "id": { "type": "string", "value": "[resourceId('Microsoft.Storage/storageAccounts','st1')]" } }
    }
    """;

    [Fact]
    public void MatchesByTypeAndNameKey_CaseInsensitive()
    {
        var result = RetryTemplateFilter.Filter(Template,
            new[] { "MICROSOFT.STORAGE/STORAGEACCOUNTS/ST1" });

        Assert.Null(result.Error);
        Assert.Equal(["microsoft.storage/storageaccounts/st1"], result.MatchedKeys);
        using var doc = System.Text.Json.JsonDocument.Parse(result.TemplateJson!);
        var resources = doc.RootElement.GetProperty("resources").EnumerateArray().ToList();
        var only = Assert.Single(resources);
        Assert.Equal("st1", only.GetProperty("name").GetString());
        // Everything outside resources survives untouched.
        Assert.True(doc.RootElement.TryGetProperty("parameters", out _));
        Assert.True(doc.RootElement.TryGetProperty("variables", out _));
        Assert.True(doc.RootElement.TryGetProperty("outputs", out _));
        Assert.Equal("https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
            doc.RootElement.GetProperty("$schema").GetString());
    }

    [Fact]
    public void MultipleMatches_KeepOriginalOrder()
    {
        var result = RetryTemplateFilter.Filter(Template, new[]
        {
            "microsoft.web/sites/app1",
            "microsoft.storage/storageaccounts/st1",
        });

        Assert.Null(result.Error);
        using var doc = System.Text.Json.JsonDocument.Parse(result.TemplateJson!);
        var names = doc.RootElement.GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "st1", "app1" }, names);
    }

    [Fact]
    public void ZeroMatches_FailsClosed_NoWidenedFallback()
    {
        var result = RetryTemplateFilter.Filter(Template, new[] { "microsoft.keyvault/vaults/missing" });

        Assert.Null(result.TemplateJson);
        Assert.Contains("retry subset is empty", result.Error);
    }

    [Fact]
    public void UnknownKeys_Warn_ButMatchedSubsetProceeds()
    {
        var result = RetryTemplateFilter.Filter(Template,
            new[] { "microsoft.storage/storageaccounts/st1", "microsoft.fabric/capacities/f1" });

        Assert.Null(result.Error);
        Assert.Contains(result.Warnings, w => w.Contains("microsoft.fabric/capacities/f1"));
    }

    [Fact]
    public void DependsOn_PrunedToSubsetWithWarning_KeepingRetainedResourceIds()
    {
        var result = RetryTemplateFilter.Filter(Template,
            new[] { "microsoft.storage/storageaccounts/st1", "microsoft.web/sites/app1" });

        Assert.Null(result.Error);
        Assert.Contains(result.Warnings, w => w.Contains("vnet2") && w.Contains("pruned"));
        using var doc = System.Text.Json.JsonDocument.Parse(result.TemplateJson!);
        var resources = doc.RootElement.GetProperty("resources").EnumerateArray()
            .ToDictionary(r => r.GetProperty("name").GetString()!);
        // st1's plain-name dependencies both point outside the subset.
        Assert.Empty(resources["st1"].GetProperty("dependsOn").EnumerateArray());
        // Plain name AND literal resourceId() to a RETAINED resource both survive verbatim.
        var deps = resources["app1"].GetProperty("dependsOn").EnumerateArray()
            .Select(d => d.GetString()).ToList();
        Assert.Equal(new[] { "st1", "[resourceId('Microsoft.Storage/storageAccounts','st1')]" }, deps);
    }

    [Fact]
    public void UnresolvableDependsOnExpression_IsKeptAndDisclosed()
    {
        var result = RetryTemplateFilter.Filter(Template, new[] { "microsoft.documentdb/databaseaccounts/db1" });

        Assert.Null(result.Error);
        Assert.Contains(result.Warnings, w => w.Contains("cannot be statically verified"));
        using var doc = System.Text.Json.JsonDocument.Parse(result.TemplateJson!);
        var db1 = doc.RootElement.GetProperty("resources").EnumerateArray().Single();
        Assert.Equal("[parameters('dep')]", db1.GetProperty("dependsOn")[0].GetString());
    }

    [Fact]
    public void NestedTypeKey_MatchesFullTypeString()
    {
        const string nested = """
        { "resources": [
            { "type": "Microsoft.Storage/storageAccounts/blobServices", "name": "acct/default", "apiVersion": "2023-01-01" },
            { "type": "Microsoft.Storage/storageAccounts", "name": "acct", "apiVersion": "2023-01-01" }
        ] }
        """;

        var result = RetryTemplateFilter.Filter(nested,
            new[] { "microsoft.storage/storageaccounts/blobServices/acct/default" });

        Assert.Null(result.Error);
        var only = Assert.Single(result.MatchedKeys);
        Assert.Equal("microsoft.storage/storageaccounts/blobservices/acct/default", only);
    }

    [Fact]
    public void CopyLoop_InMatchedResource_WarnsAboutFullIteration()
    {
        const string withCopy = """
        { "resources": [
            { "type": "Microsoft.Network/virtualNetworks", "name": "vnet1", "apiVersion": "2023-09-01",
              "copy": { "name": "peers", "count": 3 } }
        ] }
        """;

        var result = RetryTemplateFilter.Filter(withCopy,
            new[] { "microsoft.network/virtualnetworks/vnet1" });

        Assert.Null(result.Error);
        Assert.Contains(result.Warnings, w => w.Contains("copy loop") && w.Contains("full iteration"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{ "contentVersion": "1.0.0.0" }""")]
    [InlineData("""{ "resources": [ { "type": "a/b" } ] }""")]
    [InlineData("""{ "resources": { "not": "an array" } }""")]
    public void UnclassifiableTemplates_FailClosed(string template)
    {
        var result = RetryTemplateFilter.Filter(template, new[] { "a/b/c" });

        Assert.Null(result.TemplateJson);
        Assert.StartsWith("InvalidTemplate", result.Error);
    }

    [Fact]
    public void NoSelectedKeys_IsAnError()
    {
        var result = RetryTemplateFilter.Filter(Template, new[] { "  " });

        Assert.Null(result.TemplateJson);
        Assert.Contains("No failed resources were selected", result.Error);
    }

    [Fact]
    public void Filter_IsDeterministic()
    {
        var first = RetryTemplateFilter.Filter(Template, new[] { "microsoft.web/sites/app1" });
        var second = RetryTemplateFilter.Filter(Template, new[] { "microsoft.web/sites/app1" });

        Assert.Equal(first.TemplateJson, second.TemplateJson);
    }
}
