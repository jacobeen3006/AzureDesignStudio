using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Discovery.Unmanaged;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Phase 5 (D-05): the merge mode of the per-tenant guardrail policy. <c>Merge(inherited, own)</c> is
/// the union of both lists - the inherited rules always apply and a scope can only add rules. It is
/// pure: neither input is changed, and the result protects exactly what either side protects.
/// </summary>
public class GuardrailPolicyMergeTests
{
    private static readonly IReadOnlyDictionary<string, string> NoTags = new Dictionary<string, string>();

    private static GuardrailPolicy Policy(string[]? scopes = null, ProtectedTag[]? tags = null, string[]? types = null) =>
        new(scopes ?? [], tags ?? [], types ?? []);

    [Fact]
    public void TheResultIsTheUnionOfScopesTagsAndTypesInheritedFirst()
    {
        var inherited = Policy(["subscriptions/s1"], [new ProtectedTag("environment", "prod")], ["microsoft.sql/servers"]);
        var own = Policy(["subscriptions/s2"], [new ProtectedTag("owner")], ["microsoft.keyvault/vaults"]);

        var merged = GuardrailPolicy.Merge(inherited, own);

        Assert.Equal(["subscriptions/s1", "subscriptions/s2"], merged.ProtectedScopes);
        Assert.Equal([new ProtectedTag("environment", "prod"), new ProtectedTag("owner")], merged.ProtectedTags);
        Assert.Equal(["microsoft.sql/servers", "microsoft.keyvault/vaults"], merged.ProtectedTypes);
    }

    [Fact]
    public void ScopesAndTypesAreComparedCaseInsensitivelyWithoutDuplicates()
    {
        var inherited = Policy(["Subscriptions/S1"], types: ["Microsoft.Sql/Servers"]);
        var own = Policy(["subscriptions/s1", "subscriptions/s2"], types: ["microsoft.sql/servers", "Microsoft.KeyVault/vaults"]);

        var merged = GuardrailPolicy.Merge(inherited, own);

        Assert.Equal(["Subscriptions/S1", "subscriptions/s2"], merged.ProtectedScopes);
        Assert.Equal(["Microsoft.Sql/Servers", "Microsoft.KeyVault/vaults"], merged.ProtectedTypes);
    }

    [Fact]
    public void TagsAreEqualWhenKeyAndValueMatchCaseInsensitively()
    {
        var inherited = Policy(tags: [new ProtectedTag("Environment", "Prod")]);
        var own = Policy(tags: [new ProtectedTag("environment", "prod"), new ProtectedTag("environment", "dev")]);

        var merged = GuardrailPolicy.Merge(inherited, own);

        Assert.Equal([new ProtectedTag("Environment", "Prod"), new ProtectedTag("environment", "dev")], merged.ProtectedTags);
    }

    [Fact]
    public void AKeyOnlyTagAndAKeyedValueTagAreDifferentRules()
    {
        var merged = GuardrailPolicy.Merge(
            Policy(tags: [new ProtectedTag("environment")]),
            Policy(tags: [new ProtectedTag("environment", "prod")]));

        Assert.Equal(2, merged.ProtectedTags.Count);
    }

    [Fact]
    public void MergingWithTheEmptyPolicyReturnsTheOtherSideUnchanged()
    {
        var policy = Policy(["subscriptions/s1"], [new ProtectedTag("owner", "x")], ["microsoft.sql/servers"]);

        foreach (var merged in new[] { GuardrailPolicy.Merge(GuardrailPolicy.Empty, policy), GuardrailPolicy.Merge(policy, GuardrailPolicy.Empty) })
        {
            Assert.Equal(policy.ProtectedScopes, merged.ProtectedScopes);
            Assert.Equal(policy.ProtectedTags, merged.ProtectedTags);
            Assert.Equal(policy.ProtectedTypes, merged.ProtectedTypes);
        }

        var both = GuardrailPolicy.Merge(GuardrailPolicy.Empty, GuardrailPolicy.Empty);
        Assert.Empty(both.ProtectedScopes);
        Assert.Empty(both.ProtectedTags);
        Assert.Empty(both.ProtectedTypes);
    }

    [Fact]
    public void MergingDoesNotMutateEitherInput()
    {
        var inherited = Policy(["subscriptions/s1"], [new ProtectedTag("a")], ["microsoft.sql/servers"]);
        var own = Policy(["subscriptions/s2"], [new ProtectedTag("b")], ["microsoft.keyvault/vaults"]);

        var merged = GuardrailPolicy.Merge(inherited, own);

        Assert.Equal(["subscriptions/s1"], inherited.ProtectedScopes);
        Assert.Equal([new ProtectedTag("a")], inherited.ProtectedTags);
        Assert.Equal(["microsoft.sql/servers"], inherited.ProtectedTypes);
        Assert.Equal(["subscriptions/s2"], own.ProtectedScopes);
        Assert.Equal([new ProtectedTag("b")], own.ProtectedTags);
        Assert.Equal(["microsoft.keyvault/vaults"], own.ProtectedTypes);
        Assert.NotSame(inherited.ProtectedScopes, merged.ProtectedScopes);
        Assert.NotSame(own.ProtectedScopes, merged.ProtectedScopes);
    }

    [Fact]
    public void AMergedPolicyProtectsEverythingEitherSideProtectsAndNothingElse()
    {
        var inherited = Policy(types: ["microsoft.sql/servers"]);
        var own = Policy(["subscriptions/s2"], [new ProtectedTag("owner", "x")]);
        var merged = GuardrailPolicy.Merge(inherited, own);

        const string sql = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Sql/servers/db";
        const string inScope = "/subscriptions/s2/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa";
        const string tagged = "/subscriptions/s3/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sb";
        const string plain = "/subscriptions/s3/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sc";
        var ownerTag = new Dictionary<string, string> { ["Owner"] = "X" };

        Assert.NotNull(merged.ProtectionReasonFor(sql, "microsoft.sql/servers", NoTags));
        Assert.Null(own.ProtectionReasonFor(sql, "microsoft.sql/servers", NoTags));
        Assert.NotNull(merged.ProtectionReasonFor(inScope, "microsoft.storage/storageaccounts", NoTags));
        Assert.NotNull(merged.ProtectionReasonFor(tagged, "microsoft.storage/storageaccounts", ownerTag));
        Assert.Null(merged.ProtectionReasonFor(plain, "microsoft.storage/storageaccounts", NoTags));
        Assert.Null(inherited.ProtectionReasonFor(inScope, "microsoft.storage/storageaccounts", NoTags));
    }

    [Fact]
    public void ANullSideIsRejectedNotSilentlyTreatedAsEmpty()
    {
        Assert.Throws<System.ArgumentNullException>(() => GuardrailPolicy.Merge(null!, GuardrailPolicy.Empty));
        Assert.Throws<System.ArgumentNullException>(() => GuardrailPolicy.Merge(GuardrailPolicy.Empty, null!));
    }
}
