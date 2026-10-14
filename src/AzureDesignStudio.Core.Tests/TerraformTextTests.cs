using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Iac;
using AzureDesignStudio.Discovery.Inventory;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 10.2 first cut - azapi Terraform text: determinism, honest header, parent derivation, injection-safe bodies.</summary>
public class TerraformTextTests
{
    private static InventoryItem Item(
        string id, string name, string type, string location = "westeurope",
        string rg = "rg1", string sub = "sub-1", string? rawJson = null, Dictionary<string, string>? tags = null)
    {
        JsonElement? raw = rawJson is null ? null : JsonDocument.Parse(rawJson).RootElement.Clone();
        return new InventoryItem(id, name, type, location, rg, sub, tags ?? [], raw);
    }

    private static InventoryItem Vm(string id = "/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1") =>
        Item(id, "vm1", "Microsoft.Compute/virtualMachines", rawJson: """{"hardwareProfile":{"vmSize":"Standard_D2s_v3"},"osProfile":{"computerName":"vm1"}}""");

    [Fact]
    public void Generate_IsDeterministicRegardlessOfInputOrder()
    {
        var a = TerraformText.Generate([Vm(), Item("/subscriptions/sub-1/resourceGroups/rg1", "rg1", "Microsoft.Storage/storageAccounts", rawJson: """{"z":1,"a":{"y":2,"b":3}}""")]);
        var b = TerraformText.Generate([Item("/subscriptions/sub-1/resourceGroups/rg1", "rg1", "Microsoft.Storage/storageAccounts", rawJson: """{"a":{"b":3,"y":2},"z":1}"""), Vm()]);
        Assert.Equal(a, b); // same scan -> byte-identical text (id sort + canonical body keys)
    }

    [Fact]
    public void Generate_EmitsAzapiBlocksUnderHonestHeader()
    {
        var text = TerraformText.Generate([Vm()]);

        Assert.Contains("STARTING POINT, NOT a reviewed configuration", text);
        Assert.Contains("source = \"Azure/azapi\"", text);
        Assert.Contains("resource \"azapi_resource\" \"virtualmachines_vm1\"", text);
        Assert.Contains("type      = \"Microsoft.Compute/virtualMachines@2023-07-01\"", text);
        Assert.Contains("\"computerName\"", text); // live properties carried
    }

    [Fact]
    public void Generate_DerivesParentIdFromArmId()
    {
        var nested = Item(
            "/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1/extensions/ext1",
            "ext1", "Microsoft.Compute/virtualMachines/extensions");

        var text = TerraformText.Generate([Vm(), nested]);

        Assert.Contains("parent_id = \"/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1\"", text); // child -> resource
        Assert.Contains("parent_id = \"/subscriptions/sub-1/resourceGroups/rg1\"", text); // top-level -> resource group scope
    }

    [Fact]
    public void Generate_NeutralizesTerraformMarkersInBodies()
    {
        var text = TerraformText.Generate([Item("/x/y", "y", "Microsoft.Test/t", rawJson: """{"secret":"${leak}","direct":"%{evil}"}""")]);

        Assert.Contains("\"$${leak}\"", text); // literal reaches terraform; interpolation never fires
        Assert.Contains("\"%%{evil}\"", text);
        Assert.DoesNotContain("\"${leak}\"", text);
    }

    [Fact]
    public void Generate_CollisionSuffixKeepsLocalNamesUnique()
    {
        var other = Item("/subscriptions/sub-1/resourceGroups/rg2/providers/Microsoft.Compute/virtualMachines/vm1", "vm1", "Microsoft.Compute/virtualMachines");

        var text = TerraformText.Generate([Vm(), other]);

        Assert.Equal(1, Count(text, "\"virtualmachines_vm1\" {"));
        Assert.Contains("\"virtualmachines_vm1_2\" {", text);
    }

    [Fact]
    public void Generate_SkipsSubscriptionLevelAndSaysSo()
    {
        var sub = Item("/subscriptions/sub-1", "sub-1", "Microsoft.Resources/subscriptions", location: "");

        var text = TerraformText.Generate([sub, Vm()]);

        Assert.DoesNotContain("resource \"azapi_resource\" \"subscriptions_sub_1\"", text);
        Assert.Contains("#       /subscriptions/sub-1", text); // skipped list, honestly in the header
        Assert.Equal(1, Count(text, "resource \"azapi_resource\""));
    }

    [Fact]
    public void Generate_TagsSortedAndEscaped_LocationOmittedWhenEmpty()
    {
        var text = TerraformText.Generate([Item("/x/y", "y", "Microsoft.Test/t", location: "",
            rawJson: """{"k":1}""", tags: new Dictionary<string, string> { ["z"] = "1", ["a"] = "${x}" })]);

        Assert.DoesNotContain("location  =", text);
        Assert.True(text.IndexOf("\"a\" = \"$${x}\"", System.StringComparison.Ordinal) < text.IndexOf("\"z\" = \"1\"", System.StringComparison.Ordinal));
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, System.StringComparison.Ordinal)) n++;
        return n;
    }
}
