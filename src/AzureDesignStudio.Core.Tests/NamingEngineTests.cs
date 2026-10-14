using System;
using System.Linq;
using AzureDesignStudio.Core.Common;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 10.4 - naming engine: profile semantics, deterministic entropy, fitting.</summary>
public class NamingEngineTests
{
    private static readonly string[] Hex6 = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "a", "b", "c", "d", "e", "f"];

    private static bool EndsInEntropy(string name) =>
        name.Length >= 6 && name[^6..].All(c => Hex6.Contains(c.ToString()));

    [Fact]
    public void Suggest_Storage_IsLowercaseAlnumDeterministicWithStableEntropy()
    {
        var a = NamingEngine.Suggest(AdsConstants.StorageAccount, "Payments", "Prod");
        var b = NamingEngine.Suggest(AdsConstants.StorageAccount, "Payments", "Prod");

        Assert.Equal(a, b); // regenerated designs diff to nothing
        Assert.True(NamingEngine.IsValid(AdsConstants.StorageAccount, a));
        Assert.True(EndsInEntropy(a));
        Assert.DoesNotContain("-", a);
        Assert.NotEqual(a, NamingEngine.Suggest(AdsConstants.StorageAccount, "Payments", "Dev")); // environment splits entropy
    }

    [Fact]
    public void Suggest_KeyVault_KeepsHyphenSegmentsAndOriginalCase()
    {
        var name = NamingEngine.Suggest(AdsConstants.KeyVault, "Payments", "Prod");

        Assert.StartsWith("Payments-Prod-", name);
        Assert.True(NamingEngine.IsValid(AdsConstants.KeyVault, name));
    }

    [Fact]
    public void Suggest_TruncatesWorkloadButKeepsEntropyTail()
    {
        var name = NamingEngine.Suggest(AdsConstants.SqlServer, "extremelylongworkloadnameformarketingtelemetry", "Prod");

        Assert.True(name.Length <= 15); // SQL server hard cap
        Assert.True(EndsInEntropy(name)); // the globally-unique tail survives truncation
        Assert.True(NamingEngine.IsValid(AdsConstants.SqlServer, name));
    }

    [Fact]
    public void Suggest_ResolvedTakenWithStableCounter()
    {
        var first = NamingEngine.Suggest(AdsConstants.KeyVault, "Payments", "Prod");
        var second = NamingEngine.Suggest(AdsConstants.KeyVault, "Payments", "Prod", [first]);

        Assert.EndsWith("-2", second);
        Assert.NotEqual(first, second);
        Assert.True(NamingEngine.IsValid(AdsConstants.KeyVault, second));
    }

    [Fact]
    public void Suggest_PadsToProfileMinimum()
    {
        var name = NamingEngine.Suggest(AdsConstants.EventHubs, "a", "b");

        Assert.True(name.Length >= 6);
        Assert.True(NamingEngine.IsValid(AdsConstants.EventHubs, name));
    }

    [Fact]
    public void ProfileSemantics_MatchAzureNameFamilies()
    {
        Assert.False(NamingEngine.IsValid(AdsConstants.StorageAccount, "Prod1")); // uppercase illegal for blob endpoints
        Assert.True(NamingEngine.IsValid(AdsConstants.StorageAccount, "prod1"));
        Assert.True(NamingEngine.IsValid(AdsConstants.VirtualMachine, "My-VM_1")); // generic ARM family
        Assert.True(NamingEngine.IsGloballyUnique(AdsConstants.StorageAccount));
        Assert.False(NamingEngine.IsGloballyUnique(AdsConstants.VirtualNetwork));
    }
}
