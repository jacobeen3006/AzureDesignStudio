using System;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 10.2 follow-up - api-version table derived from the generated ARM models' [AzureResource] attributes.</summary>
public class AzureApiVersionsTests
{
    [Fact]
    public void Map_CarriesKnownTypes_WithStableVersions()
    {
        Assert.True(AzureApiVersions.Map.Count > 20, $"expected a real table, got {AzureApiVersions.Map.Count} entries");
        Assert.Equal("2023-08-01", AzureApiVersions.Map["Microsoft.Cache/redis"]);
        Assert.DoesNotContain("preview", AzureApiVersions.Map["Microsoft.Compute/virtualMachineScaleSets"]);
    }

    [Fact]
    public void Map_IsCaseInsensitive_AndBuiltOnce()
    {
        Assert.Equal(AzureApiVersions.Map["Microsoft.Cache/redis"], AzureApiVersions.Map["MICROSOFT.CACHE/REDIS"]);
        Assert.Same(AzureApiVersions.Map, AzureApiVersions.Map);
    }

    [Fact]
    public void RealTable_PinsScannedTypes_RealDifferentFromUniformDefault()
    {
        var vm = new InventoryItem(
            "/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1",
            "vm1", "Microsoft.Compute/virtualMachines", "westeurope", "rg1", "sub-1", new System.Collections.Generic.Dictionary<string, string>(), null);

        var text = TerraformText.Generate([vm], typeApiVersions: AzureApiVersions.Map);
        var pinned = AzureApiVersions.Map["Microsoft.Compute/virtualMachines"];

        Assert.Contains($"Microsoft.Compute/virtualMachines@{pinned}", text);
        Assert.NotEqual(TerraformText.DefaultApiVersion, pinned); // a real model version, not the fallback
    }
}
