using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Security;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// [86] Expiry watch. The contract: only recognized date keys the providers actually report
/// are watched, every row names its evidence path, unparseable values are skipped, statuses
/// are computed against an injected now, and the data-plane blind spot is a permanent caveat.
/// </summary>
public class ExpiryWatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    private static InventoryItem Item(string name, string type, string? props) =>
        new($"/subscriptions/sub1/resourceGroups/rg/providers/{type}/{name}", name, type, "westeurope", "rg", "sub1",
            new Dictionary<string, string>(),
            props is null ? null : JsonDocument.Parse(props).RootElement.Clone());

    private static InventoryItem Cert(string name, string? props) =>
        Item(name, "Microsoft.Web/certificates", props);

    [Fact]
    public void KnownKeyAtRoot_IsReportedWithEvidencePath()
    {
        var audit = ExpiryWatch.Analyze([Cert("c1", """{"expireDate":"2026-11-30T00:00:00Z"}""")], Now);
        var row = Assert.Single(audit.Rows);
        Assert.Equal("properties.expireDate", row.PropertyPath);
        Assert.Equal(ExpiryStatus.Warning, row.Status); // 45 days out
        Assert.Equal(45, row.DaysRemaining);
    }

    [Fact]
    public void NestedAndDeepKeys_AreFound_EarliestWins()
    {
        var json = """
        {"hostNameSslStates":[{"sslExpirationDate":"2026-12-01T00:00:00Z"}],
         "nested":{"deeper":{"notAfter":"2026-10-20T00:00:00Z"}}}
        """;
        var audit = ExpiryWatch.Analyze([Cert("site1", json)], Now);
        var row = Assert.Single(audit.Rows);
        Assert.Equal("properties.nested.deeper.notAfter", row.PropertyPath);
        Assert.Equal(ExpiryStatus.Critical, row.Status); // 4 days out
    }

    [Fact]
    public void MalformedDates_AreSkippedNotGuessed()
    {
        var audit = ExpiryWatch.Analyze([Cert("c1", """{"expireDate":"tomish"}""")], Now);
        Assert.Empty(audit.Rows);
        Assert.Equal(1, audit.ScannedResources);
        Assert.Equal(0, audit.DatedResources);
    }

    [Fact]
    public void UnknownKeyNames_AreNeverWatched()
    {
        // "expiry" is not a key ARM reports for these types - guessing it would invent dates.
        var audit = ExpiryWatch.Analyze([Cert("c1", """{"expiry":"2026-10-17T00:00:00Z"}""")], Now);
        Assert.Empty(audit.Rows);
    }

    [Fact]
    public void ExpiredAndCriticalAndWarning_BucketByInjectedNow()
    {
        var items = new[]
        {
            Cert("gone", """{"expireDate":"2026-10-15T00:00:00Z"}"""),
            Cert("soon", """{"expireDate":"2026-10-30T00:00:00Z"}"""),
            Cert("watch", """{"expireDate":"2027-01-04T00:00:00Z"}"""),
            Cert("later", """{"expireDate":"2027-06-01T00:00:00Z"}"""),
        };
        var audit = ExpiryWatch.Analyze(items, Now);
        Assert.Equal(
            [("gone", ExpiryStatus.Expired), ("soon", ExpiryStatus.Critical), ("watch", ExpiryStatus.Warning)],
            audit.Rows.Select(r => (r.Name, r.Status)));
        // "later" is dated but beyond 90 days: counted, not alarmed
        Assert.Equal(4, audit.ScannedResources);
    }

    [Fact]
    public void DuplicateScanRows_CollapseToEarliestDate()
    {
        var a = Cert("c1", """{"expireDate":"2026-12-31T00:00:00Z"}""");
        var b = Cert("c1", """{"expireDate":"2026-10-20T00:00:00Z"}""") with { Id = a.Id };
        var audit = ExpiryWatch.Analyze([a, b], Now);
        var row = Assert.Single(audit.Rows);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), row.ExpiresAt);
    }

    [Fact]
    public void RowsSortByDateThenId_Deterministically()
    {
        var items = new[]
        {
            Cert("b", """{"expireDate":"2026-11-02T00:00:00Z"}"""),
            Cert("a", """{"expireDate":"2026-11-01T00:00:00Z"}"""),
            Cert("c", """{"expireDate":"2026-10-31T00:00:00Z"}"""),
        };
        var audit = ExpiryWatch.Analyze(items, Now);
        Assert.Equal(["c", "a", "b"], audit.Rows.Select(r => r.Name));
    }

    [Fact]
    public void Caveats_AreAlwaysPresent_EvenOnGreenData()
    {
        var audit = ExpiryWatch.Analyze([Cert("c1", """{"expireDate":"2030-01-01T00:00:00Z"}""")], Now);
        Assert.Empty(audit.Rows);
        Assert.Contains(audit.Caveats, c => c.Contains("Key Vault", StringComparison.Ordinal));
        Assert.Contains(audit.Caveats, c => c.Contains("unlisted", StringComparison.Ordinal));
    }

    [Fact]
    public void OffsetAwareDates_CompareAsInstants()
    {
        // same instant written with a +02:00 offset - must bucket identically to the UTC form
        var audit = ExpiryWatch.Analyze([Cert("c1", """{"expireDate":"2026-10-16T01:00:00+02:00"}""")], Now);
        var row = Assert.Single(audit.Rows);
        Assert.Equal(ExpiryStatus.Expired, row.Status); // 01:00+02:00 = 23:00Z the day before
    }
}
