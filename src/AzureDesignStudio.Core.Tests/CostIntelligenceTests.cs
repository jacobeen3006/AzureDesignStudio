using System;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Cost;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 9.5 - actual-spend parsing and rollups over Cost Management query fixtures.
/// Offline: hand-built response envelopes, no HTTP.
/// </summary>
public class CostIntelligenceTests
{
    private static JsonElement Response(string columnsJson, string rowsJson) =>
        JsonDocument.Parse("{\"properties\":{\"columns\":" + columnsJson + ",\"rows\":" + rowsJson + "}}").RootElement.Clone();

    // --- parsing ---

    [Fact]
    public void Parse_MapsColumnsByNameNotPosition()
    {
        var doc = Response(
            """[{"name":"ResourceGroupName","type":"Dimension"},{"name":"Cost","type":"Usage"},{"name":"Currency","type":"Usage"},{"name":"ServiceFamily","type":"Dimension"}]""",
            """[["rg1",12.5,"USD","Compute"],["rg2",0.25,"USD","Storage"]]""");

        var rows = CostQueryParser.Parse(doc);
        Assert.Equal(2, rows.Count);
        Assert.Equal("rg1", rows[0].ResourceGroup);
        Assert.Equal(12.5m, rows[0].Cost);
        Assert.Equal("USD", rows[0].Currency);
        Assert.Equal("Storage", rows[1].ServiceFamily);
        Assert.Null(rows[1].ResourceId); // column absent -> null, never guessed
    }

    [Fact]
    public void Parse_NullAndQuotedCosts_AreAmounts()
    {
        var doc = Response(
            """[{"name":"Cost","type":"Usage"}]""",
            """[[null],["7.50"],[3]]""");

        Assert.Equal(new[] { 0m, 7.50m, 3m }, CostQueryParser.Parse(doc).Select(r => r.Cost).ToArray());
    }

    [Fact]
    public void Parse_TotalCostAliasAccepted()
    {
        var doc = Response(
            """[{"name":"TotalCost","type":"Usage"}]""",
            """[[1.00]]""");
        Assert.Equal(1.00m, Assert.Single(CostQueryParser.Parse(doc)).Cost);
    }

    [Fact]
    public void Parse_EnvelopeWithoutCostColumn_IsEmpty()
    {
        var doc = Response("""[{"name":"ResourceId","type":"Dimension"}]""", """[["/subs/x"]]""");
        Assert.Empty(CostQueryParser.Parse(doc));
    }

    [Fact]
    public void Parse_MissingEnvelopeOrRaggedRows_EmptyOrZeroNoThrow()
    {
        Assert.Empty(CostQueryParser.Parse(JsonDocument.Parse("{}").RootElement));
        Assert.Empty(CostQueryParser.Parse(JsonDocument.Parse("""{"properties":{}}""").RootElement));

        var rows = CostQueryParser.Parse(
            Response("""[{"name":"Cost","type":"Usage"}]""", """[[],["a"],[1]]"""));
        Assert.Equal(new[] { 0m, 1m }, rows.Select(r => r.Cost).ToArray()); // ragged drops, garbage cell -> 0
    }

    [Fact]
    public void ToResult_CarriesProvenanceOnEveryFigure()
    {
        var from = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var asOf = from.AddMonths(1).AddMinutes(3);
        var doc = Response(
            """[{"name":"Cost","type":"Usage"},{"name":"Currency","type":"Usage"}]""",
            """[[9.99,"EUR"]]""");

        var result = CostQueryParser.ToResult(doc, "subscriptions/s1", "management.azure.com", from, to, asOf);

        Assert.Equal("cost-management-query", result.Provenance.Source);
        Assert.Equal("subscriptions/s1", result.Provenance.Scope);
        Assert.Equal("management.azure.com", result.Provenance.Region);
        Assert.Equal("EUR", result.Provenance.Currency);
        Assert.Equal(from, result.Provenance.From);
        Assert.Equal(to, result.Provenance.To);
        Assert.Equal(asOf, result.Provenance.AsOf);
    }

    // --- rollups ---

    [Fact]
    public void Rollup_ByServiceFamily_SumsSortsAndShares()
    {
        var rows = new[]
        {
            Row(null, "Compute", 60m),
            Row(null, "Storage", 10m),
            Row(null, "Compute", 25m),
            Row(null, "Networking", 5m),
        };

        var buckets = CostRollup.By(rows, SpendDimension.ServiceFamily);

        Assert.Equal(new[] { "Compute", "Storage", "Networking" }, buckets.Select(b => b.Key).ToArray());
        Assert.Equal(85m, buckets[0].Cost);
        Assert.Equal(0.85, buckets[0].Share, 3);
        Assert.Equal(1.0, buckets.Sum(b => b.Share), 6);
    }

    [Fact]
    public void Rollup_SkipsRowsWithoutTheDimension()
    {
        var rows = new[]
        {
            Row("/subs/s1/rgA/vm1", "Compute", 4m),
            Row(null, "Compute", 2m), // shared/unknown resource: out of Resource buckets, still in Total
        };

        var buckets = CostRollup.By(rows, SpendDimension.Resource);
        Assert.Single(buckets);
        Assert.Equal(4m, buckets[0].Cost);
        Assert.Equal(6m, CostRollup.Total(rows));
    }

    [Fact]
    public void Rollup_ZeroTotal_ZeroShareNoDivByZero()
    {
        var rows = new[] { Row("rg-a", "Compute", 0m), Row("rg-b", "Compute", 0m) };
        var buckets = CostRollup.By(rows, SpendDimension.ResourceGroup);
        Assert.NotEmpty(buckets);
        Assert.All(buckets, b => Assert.Equal(0d, b.Share));
    }

    [Fact]
    public void Rollup_TiesDeterministicByOrdinalKey()
    {
        var rows = new[] { Row("zeta", "x", 5m), Row("alpha", "x", 5m) };
        var buckets = CostRollup.By(rows, SpendDimension.Resource);
        Assert.Equal(new[] { "alpha", "zeta" }, buckets.Select(b => b.Key).ToArray());
    }

    // --- request shape ---

    [Fact]
    public void BuildUsageRequest_CustomWindowAllDimensionsSummedCost()
    {
        var json = ActualSpendClient.BuildUsageRequest(
            new DateTimeOffset(2026, 9, 14, 6, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 14, 6, 30, 0, TimeSpan.Zero));

        using var doc = JsonDocument.Parse(json); // must be valid JSON
        Assert.Contains("\"2026-09-14T06:30:00Z\"", json);
        Assert.Contains("\"2026-10-14T06:30:00Z\"", json);
        Assert.Equal("Custom", doc.RootElement.GetProperty("timeframe").GetString());
        Assert.Equal("Usage", doc.RootElement.GetProperty("type").GetString());
        var names = doc.RootElement.GetProperty("dataset").GetProperty("grouping")
            .EnumerateArray().Select(g => g.GetProperty("name").GetString()).ToArray();
        Assert.Equal(
            new[] { "ResourceId", "ResourceGroupName", "ServiceFamily", "MeterCategory", "ResourceLocation" },
            names);
        Assert.Equal("SUM", doc.RootElement.GetProperty("dataset")
            .GetProperty("aggregation").GetProperty("TotalCost").GetProperty("function").GetString());
    }

    private static SpendRow Row(string? resourceId, string family, decimal cost) =>
        new(resourceId, "rg1", family, "Tests", "westeurope", cost, "USD");
}
