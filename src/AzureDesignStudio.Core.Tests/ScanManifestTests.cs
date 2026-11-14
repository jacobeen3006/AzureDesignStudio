using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureDesignStudio.Discovery.Governance;
using AzureDesignStudio.Discovery.Scan;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// The scan manifest (Wave B3) is one public shape the drawer, the reports and MCP read. Its
/// four original members keep their JSON names and meaning; governance sources add one state
/// each - collected, incomplete, unavailable or not-requested - and a manifest written before
/// sources existed reads with <c>Sources == null</c> ("not collected"), never as zero findings.
/// </summary>
public class ScanManifestTests
{
    private const string SubA = "00000000-0000-0000-0000-0000000000a1";
    private const string SubB = "00000000-0000-0000-0000-0000000000b2";

    private static ScanCheckpoint Checkpoint(
        IEnumerable<string> completed,
        IEnumerable<string>? requested = null,
        Dictionary<string, string>? failedSourceGroups = null,
        Dictionary<string, int>? sourceRows = null,
        List<DefenderPlan>? plans = null) => new()
        {
            CompletedQueries = [.. completed],
            RequestedSources = [.. requested ?? []],
            FailedSourceGroups = failedSourceGroups ?? [],
            SourceRows = sourceRows ?? [],
            DefenderPlans = plans ?? [],
        };

    // The manifest Desktop wrote before this change (WorkspaceScanSink's private record).
    private sealed record LegacyManifestView(int GroupsCompleted, int GroupsFailed, int Rows, bool Truncated);

    [Fact]
    public void ScanWithoutSources_KeepsTheFourExistingMembers()
    {
        var checkpoint = Checkpoint(["gA", "gB"]);
        checkpoint.FailedQueries["gC"] = "BadRequest: nope";
        checkpoint.Rows = 7;
        checkpoint.AnyTruncated = true;

        var json = ScanManifest.Build("Partial", checkpoint, 3).ToJson();

        var legacy = JsonSerializer.Serialize(new LegacyManifestView(2, 1, 7, true),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        using var written = JsonDocument.Parse(json);
        using var expected = JsonDocument.Parse(legacy);
        var members = written.RootElement.EnumerateObject().Take(4).Select(p => (p.Name, p.Value.GetRawText())).ToArray();
        Assert.Equal(expected.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetRawText())).ToArray(), members);
        Assert.Equal(["groupsCompleted", "groupsFailed", "rows", "truncated", "sources"],
            written.RootElement.EnumerateObject().Select(p => p.Name).ToArray()); // no defenderPlans: not requested

        var manifest = ScanManifest.Read(json)!;
        Assert.Equal((2, 1, 7, true), (manifest.GroupsCompleted, manifest.GroupsFailed, manifest.Rows, manifest.Truncated));
        Assert.Null(manifest.DefenderPlans);
    }

    [Fact]
    public void GroupsCompleted_CountsInventoryGroupsOnly()
    {
        var checkpoint = Checkpoint(["gA", "advisor:gA", "gB", "policy:gA", "defender:gB"], requested: GovernanceSources.All);

        Assert.Equal(2, ScanManifest.Build("Completed", checkpoint, 2).GroupsCompleted);
    }

    [Fact]
    public void SourceStates_CollectedIncompleteUnavailableNotRequested()
    {
        var checkpoint = Checkpoint(
            ["gA", "gB", "advisor:gA", "advisor:gB", "policy:gA", "defender:gA"],
            requested: ["advisor", "policy", "defender"],
            failedSourceGroups: new() { ["policy:gB"] = "AuthorizationFailed: no access" },
            sourceRows: new() { ["advisor"] = 5, ["policy"] = 2, ["defender"] = 1 });

        var sources = ScanManifest.Build("Canceled", checkpoint, 2).Sources!;

        Assert.Equal(["advisor", "policy", "defender"], sources.Keys.ToArray());
        Assert.Equal(new SourceState("collected", 5, null), sources["advisor"]);
        Assert.Equal(new SourceState("unavailable", 2, "AuthorizationFailed: no access"), sources["policy"]);
        Assert.Equal("incomplete", sources["defender"].State);
        Assert.Equal(1, sources["defender"].Rows);
        Assert.Contains("1 of 2", sources["defender"].Detail);

        var onlyAdvisor = ScanManifest.Build("Completed", Checkpoint(["gA", "advisor:gA"], requested: ["advisor"]), 1).Sources!;
        Assert.Equal(new SourceState("collected", 0, null), onlyAdvisor["advisor"]); // collected, zero rows
        Assert.Equal(new SourceState("not-requested", 0, null), onlyAdvisor["policy"]);
        Assert.Equal(new SourceState("not-requested", 0, null), onlyAdvisor["defender"]);
    }

    [Fact]
    public void StateNames_AreThePublishedOnes()
    {
        Assert.Equal(
            ["collected", "incomplete", "unavailable", "not-requested"],
            new[] { SourceState.Collected, SourceState.Incomplete, SourceState.Unavailable, SourceState.NotRequested });
    }

    [Fact]
    public void Unavailable_WinsOverCompletedGroups_AndNamesTheFirstReasonByKey()
    {
        var checkpoint = Checkpoint(
            ["gA", "gB", "policy:gC"],
            requested: ["policy"],
            failedSourceGroups: new() { ["policy:gB"] = "second", ["policy:gA"] = "first" });

        var policy = ScanManifest.Build("Completed", checkpoint, 3).Sources!["policy"];

        Assert.Equal(SourceState.Unavailable, policy.State);
        Assert.Equal("first", policy.Detail);
    }

    [Fact]
    public void Incomplete_WhenNoGroupFinished()
    {
        var policy = ScanManifest.Build("Canceled", Checkpoint(["gA"], requested: ["policy"]), 2).Sources!["policy"];

        Assert.Equal(new SourceState(SourceState.Incomplete, 0, policy.Detail), policy);
        Assert.Contains("0 of 2", policy.Detail);
        Assert.Contains("canceled", policy.Detail);
    }

    [Fact]
    public void LegacyManifest_ReadsWithSourcesNull()
    {
        var manifest = ScanManifest.Read("""{"groupsCompleted":2,"groupsFailed":1,"rows":40,"truncated":false}""")!;

        Assert.Equal(2, manifest.GroupsCompleted);
        Assert.Equal(1, manifest.GroupsFailed);
        Assert.Equal(40, manifest.Rows);
        Assert.False(manifest.Truncated);
        Assert.Null(manifest.Sources); // "not collected (scan predates governance collection)"
        Assert.Null(manifest.DefenderPlans);
        Assert.Equal(2, ScanManifest.Read("""{"groupsCompleted":2}""")!.GroupsCompleted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"groupsCompleted":"two"}""")]
    public void Read_SomethingThatIsNotAManifest_IsNull(string? json)
    {
        Assert.Null(ScanManifest.Read(json));
    }

    [Fact]
    public void DefenderPlans_RoundTrip()
    {
        var plans = new List<DefenderPlan>
        {
            new(SubA, "CloudPosture", "Free"),
            new(SubA, "VirtualMachines", "Standard"),
            new(SubB, "VirtualMachines", "Free"),
        };
        var checkpoint = Checkpoint(["gA", "defender:gA"], requested: ["defender"], sourceRows: new() { ["defender"] = 3 }, plans: plans);

        var built = ScanManifest.Build("Completed", checkpoint, 1);
        var read = ScanManifest.Read(built.ToJson())!;

        Assert.Equal(plans, built.DefenderPlans);
        Assert.Equal(plans, read.DefenderPlans);
        Assert.Equal(built.Sources, read.Sources);
        Assert.Equal(new SourceState(SourceState.Collected, 3, null), read.Sources!["defender"]);
    }

    [Fact]
    public void DefenderRequestedWithNoPlans_IsAnEmptyListNotNull()
    {
        var manifest = ScanManifest.Read(ScanManifest.Build("Completed", Checkpoint(["gA", "defender:gA"], requested: ["defender"]), 1).ToJson())!;

        Assert.NotNull(manifest.DefenderPlans);
        Assert.Empty(manifest.DefenderPlans);
    }

    [Fact]
    public void ToJson_IsCamelCase_AndOmitsNulls()
    {
        var json = ScanManifest.Build("Completed",
            Checkpoint(["gA", "advisor:gA"], requested: ["advisor"], failedSourceGroups: new() { ["advisor:gB"] = "Truncated: cut short" }), 2).ToJson();

        using var doc = JsonDocument.Parse(json);
        var sources = doc.RootElement.GetProperty("sources");
        Assert.Equal("unavailable", sources.GetProperty("advisor").GetProperty("state").GetString());
        Assert.Equal("Truncated: cut short", sources.GetProperty("advisor").GetProperty("detail").GetString());
        Assert.False(sources.GetProperty("policy").TryGetProperty("detail", out _));
        Assert.Equal(0, sources.GetProperty("policy").GetProperty("rows").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("defenderPlans", out _));
    }

    [Fact]
    public void Read_LooksUpSourcesCaseInsensitively()
    {
        var manifest = ScanManifest.Read("""{"groupsCompleted":1,"sources":{"Advisor":{"state":"collected","rows":4}}}""")!;

        Assert.Equal(new SourceState(SourceState.Collected, 4, null), manifest.Sources!["advisor"]);
    }
}
