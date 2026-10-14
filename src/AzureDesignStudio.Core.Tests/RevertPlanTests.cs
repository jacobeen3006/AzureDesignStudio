using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Iac;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 11.4/[57]: the revert plan derives typed undo entries from full what-if before
/// payloads, stays canonical (stable hash, member order, (action, resourceId) ordinal sort),
/// and never claims executability; the token gate carries it to approval without touching
/// the plan-hash identity.
/// </summary>
public class RevertPlanTests
{
    private static WhatIfSummary Summary(params WhatIfChange[] changes) =>
        new() { Status = "Succeeded", Changes = changes };

    private static JsonDocument Doc(RevertPlan plan) => JsonDocument.Parse(plan.DocumentJson);

    [Fact]
    public void Create_YieldsDeleteEntryWithoutPayload()
    {
        var plan = RevertPlan.Build(Summary(
            new WhatIfChange(WhatIfChangeKind.Create, "/subs/x", null, "{\"id\":\"/subs/x\"}", 1)));

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(RevertActionKind.Delete, entry.Action);
        Assert.Equal("/subs/x", entry.ResourceId);
        Assert.Null(entry.PayloadJson);
        Assert.Equal(0, plan.UnrevertableCount);
    }

    [Fact]
    public void CreateWithoutId_IsUnrevertableNotBlindDelete()
    {
        var plan = RevertPlan.Build(Summary(
            new WhatIfChange(WhatIfChangeKind.Create, null, null, "{\"id\":1}", 1)));

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(RevertActionKind.Unrevertable, entry.Action);
        Assert.Null(entry.ResourceId);
        Assert.Equal("the resource ID is unknown", entry.Reason);
        Assert.Equal(1, plan.UnrevertableCount);
    }

    [Fact]
    public void ModifyAndDelete_CarryFullBeforePayloads()
    {
        var before = "{\"properties\":{\"sku\":\"Basic\"},\"location\":\"westeurope\"}";
        var plan = RevertPlan.Build(Summary(
            new WhatIfChange(WhatIfChangeKind.Modify, "/m", before, null, 1),
            new WhatIfChange(WhatIfChangeKind.Delete, "/d", before, null, 1)));

        var restore = plan.Entries.Single(e => e.Action == RevertActionKind.Restore);
        var recreate = plan.Entries.Single(e => e.Action == RevertActionKind.Recreate);
        foreach (var entry in new[] { restore, recreate })
        {
            using var payload = JsonDocument.Parse(entry.PayloadJson!);
            Assert.Equal("Basic", payload.RootElement.GetProperty("properties").GetProperty("sku").GetString());
            Assert.Equal("westeurope", payload.RootElement.GetProperty("location").GetString());
        }
    }

    [Fact]
    public void ValidJsonPayloads_EmbedAsIs_NotJustObjects()
    {
        var plan = RevertPlan.Build(Summary(
            new WhatIfChange(WhatIfChangeKind.Modify, "/null-payload", "null", null, 1),
            new WhatIfChange(WhatIfChangeKind.Modify, "/array", "[1, 2]", null, 1),
            new WhatIfChange(WhatIfChangeKind.Modify, "/scalar", "42", null, 1)));

        Assert.Equal(0, plan.UnrevertableCount);
        Assert.Equal("null", plan.Entries.Single(e => e.ResourceId == "/null-payload").PayloadJson);
        Assert.Equal("[1,2]", plan.Entries.Single(e => e.ResourceId == "/array").PayloadJson);
        Assert.Equal("42", plan.Entries.Single(e => e.ResourceId == "/scalar").PayloadJson);
        // The JSON-null snapshot survives as an explicit null member, not a missing one.
        Assert.Contains("\"payload\":null", plan.DocumentJson);
    }

    [Fact]
    public void NoChangeAndIgnore_AreSkipped()
    {
        var plan = RevertPlan.Build(Summary(
            new WhatIfChange(WhatIfChangeKind.NoChange, "/a", null, null, 0),
            new WhatIfChange(WhatIfChangeKind.Ignore, "/b", null, null, 0)));

        Assert.Empty(plan.Entries);
        Assert.Equal(0, plan.UnrevertableCount);
    }

    [Fact]
    public void MissingOrMalformedBeforePayload_IsUnrevertableNotSilent()
    {
        var plan = RevertPlan.Build(Summary(
            new WhatIfChange(WhatIfChangeKind.Modify, "/no-before", null, null, 1),
            new WhatIfChange(WhatIfChangeKind.Delete, "/bad-json", "{oops", null, 1),
            new WhatIfChange(WhatIfChangeKind.Delete, null, "{\"a\":1}", null, 1)));

        Assert.Equal(3, plan.UnrevertableCount);
        Assert.All(plan.Entries, e => Assert.Equal(RevertActionKind.Unrevertable, e.Action));
        Assert.Contains(plan.Entries, e => e.Reason == "ARM did not return the pre-change payload");
        Assert.Contains(plan.Entries, e => e.Reason == "failed to parse pre-deployment payload");
        Assert.Contains(plan.Entries, e => e.Reason == "the resource ID is unknown" && e.ResourceId is null);
    }

    [Fact]
    public void IndeterminateRows_AreUnrevertableWithHonestReason()
    {
        var plan = RevertPlan.Build(Summary(
            new WhatIfChange(WhatIfChangeKind.Bad, "/b", null, null, 0),
            new WhatIfChange(WhatIfChangeKind.Unknown, "/u", null, null, 0)));

        Assert.Equal(2, plan.UnrevertableCount);
        Assert.All(plan.Entries, e => Assert.Equal("ARM could not classify the predicted change", e.Reason));
    }

    [Fact]
    public void Document_MemberAndEntryOrderAreCanonical()
    {
        var plan = RevertPlan.Build(
            Summary(
                new WhatIfChange(WhatIfChangeKind.Create, "/z", null, null, 1),
                new WhatIfChange(WhatIfChangeKind.Delete, "/y", "{\"k\":1}", null, 1),
                new WhatIfChange(WhatIfChangeKind.Modify, "/x", "{\"k\":2}", null, 1),
                new WhatIfChange(WhatIfChangeKind.Bad, "/m", null, null, 0)),
            coverageNote: "only the first batch was previewed");

        using var doc = Doc(plan);
        Assert.False(doc.RootElement.GetProperty("executable").GetBoolean());
        Assert.Equal(RevertPlan.ExecutableNote, doc.RootElement.GetProperty("executableNote").GetString());
        Assert.Equal("only the first batch was previewed", doc.RootElement.GetProperty("coverageNote").GetString());

        // Fixed top-level member order, entries before unrevertableCount.
        Assert.Equal(
            new[] { "version", "coverageNote", "executable", "executableNote", "entries", "unrevertableCount" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, doc.RootElement.GetProperty("unrevertableCount").GetInt32());

        // Entries sort by serialized action (ordinal), then resource id:
        // delete < recreate < restore < unrevertable - recreate BEFORE restore.
        var ids = doc.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("resourceId").GetString()).ToList();
        Assert.Equal(new[] { "/z", "/y", "/x", "/m" }, ids);
    }

    [Fact]
    public void Build_IsDeterministic_SameDocumentSameHash()
    {
        var changes = new[]
        {
            new WhatIfChange(WhatIfChangeKind.Modify, "/m", "{\"b\":2,\"a\":1}", null, 1),
            new WhatIfChange(WhatIfChangeKind.Create, "/c", null, null, 1),
        };
        var first = RevertPlan.Build(Summary(changes));
        var second = RevertPlan.Build(Summary(changes));

        Assert.Equal(first.DocumentJson, second.DocumentJson);
        Assert.Equal(first.Hash, second.Hash);
    }

    [Fact]
    public void HashOf_FollowsPlanHashShape_AndNullStaysNull()
    {
        var plan = RevertPlan.Build(Summary(new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 1)));

        var hash = RevertPlan.HashOf(plan.DocumentJson);
        Assert.Equal(plan.Hash, hash);
        Assert.StartsWith(RevertPlan.HashPrefix, hash);
        Assert.Equal(71, hash!.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Null(RevertPlan.HashOf(null));
        Assert.Null(RevertPlan.HashOf(string.Empty));
    }

    [Fact]
    public void Gate_CarriesRevertPlanToApprovalPeek_AndOldPeekKeepsWorking()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var summary = Summary(new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 1));
        var revert = RevertPlan.Build(summary);

        gate.Issue("s", "rg", "t", "p", summary, canonicalPlanJson: "{\"plan\":1}",
            planHash: "sha256:" + new string('a', 64), revertPlanJson: revert.DocumentJson);

        Assert.True(gate.TryPeekPlan("sha256:" + new string('a', 64), out var planJson, out var revertJson));
        Assert.Equal("{\"plan\":1}", planJson);
        Assert.Equal(revert.DocumentJson, revertJson);

        Assert.True(gate.TryPeekPlan("sha256:" + new string('a', 64), out var legacyJson));
        Assert.Equal("{\"plan\":1}", legacyJson);
    }

    [Fact]
    public void Gate_PreviewWithoutRevert_PeeksNullRevert()
    {
        var gate = new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl);
        var summary = Summary(new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 1));
        gate.Issue("s", "rg", "t", "p", summary, "{}", "sha256:" + new string('b', 64));

        Assert.True(gate.TryPeekPlan("sha256:" + new string('b', 64), out _, out var revertJson));
        Assert.Null(revertJson);
    }

    [Fact]
    public void Gate_ExpiredPreview_HidesRevertPlanToo()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new WhatIfTokenGate(TimeSpan.FromMinutes(1), () => now);
        var summary = Summary(new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 1));
        gate.Issue("s", "rg", "t", "p", summary, "{}", "sha256:" + new string('c', 64), "{\"entries\":[]}");

        now = now.AddMinutes(2);
        Assert.False(gate.TryPeekPlan("sha256:" + new string('c', 64), out _, out var revertJson));
        Assert.Null(revertJson);
    }
}
