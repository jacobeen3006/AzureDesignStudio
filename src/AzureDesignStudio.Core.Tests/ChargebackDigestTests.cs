using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Discovery.Cost;
using AzureDesignStudio.Discovery.Inventory;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// [77] Chargeback piles. The contract that matters is honesty about leftovers: untagged
/// and unmatched spend ride the digest as named piles, currencies are never blended,
/// and the CSV a finance team receives still names where its numbers came from.
/// </summary>
public class ChargebackDigestTests
{
    private const string Sub = "/subscriptions/sub1";

    private static InventoryItem Item(string id, params (string K, string V)[] tags) =>
        new(id, id.Split('/')[^1], "microsoft.compute/virtualmachines", "westeurope", "rg", "sub1",
            tags.ToDictionary(t => t.K, t => t.V), null);

    private static SpendRow Row(string? id, decimal cost, string currency = "EUR") =>
        new(id, "rg", null, null, null, cost, currency);

    private static SpendProvenance Prov(string scope = "subscriptions/sub1") =>
        new("cost-management-query", scope, "westeurope", "EUR",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    private readonly InventoryItem _alice = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vmA", ("owner", "alice"));
    private readonly InventoryItem _bob = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vmB", ("cost-center", "bob"));
    private readonly InventoryItem _nobody = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vmC");

    [Fact]
    public void OwnerPilesComeFromTagsAndUntaggedStaysVisible()
    {
        var rows = ChargebackDigest.Build(
            [Row(_alice.Id, 10m), Row(_bob.Id, 2.5m), Row(_nobody.Id, 7.5m)],
            [_alice, _bob, _nobody]);

        Assert.Equal(
            ["alice", "untagged", "bob"],
            rows.Select(r => r.Owner).ToList()); // biggest pile first, untagged named, ties by name
        Assert.Equal(10m, rows.Single(r => r.Owner == "alice").Cost);
        Assert.Equal(0.5, rows.Single(r => r.Owner == "alice").Share, 3);
        Assert.Equal(0.375, rows.Single(r => r.Owner == "untagged").Share, 3);
        Assert.Equal(1.0, rows.Sum(r => r.Share), 6); // shares are of the currency's grand total
    }

    [Fact]
    public void SpendThisScanCannotNameIsItsOwnPile()
    {
        var rows = ChargebackDigest.Build(
            [Row(Sub + "/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/ghost", 4m),
             Row(null, 1m),
             Row(_alice.Id, 5m)],
            [_alice]);

        var unmatched = rows.Single(r => r.Owner == ChargebackDigest.UnmatchedOwner);
        Assert.Equal(5m, unmatched.Cost); // ghost id + id-less row, counted as rows
        Assert.Equal(2, unmatched.ResourceCount);
        Assert.Equal(5m, rows.Single(r => r.Owner == "alice").Cost);
        Assert.DoesNotContain(rows, r => r.Owner == ChargebackDigest.UntaggedOwner); // nothing matched-but-unowned here
    }

    [Fact]
    public void OwnerKeyPrecedenceAndTrimmingFollowTheDocumentedOrder()
    {
        var mixed = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vmD",
            ("Owner", " Alice "), ("team", "z"));

        var rows = ChargebackDigest.Build([Row(mixed.Id, 3m)], [mixed]);

        Assert.Equal("Alice", Assert.Single(rows).Owner); // "owner" beats "team", value trimmed
    }

    [Fact]
    public void CurrenciesAreNeverBlended()
    {
        var rows = ChargebackDigest.Build(
            [Row(_alice.Id, 10m, "EUR"), Row(_alice.Id, 5m, "USD")],
            [_alice]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(1.0, r.Share, 6)); // each currency totals itself
        Assert.Equal(["EUR", "USD"], rows.Select(r => r.Currency).ToList());
    }

    [Fact]
    public void ZeroCostWindowSharesAreZeroNotNaN()
    {
        var rows = ChargebackDigest.Build([Row(_alice.Id, 0m)], [_alice]);

        Assert.Equal(0.0, Assert.Single(rows).Share);
    }

    [Fact]
    public void CsvIsDeterministicAndCarriesItsOwnProvenance()
    {
        var input = new[]
        {
            Row(_alice.Id, 10m), Row(_bob.Id, 2.5m), Row(_nobody.Id, 7.5m), Row("other/id", 4m), Row(null, 1m),
        };
        var csv = ChargebackDigest.Csv(ChargebackDigest.Build(input, [_alice, _bob, _nobody]), Prov());
        var shuffled = ChargebackDigest.Csv(
            ChargebackDigest.Build(input.Reverse().ToList(), [_nobody, _alice, _bob]), Prov());

        Assert.Equal(csv, shuffled);
        Assert.StartsWith("owner,currency,cost,share,resources\n", csv);
        Assert.Contains("untagged,EUR,7.50,30.0%,1", csv);
        Assert.Contains("unmatched-spend,EUR,5.00,20.0%,2", csv);
        Assert.Contains("# source: cost-management-query · scope subscriptions/sub1", csv);
        Assert.Contains("owner > cost-center > costcenter > team", csv);
    }
}
