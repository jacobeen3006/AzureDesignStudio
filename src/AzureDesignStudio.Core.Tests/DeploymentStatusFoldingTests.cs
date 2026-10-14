using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>Monitor fold: one approved plan, worst state wins, only anchored staged names match.</summary>
public class DeploymentStatusFoldingTests
{
    [Theory]
    [InlineData(new[] { "Succeeded", "Succeeded" }, "Succeeded")]
    [InlineData(new[] { "Succeeded", "Failed" }, "Failed")]
    [InlineData(new[] { "Failed", "Running" }, "Failed")]
    [InlineData(new[] { "Running", "Succeeded" }, "Running")]
    [InlineData(new[] { "Accepted", "Succeeded" }, "Running")]
    [InlineData(new[] { "Created", "Running" }, "Running")]
    [InlineData(new[] { "Canceled", "Succeeded" }, "Canceled")]
    [InlineData(new[] { "Succeeded", "Weird" }, "Running")]
    [InlineData(new string[0], "")]
    public void Fold_WorstStateWinsAcrossTheFamily(string[] states, string expected) =>
        Assert.Equal(expected, DeploymentStatusFolding.Fold(states));

    [Fact]
    public void Fold_IgnoresMissingAndBlankStates() =>
        Assert.Equal("Failed", DeploymentStatusFolding.Fold([null, "  ", "Failed", null]));

    [Theory]
    [InlineData("ads-deployment", true)]
    [InlineData("ads-deployment-b1", true)]
    [InlineData("ADS-DEPLOYMENT-B12", true)]
    [InlineData("ads-deployment-x", false)]      // foreign name wearing the bare prefix
    [InlineData("ads-deployments-b1", false)]
    [InlineData("my-ads-deployment-b1", false)]
    [InlineData("other", false)]
    [InlineData(null, false)]
    public void IsAdsDeployment_AnchorsTheStagedFamily(string? name, bool expected) =>
        Assert.Equal(expected, DeploymentStatusFolding.IsAdsDeployment(name));
}
