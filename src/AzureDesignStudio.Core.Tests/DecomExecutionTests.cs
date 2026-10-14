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

    [Fact]
    public void LockIdFor_IsDeterministicAndNormalized()
    {
        Assert.Equal(DecomExecution.LockIdFor("w1", "/subs/1/rgs/a"), DecomExecution.LockIdFor("w1", "  /subs/1/rgs/a  "));
        Assert.Equal(DecomExecution.LockIdFor("w1", "/subs/1/rgs/a"), DecomExecution.LockIdFor("w1", "/SUBS/1/RGS/A"));
    }

    [Fact]
    public void LockIdFor_IsDistinctPerScopeAndWindow()
    {
        Assert.NotEqual(DecomExecution.LockIdFor("w1", "/subs/1/rgs/a"), DecomExecution.LockIdFor("w1", "/subs/1/rgs/b"));
        Assert.NotEqual(DecomExecution.LockIdFor("w1", "/subs/1/rgs/a"), DecomExecution.LockIdFor("w2", "/subs/1/rgs/a"));
    }

    [Fact]
    public void LockUri_HasTheArmLockShape()
    {
        var id = new Guid("11111111-2222-3333-4444-555555555555");
        Assert.Equal(
            "https://management.azure.com/subs/1/rgs/a/providers/Microsoft.Authorization/locks/11111111-2222-3333-4444-555555555555?api-version=2016-09-01",
            DecomExecution.LockUri("https://management.azure.com/", "/subs/1/rgs/a", id));
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
    public void ParseLockState_OursOnly_YieldsOursAndTheLockId()
    {
        var (ours, foreign, id) = DecomExecution.ParseLockState(
            """{"value":[{"name":"ads-decom-abc","id":"/subs/1/rgs/a/providers/Microsoft.Authorization/locks/11111111-2222-3333-4444-555555555555"}]}""",
            "ads-decom-abc");
        Assert.True(ours);
        Assert.Empty(foreign);
        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), id);
    }

    [Fact]
    public void ParseLockState_OursAndForeign_SeparatesByOrdinalName()
    {
        var (ours, foreign, id) = DecomExecution.ParseLockState(
            """{"value":[{"name":"operator-lock","id":"/subs/1/rgs/a/providers/Microsoft.Authorization/locks/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"},{"name":"ads-decom-abc","id":"/subs/1/rgs/a/providers/Microsoft.Authorization/locks/11111111-2222-3333-4444-555555555555"}]}""",
            "ads-decom-abc");
        Assert.True(ours);
        Assert.Equal(["operator-lock"], foreign);
        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), id);
    }

    [Fact]
    public void ParseLockState_ForeignOnly_LeavesTheWindowUnmatched()
    {
        var (ours, foreign, id) = DecomExecution.ParseLockState(
            """{"value":[{"name":"operator-lock","id":"/subs/1/rgs/a/providers/Microsoft.Authorization/locks/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}]}""",
            "ads-decom-abc");
        Assert.False(ours);
        Assert.Equal(["operator-lock"], foreign);
        Assert.Null(id);
    }

    [Fact]
    public void ParseLockState_NameMatchIsOrdinal_NotCaseInsensitive()
    {
        var (ours, foreign, id) = DecomExecution.ParseLockState(
            """{"value":[{"name":"ADS-DECOM-ABC","id":"/subs/1/rgs/a/providers/Microsoft.Authorization/locks/11111111-2222-3333-4444-555555555555"}]}""",
            "ads-decom-abc");
        Assert.False(ours);
        Assert.Equal(["ADS-DECOM-ABC"], foreign);
        Assert.Null(id);
    }

    [Fact]
    public void ParseLockState_EmptyValueArray_YieldsNoOursNoForeignNoId()
    {
        var (ours, foreign, id) = DecomExecution.ParseLockState("""{"value":[]}""", "ads-decom-abc");
        Assert.False(ours);
        Assert.Empty(foreign);
        Assert.Null(id);
    }

    [Fact]
    public void ParseLockState_MissingValueProperty_YieldsNoOursNoForeignNoId()
    {
        var (ours, foreign, id) = DecomExecution.ParseLockState("""{}""", "ads-decom-abc");
        Assert.False(ours);
        Assert.Empty(foreign);
        Assert.Null(id);
    }
}
