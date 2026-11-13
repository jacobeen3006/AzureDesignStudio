using System;
using System.Text.Json;
using Xunit;
using AzureDesignStudio.Discovery.Decommission;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Decommission-window execution primitives (v2 plan 13.5): the execution hash canonical form
/// (order-invariant, normalized), the deterministic lock identity (name-independent id/URI/notes),
/// and the ARM lock-list parse. Offline - pure string/hash logic, no network.
/// </summary>
public class DecomExecutionTests
{
    private static readonly DateTimeOffset Purge = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PurgeOtherOffset = new(2026, 10, 20, 2, 0, 0, TimeSpan.FromHours(2));
    private const string PlanHash = "sha256:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void ExecutionHash_IsOrderInvariantAndNormalized()
    {
        var a = DecomExecution.ExecutionHash("w1", PlanHash, " Sub-1 ", Purge,
            ["/subs/1/rgs/B", "/subs/1/rgs/a"]);
        var b = DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge,
            ["  /subs/1/rgs/a  ", "/subs/1/rgs/B"]);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ExecutionHash_IsPurgeOffsetInvariantForTheSameInstant()
    {
        Assert.Equal(
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge, ["/subs/1/rgs/a"]),
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", PurgeOtherOffset, ["/subs/1/rgs/a"]));
    }

    [Fact]
    public void ExecutionHash_HasTheHashPrefixShape()
    {
        var hash = DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge, ["/subs/1/rgs/a"]);
        Assert.StartsWith("sha256:", hash);
        Assert.Equal(71, hash.Length);
        Assert.Matches("^sha256:[0-9a-f]{64}$", hash);
    }

    [Theory]
    [InlineData("w2", "w1")]
    [InlineData("w1", "w1x")]
    public void ExecutionHash_IsSensitiveToWindowId(string other, string baseline)
    {
        Assert.NotEqual(
            DecomExecution.ExecutionHash(baseline, PlanHash, "sub-1", Purge, ["/subs/1/rgs/a"]),
            DecomExecution.ExecutionHash(other, PlanHash, "sub-1", Purge, ["/subs/1/rgs/a"]));
    }

    [Fact]
    public void ExecutionHash_IsSensitiveToPlanHash()
    {
        Assert.NotEqual(
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge, ["/subs/1/rgs/a"]),
            DecomExecution.ExecutionHash("w1", "sha256:" + "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "sub-1", Purge, ["/subs/1/rgs/a"]));
    }

    [Fact]
    public void ExecutionHash_IsSensitiveToSubscription()
    {
        Assert.NotEqual(
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge, ["/subs/1/rgs/a"]),
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-2", Purge, ["/subs/1/rgs/a"]));
    }

    [Fact]
    public void ExecutionHash_IsSensitiveToPurgeDate()
    {
        Assert.NotEqual(
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge, ["/subs/1/rgs/a"]),
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge.AddDays(1), ["/subs/1/rgs/a"]));
    }

    [Fact]
    public void ExecutionHash_IsSensitiveToScopeSet()
    {
        Assert.NotEqual(
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge, ["/subs/1/rgs/a"]),
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge, ["/subs/1/rgs/b"]));
        Assert.NotEqual(
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge, ["/subs/1/rgs/a", "/subs/1/rgs/b"]),
            DecomExecution.ExecutionHash("w1", PlanHash, "sub-1", Purge, ["/subs/1/rgs/a"]));
    }

    // In ARM the last path segment is the lock's name. The window lock therefore has to be
    // written under the same name the runbook, the lock listing and the release all look for.
    [Fact]
    public void LockUri_AddressesTheLockByItsWindowName()
    {
        var lockName = ProtectionWindowPlanner.LockNameFor("w1");

        var uri = DecomExecution.LockUri("https://management.azure.com/", "/subs/1/rgs/a", lockName);

        Assert.Equal(
            $"https://management.azure.com/subs/1/rgs/a/providers/Microsoft.Authorization/locks/{lockName}?api-version=2016-09-01",
            uri);
    }

    [Fact]
    public void LandedLockIsRecognisedAsTheWindowsOwnWhenListedBack()
    {
        var lockName = ProtectionWindowPlanner.LockNameFor("w1");
        var uri = new Uri(DecomExecution.LockUri("https://management.azure.com/", "/subs/1/rgs/a", lockName));

        // ARM lists a lock with name == the segment it was PUT under and id == its path.
        var listed = JsonSerializer.Serialize(new
        {
            value = new[] { new { name = uri.Segments[^1], id = uri.AbsolutePath } },
        });

        var (ours, foreign) = DecomExecution.ParseLockState(listed, lockName);
        Assert.True(ours);
        Assert.Empty(foreign);
    }

    [Theory]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111", true)]
    [InlineData("/SUBSCRIPTIONS/11111111-1111-1111-1111-111111111111/resourceGroups/rg-a", true)]
    [InlineData("  /subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-a/providers/Microsoft.Compute/virtualMachines/vm1  ", true)]
    [InlineData("/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-a", false)]  // another subscription
    [InlineData("/subscriptions/11111111-1111-1111-1111-1111111111110/resourceGroups/rg-a", false)] // prefix of a longer id
    [InlineData("/providers/Microsoft.Management/managementGroups/mg", false)]
    [InlineData("subscriptions/11111111-1111-1111-1111-111111111111", false)]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/", false)]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111//resourceGroups/rg-a", false)]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/../22222222-2222-2222-2222-222222222222", false)]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-a?api-version=1", false)]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-a#x", false)]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/%2e%2e", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsScopeInSubscription_AcceptsOnlyPlainIdsUnderTheSubscription(string? scope, bool expected)
    {
        Assert.Equal(expected, DecomExecution.IsScopeInSubscription(scope, "11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void WindowNotes_HasTheExactRunbookFormat()
    {
        var start = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            "Azure Design Studio decommission window 2026-10-14T08:00:00.0000000+00:00 .. 2026-10-14T10:00:00.0000000+00:00 (window 'w1')",
            DecomExecution.WindowNotes("w1", start, end));
    }

    [Fact]
    public void WindowNotes_RoundTripsOStringsAndNormalizesToUtc()
    {
        var start = DateTimeOffset.Parse("2026-10-14T10:00:00.0000000+02:00");
        var end = DateTimeOffset.Parse("2026-10-14T12:00:00.0000000+02:00");
        Assert.Equal(
            $"Azure Design Studio decommission window {start.ToUniversalTime():O} .. {end.ToUniversalTime():O} (window 'w1')",
            DecomExecution.WindowNotes("w1", start, end));
        // The "O" bytes travel on the wire and rebuild byte-identically.
        var startUtc = DateTimeOffset.Parse(start.ToUniversalTime().ToString("O"), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.RoundtripKind);
        var endUtc = DateTimeOffset.Parse(end.ToUniversalTime().ToString("O"), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(DecomExecution.WindowNotes("w1", start, end), DecomExecution.WindowNotes("w1", startUtc, endUtc));
    }

    [Fact]
    public void LockBody_IsValidJsonWithCanNotDeleteAndEscapedNotes()
    {
        var notes = "He said \"hi\" & left it";
        var body = DecomExecution.LockBody(notes);
        using var doc = JsonDocument.Parse(body);
        var properties = doc.RootElement.GetProperty("properties");
        Assert.Equal("CanNotDelete", properties.GetProperty("level").GetString());
        Assert.Equal(notes, properties.GetProperty("notes").GetString());
    }

    [Fact]
    public void ParseLockState_OursOnly_YieldsOurs()
    {
        var (ours, foreign) = DecomExecution.ParseLockState(
            """{"value":[{"name":"ads-decom-abc","id":"/subs/1/rgs/a/providers/Microsoft.Authorization/locks/ads-decom-abc"}]}""",
            "ads-decom-abc");
        Assert.True(ours);
        Assert.Empty(foreign);
    }

    [Fact]
    public void ParseLockState_OursAndForeign_SeparatesByOrdinalName()
    {
        var (ours, foreign) = DecomExecution.ParseLockState(
            """{"value":[{"name":"operator-lock","id":"/subs/1/rgs/a/providers/Microsoft.Authorization/locks/operator-lock"},{"name":"ads-decom-abc","id":"/subs/1/rgs/a/providers/Microsoft.Authorization/locks/ads-decom-abc"}]}""",
            "ads-decom-abc");
        Assert.True(ours);
        Assert.Equal(["operator-lock"], foreign);
    }

    [Fact]
    public void ParseLockState_ForeignOnly_LeavesTheWindowUnmatched()
    {
        var (ours, foreign) = DecomExecution.ParseLockState(
            """{"value":[{"name":"operator-lock","id":"/subs/1/rgs/a/providers/Microsoft.Authorization/locks/operator-lock"}]}""",
            "ads-decom-abc");
        Assert.False(ours);
        Assert.Equal(["operator-lock"], foreign);
    }

    [Fact]
    public void ParseLockState_NameMatchIsOrdinal_NotCaseInsensitive()
    {
        var (ours, foreign) = DecomExecution.ParseLockState(
            """{"value":[{"name":"ADS-DECOM-ABC","id":"/subs/1/rgs/a/providers/Microsoft.Authorization/locks/ADS-DECOM-ABC"}]}""",
            "ads-decom-abc");
        Assert.False(ours);
        Assert.Equal(["ADS-DECOM-ABC"], foreign);
    }

    [Fact]
    public void ParseLockState_EmptyValueArray_YieldsNoOursNoForeign()
    {
        var (ours, foreign) = DecomExecution.ParseLockState("""{"value":[]}""", "ads-decom-abc");
        Assert.False(ours);
        Assert.Empty(foreign);
    }

    [Fact]
    public void ParseLockState_MissingValueProperty_YieldsNoOursNoForeign()
    {
        var (ours, foreign) = DecomExecution.ParseLockState("""{}""", "ads-decom-abc");
        Assert.False(ours);
        Assert.Empty(foreign);
    }
}
