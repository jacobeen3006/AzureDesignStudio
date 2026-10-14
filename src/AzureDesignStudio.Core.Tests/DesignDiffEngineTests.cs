using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// [9.2] design diff engine: stable identity (ARM id wins, survives saved-id churn), leaf-level raw-JSON
/// payload diff with canonical lowercase paths, link identity through per-document id maps, counted
/// diagnostics - pure, deterministic, document-order invariant.
/// </summary>
public class DesignDiffEngineTests
{
    private static object Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static DiagramGraph Graph(IList<object> nodes, IList<object>? groups = null, IList<LinkModelDto>? links = null) =>
        new()
        {
            Nodes = nodes.ToList(),
            Groups = (groups ?? []).ToList(),
            Links = (links ?? []).ToList(),
        };

    private static LinkModelDto Link(string source, string target) =>
        new() { SourcePortParentId = source, TargetPortParentId = target };

    private static void AssertChange(DesignChange actual, DesignChangeKind kind, string subject, string key,
        string? name, string? source = null, string? target = null)
    {
        Assert.Equal(kind, actual.Kind);
        Assert.Equal(subject, actual.SubjectKind);
        Assert.Equal(key, actual.Key);
        Assert.Equal(name, actual.Name);
        Assert.Equal(source, actual.SourceKey);
        Assert.Equal(target, actual.TargetKey);
    }

    // --- EntityKey ---

    [Fact]
    public void EntityKey_ArmIdWins_TrimmedAndLowercased()
    {
        Assert.Equal("arm:/subscriptions/abc", DesignDiffEngine.EntityKey("  /Subscriptions/ABC  ", "node1"));
    }

    [Fact]
    public void EntityKey_FallsBackToId_WhenArmIdBlankOrMissing()
    {
        Assert.Equal("id:node1", DesignDiffEngine.EntityKey("   ", "node1"));
        Assert.Equal("id:node1", DesignDiffEngine.EntityKey(null, "node1"));
    }

    [Fact]
    public void EntityKey_IsNull_WhenBothBlank()
    {
        Assert.Null(DesignDiffEngine.EntityKey(" ", null));
        Assert.Null(DesignDiffEngine.EntityKey(null, ""));
    }

    // --- identity matching ---

    [Fact]
    public void Diff_ArmIdIdentity_MatchesAcrossDifferentSavedIdsAndCasing()
    {
        var before = Graph([Json("""{"Id":"old-id","TypeKey":"vm","ArmId":"/SUBS/abc","Name":"vm"}""")]);
        var after = Graph([Json("""{"Id":"new-id","TypeKey":"vm","ArmId":"/subs/ABC","Name":"vm"}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        Assert.Empty(diff.Changes);
        Assert.Empty(diff.Diagnostics);
    }

    [Fact]
    public void Diff_MemberNames_AreReadCaseInsensitively()
    {
        var before = Graph([Json("""{"id":"n1","typeKey":"vm","armId":"/s/x","name":"N"}""")]);
        var after = Graph([Json("""{"Id":"n1","TypeKey":"vm","ArmId":"/S/X","Name":"N"}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        Assert.Empty(diff.Changes);
    }

    // --- added / removed / retyped ---

    [Fact]
    public void Diff_AddedAndRemoved_KeysSubjectKindName_NoProps()
    {
        var before = Graph(
            [Json("""{"Id":"n1","TypeKey":"vm","Name":"VM1"}""")],
            groups: [Json("""{"Id":"g1","TypeKey":"group","Name":"G1"}""")]);
        var after = Graph(
            [Json("""{"Id":"n2","TypeKey":"vm","Name":"VM2"}""")],
            groups: [Json("""{"Id":"g1","TypeKey":"group","Name":"G1"}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        Assert.Equal(2, diff.Changes.Count);
        AssertChange(diff.Changes[0], DesignChangeKind.Added, "node", "id:n2", "VM2");
        Assert.Empty(diff.Changes[0].Props);
        AssertChange(diff.Changes[1], DesignChangeKind.Removed, "node", "id:n1", "VM1");
        Assert.Empty(diff.Changes[1].Props);
        Assert.False(diff.Changes[0].PropsTruncated);
    }

    [Fact]
    public void Diff_Retyped_WhenTypeKeyDiffersCaseInsensitively_NoProps()
    {
        var before = Graph([Json("""{"Id":"n1","TypeKey":"Microsoft.Compute/VirtualMachine","Name":"vm"}""")]);
        var after = Graph([Json("""{"Id":"n1","TypeKey":"microsoft.storage/storageaccount","Name":"vm"}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        var change = Assert.Single(diff.Changes);
        AssertChange(change, DesignChangeKind.Retyped, "node", "id:n1", "vm");
        Assert.Empty(change.Props);
        Assert.False(change.PropsTruncated);
    }

    [Fact]
    public void Diff_TypeKeyCasingOnly_IsNotRetyped()
    {
        var before = Graph([Json("""{"Id":"n1","TypeKey":"vm","Name":"vm"}""")]);
        var after = Graph([Json("""{"Id":"n1","TypeKey":"VM","Name":"vm"}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        Assert.Empty(diff.Changes); // typeKey is an identity member, never a prop
    }

    [Fact]
    public void Diff_Retyped_RequiresNonEmptyTypeKeyOnBothSides()
    {
        var before = Graph([Json("""{"Id":"n1","TypeKey":"vm","P":1}""")]);
        var after = Graph([Json("""{"Id":"n1","TypeKey":"","P":2}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        var change = Assert.Single(diff.Changes);
        AssertChange(change, DesignChangeKind.Changed, "node", "id:n1", null);
        Assert.Equal([new DesignPropChange("p", "1", "2")], change.Props);
    }

    // --- payload leaf diff ---

    [Fact]
    public void Diff_PropChanges_CarryVerbatimRawJson_StringsQuotedNumbersRaw()
    {
        var before = Graph([Json("""{"Id":"n1","TypeKey":"t","Location":"westeurope","Size":{"Width":1}}""")]);
        var after = Graph([Json("""{"Id":"n1","TypeKey":"t","location":"northeurope","size":{"width":1.5}}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        var change = Assert.Single(diff.Changes);
        AssertChange(change, DesignChangeKind.Changed, "node", "id:n1", null);
        Assert.Equal(
            [
                new DesignPropChange("location", "\"westeurope\"", "\"northeurope\""),
                new DesignPropChange("size.width", "1", "1.5"),
            ],
            change.Props);
    }

    [Fact]
    public void Diff_PositionMove_AppearsOnlyAsChangedLeafProps()
    {
        var before = Graph([Json("""{"Id":"n1","TypeKey":"t","Position":{"X":10,"Y":20}}""")]);
        var after = Graph([Json("""{"Id":"n1","TypeKey":"t","Position":{"X":110,"Y":20}}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        var change = Assert.Single(diff.Changes);
        AssertChange(change, DesignChangeKind.Changed, "node", "id:n1", null);
        Assert.Equal([new DesignPropChange("position.x", "10", "110")], change.Props);
    }

    [Fact]
    public void Diff_NestedPayload_ReportsLeafAndArrayIndexPaths()
    {
        var before = Graph([Json("""{"Id":"n1","TypeKey":"t","Properties":{"Subnets":[{"Name":"a","Net":1},{"Name":"b","Net":2}],"Tags":{"Env":"dev"}}}""")]);
        var after = Graph([Json("""{"Id":"n1","TypeKey":"t","Properties":{"Subnets":[{"Name":"a","Net":9},{"Name":"b","Net":2}],"Tags":{"Env":"prod"}}}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        var change = Assert.Single(diff.Changes);
        AssertChange(change, DesignChangeKind.Changed, "node", "id:n1", null);
        Assert.Equal(
            [
                new DesignPropChange("properties.subnets[0].net", "1", "9"),
                new DesignPropChange("properties.tags.env", "\"dev\"", "\"prod\""),
            ],
            change.Props);
    }

    [Fact]
    public void Diff_NullPresentVsAbsent_CountsAsChange()
    {
        var before = Graph([Json("""{"Id":"n1","TypeKey":"t","P":null}""")]);
        var after = Graph([Json("""{"Id":"n1","TypeKey":"t"}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        var change = Assert.Single(diff.Changes);
        Assert.Equal([new DesignPropChange("p", "null", null)], change.Props);
    }

    // --- links ---

    [Fact]
    public void Diff_Links_Added_IdentityThroughResolvedEndpointKeys()
    {
        var before = Graph(
            [
                Json("""{"Id":"a","TypeKey":"vm","ArmId":"/s/vm1","Name":"vm"}"""),
                Json("""{"Id":"b","TypeKey":"vm","Name":"vm2"}"""),
            ]);
        var after = Graph(
            [
                Json("""{"Id":"z","TypeKey":"vm","ArmId":"/s/vm1","Name":"vm"}"""),
                Json("""{"Id":"b","TypeKey":"vm","Name":"vm2"}"""),
            ],
            links: [Link("z", "b")]);

        var diff = DesignDiffEngine.Diff(before, after);

        var added = Assert.Single(diff.Changes); // the arm-id entity matched across saved ids, so only the link changed
        AssertChange(added, DesignChangeKind.LinkAdded, "link", "arm:/s/vm1->id:b", null, "arm:/s/vm1", "id:b");
        Assert.Empty(added.Props);
    }

    [Fact]
    public void Diff_Links_Removed_WhenIdentityDisappears()
    {
        var before = Graph(
            [Json("""{"Id":"a","TypeKey":"vm","Name":"a"}"""), Json("""{"Id":"b","TypeKey":"vm","Name":"b"}""")],
            links: [Link("a", "b")]);
        var after = Graph(
            [Json("""{"Id":"a","TypeKey":"vm","Name":"a"}"""), Json("""{"Id":"b","TypeKey":"vm","Name":"b"}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        var removed = Assert.Single(diff.Changes);
        AssertChange(removed, DesignChangeKind.LinkRemoved, "link", "id:a->id:b", null, "id:a", "id:b");
        Assert.Empty(removed.Props);
    }

    [Fact]
    public void Diff_Links_UnresolvedEndpoint_DiagnosticAndFallbackKey()
    {
        var before = Graph(
            [Json("""{"Id":"n1","TypeKey":"vm","Name":"n1"}"""), Json("""{"Id":"n2","TypeKey":"vm","Name":"n2"}""")],
            links: [Link("n1", "ghost")]);
        var after = Graph(
            [Json("""{"Id":"n1","TypeKey":"vm","Name":"n1"}"""), Json("""{"Id":"n2","TypeKey":"vm","Name":"n2"}""")],
            links: [Link("n1", "n2")]);

        var diff = DesignDiffEngine.Diff(before, after);

        Assert.Equal(2, diff.Changes.Count);
        AssertChange(diff.Changes[0], DesignChangeKind.LinkAdded, "link", "id:n1->id:n2", null, "id:n1", "id:n2");
        AssertChange(diff.Changes[1], DesignChangeKind.LinkRemoved, "link", "id:n1->id:ghost", null, "id:n1", "id:ghost");
        Assert.Equal(["Unresolved link endpoint 'ghost' in before"], diff.Diagnostics);
    }

    // --- diagnostics ---

    [Fact]
    public void Diff_DuplicateKeys_FoldedIntoCanonicalMinimal_CountedDiagnostic()
    {
        var before = Graph(
            [
                Json("""{"ArmId":"x","Id":"a","P":1}"""),
                Json("""{"ArmId":"x","Id":"b","P":2}"""),
            ]);
        var after = Graph([Json("""{"ArmId":"x","Id":"c","P":2}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        var change = Assert.Single(diff.Changes);
        AssertChange(change, DesignChangeKind.Changed, "node", "arm:x", null);
        Assert.Equal([new DesignPropChange("p", "1", "2")], change.Props); // diffed against the canonical-minimal duplicate
        Assert.Equal(["Duplicate entity keys in before: 1 (first: arm:x)"], diff.Diagnostics);
        Assert.Equal(2, diff.EntityCountBefore);
    }

    [Fact]
    public void Diff_UnidentifiedEntities_CountedDiagnostic_NotMatched()
    {
        var before = Graph(
            [
                Json("""{"Name":"ghost"}"""),
                Json("""{"TypeKey":"t2"}"""),
                Json("""{"Id":"n1","TypeKey":"t","P":1}"""),
            ]);
        var after = Graph([Json("""{"Id":"n1","TypeKey":"t","P":1}""")]);

        var diff = DesignDiffEngine.Diff(before, after);

        Assert.Empty(diff.Changes);
        Assert.Equal(["Unidentified entities in before: 2"], diff.Diagnostics);
        Assert.Equal(3, diff.EntityCountBefore);
    }

    // --- empty / identical / order invariance / truncation ---

    [Fact]
    public void Diff_EmptyGraphs_ZeroChangesZeroDiagnostics()
    {
        var diff = DesignDiffEngine.Diff(new DiagramGraph(), new DiagramGraph());

        Assert.False(diff.AnyChanges);
        Assert.Empty(diff.Changes);
        Assert.Empty(diff.Diagnostics);
        Assert.Equal(0, diff.EntityCountBefore);
        Assert.Equal(0, diff.EntityCountAfter);
    }

    [Fact]
    public void Diff_IdenticalDocuments_ZeroChanges_AcrossNormalizationShapes()
    {
        var before = Graph(
            [Json("""{"Id":"n1","TypeKey":"vm","Name":"vm","Size":{"Width":1}}""")],
            groups: [Json("""{"Id":"g1","TypeKey":"group","Name":"G"}""")],
            links: [Link("n1", "g1")]);
        // after: same content, already-parsed JsonObject shape instead of STJ JsonElement
        var after = Graph(
            [JsonObject.Parse("""{"Id":"n1","TypeKey":"vm","Name":"vm","Size":{"Width":1}}""")!],
            groups: [JsonObject.Parse("""{"Id":"g1","TypeKey":"group","Name":"G"}""")!],
            links: [Link("n1", "g1")]);

        var diff = DesignDiffEngine.Diff(before, after);

        Assert.False(diff.AnyChanges);
        Assert.Empty(diff.Diagnostics);
    }

    [Fact]
    public void Diff_DocumentOrder_DoesNotAffectOutput()
    {
        var n1 = """{"Id":"n1","TypeKey":"vm","Name":"n1"}""";
        var n2a = """{"Id":"n2","TypeKey":"vm","Name":"n2","P":1}""";
        var n2b = """{"Id":"n2","TypeKey":"vm","Name":"n2","P":2}""";
        var n3 = """{"Id":"n3","TypeKey":"vm","Name":"n3"}""";
        var g1 = """{"Id":"g1","TypeKey":"group","Name":"G"}""";

        var before = Graph([Json(n1), Json(n2a), Json(n3)], groups: [Json(g1)], links: [Link("n1", "n2"), Link("n3", "n1")]);
        var after = Graph([Json(n3), Json(n2b), Json(n1)], groups: [Json(g1)], links: [Link("n3", "n1"), Link("n1", "n2")]);
        var shuffled = Graph([Json(n1), Json(n3), Json(n2b)], groups: [Json(g1)], links: [Link("n3", "n1"), Link("n1", "n2")]);

        var diff1 = DesignDiffEngine.Diff(before, after);
        var diff2 = DesignDiffEngine.Diff(before, shuffled);

        Assert.True(diff1.AnyChanges); // the test is meaningful: n2's prop changed
        Assert.Equal(diff1.Changes.Count, diff2.Changes.Count);
        for (var i = 0; i < diff1.Changes.Count; i++)
        {
            Assert.Equal(diff1.Changes[i].Kind, diff2.Changes[i].Kind);
            Assert.Equal(diff1.Changes[i].SubjectKind, diff2.Changes[i].SubjectKind);
            Assert.Equal(diff1.Changes[i].Key, diff2.Changes[i].Key);
            Assert.Equal(diff1.Changes[i].Name, diff2.Changes[i].Name);
            Assert.Equal(diff1.Changes[i].SourceKey, diff2.Changes[i].SourceKey);
            Assert.Equal(diff1.Changes[i].TargetKey, diff2.Changes[i].TargetKey);
            Assert.Equal(diff1.Changes[i].PropsTruncated, diff2.Changes[i].PropsTruncated);
            Assert.Equal(diff1.Changes[i].Props.Count, diff2.Changes[i].Props.Count);
            for (var j = 0; j < diff1.Changes[i].Props.Count; j++)
            {
                Assert.Equal(diff1.Changes[i].Props[j], diff2.Changes[i].Props[j]);
            }
        }
        Assert.Equal(diff1.Diagnostics, diff2.Diagnostics);
        Assert.Equal(diff1.EntityCountBefore, diff2.EntityCountBefore);
        Assert.Equal(diff1.EntityCountAfter, diff2.EntityCountAfter);
    }

    [Fact]
    public void Diff_PropDiffs_TruncatedAtMaxPropertyDiffs_WithFlag()
    {
        static object Entity(int value) =>
            Json($"{{\"Id\":\"n1\",\"TypeKey\":\"t\"," + string.Join(",", Enumerable.Range(0, 210).Select(i => $"\"p{i}\":{value}")) + "}");

        var diff = DesignDiffEngine.Diff(Graph([Entity(0)]), Graph([Entity(1)]));

        var change = Assert.Single(diff.Changes);
        AssertChange(change, DesignChangeKind.Changed, "node", "id:n1", null);
        Assert.Equal(DesignDiffEngine.MaxPropertyDiffs, change.Props.Count);
        Assert.True(change.PropsTruncated);
    }

    // --- DiffGraphJson ---

    private const string ValidGraph = """
        {
            "Groups": [],
            "Nodes": [ { "Id": "n1", "TypeKey": "vm", "Name": "vm" } ],
            "Links": []
        }
        """;

    [Fact]
    public void DiffGraphJson_MalformedBefore_ThrowsNamingBeforeSide()
    {
        var ex = Assert.Throws<AdsDesignFormatException>(() => DesignDiffEngine.DiffGraphJson("{ not json", ValidGraph));
        Assert.Contains("before design", ex.Message);
    }

    [Fact]
    public void DiffGraphJson_MalformedAfter_ThrowsNamingAfterSide()
    {
        var ex = Assert.Throws<AdsDesignFormatException>(() => DesignDiffEngine.DiffGraphJson(ValidGraph, "{ not json"));
        Assert.Contains("after design", ex.Message);
    }

    [Fact]
    public void DiffGraphJson_BothMalformed_NamesBeforeSide()
    {
        var ex = Assert.Throws<AdsDesignFormatException>(() => DesignDiffEngine.DiffGraphJson("{ bad", "{ also bad"));
        Assert.Contains("before design", ex.Message);
    }

    [Fact]
    public void DiffGraphJson_ValidDocuments_ParsesAndDiffs()
    {
        var afterJson = """
            {
                "Groups": [],
                "Nodes": [ { "Id": "n1", "TypeKey": "vm", "Name": "vm", "P": 2 } ],
                "Links": []
            }
            """;

        var diff = DesignDiffEngine.DiffGraphJson(ValidGraph, afterJson);

        var change = Assert.Single(diff.Changes);
        AssertChange(change, DesignChangeKind.Changed, "node", "id:n1", "vm");
        Assert.Equal([new DesignPropChange("p", null, "2")], change.Props);
    }
}
