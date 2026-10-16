using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using Blazor.Diagrams.Core.Geometry;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan [58] per-resource reconcile: every live row gets an honest design-side verdict; nothing destructive is implied.</summary>
public class ReconcileEngineTests
{
    private const string Sub = "/subscriptions/SUB-1";

    private static AdoptableResource Res(string type, string rg, string name, string? location = "westeurope") =>
        new($"{Sub}/resourceGroups/{rg}/providers/{type}/{name}", type, name, location);

    private static AzureNodeDto Node(string id, string name = "", string location = "") => new()
    {
        Id = id,
        TypeKey = "storage-account",
        Name = name,
        Location = location,
        Position = Point.Zero,
        Size = new Size(81, 81),
        GroupId = string.Empty,
    };

    private static readonly AdoptableResource Storage = Res("Microsoft.Storage/storageAccounts", "rg-a", "stprod1a2b");
    private static readonly string StorageId = "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Storage/storageAccounts/stprod1a2b";

    [Fact]
    public void Reconcile_ClassifiesAdoptSyncedAndDrift()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node(StorageId, "stprod1a2b"));                                   // synced
        intent.Nodes.Add(Node("subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/renamed-app", "old-name")); // drift

        var live = new[]
        {
            Storage,
            Res("Microsoft.Web/sites", "rg-a", "renamed-app"),                              // label moved on canvas since adopt
            Res("Microsoft.Network/dnsZones", "rg-a", "contoso.com"),                       // not in design
        };

        var report = ReconcileEngine.Reconcile(intent, live);

        Assert.Equal(StorageId, report.Rows.Single(r => r.State == DesignState.InBothSynced).Id);
        var drift = report.Rows.Single(r => r.State == DesignState.Drifted);
        Assert.Equal("renamed-app", drift.Name);
        Assert.Equal("old-name", drift.DesignName);                                          // honest before/after for the row UI
        Assert.Equal(1, report.CountIn(DesignState.InLiveNotDesign));
        Assert.Equal("subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Network/dnsZones/contoso.com",
            report.Rows.Single(r => r.State == DesignState.InLiveNotDesign).Id);
    }

    [Fact]
    public void Reconcile_DesignOnlyListedSeparately_AndUntrackedCounted()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node("subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/gone-app", "gone-app"));
        intent.Nodes.Add(Node(string.Empty, "hand-made"));                                   // no ARM id

        var report = ReconcileEngine.Reconcile(intent, new[] { Storage });

        Assert.Single(report.DesignOnly);
        Assert.EndsWith("gone-app", report.DesignOnly[0]);
        Assert.Equal(1, report.UntrackedDesignNodes);
    }

    [Fact]
    public void Reconcile_EmptyDesignValuesAreNotDrift()
    {
        // A canvas node that only knows its identity (name/location blank) never claims drift.
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node(StorageId));

        var report = ReconcileEngine.Reconcile(intent, new[] { Storage with { Name = "stprod1a2b", Location = "northeurope" } });

        Assert.Equal(DesignState.InBothSynced, Assert.Single(report.Rows).State);
    }

    [Fact]
    public void Reconcile_LocationDrift_IsCaseInsensitiveOnValueOrdinalOnName()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node(StorageId, "stprod1a2b", "WestEurope"));

        var report = ReconcileEngine.Reconcile(intent, new[] { Storage with { Location = "westeurope" } });
        Assert.Equal(DesignState.InBothSynced, Assert.Single(report.Rows).State);

        var renamed = ReconcileEngine.Reconcile(intent, new[] { Storage with { Name = "STPROD1A2B" } });
        Assert.Equal(DesignState.Drifted, Assert.Single(renamed.Rows).State); // ARM names are case-sensitive; stale casing is real drift
    }

    [Fact]
    public void Reconcile_MatchingIsCaseInsensitive_AndLiveIdsDedupe()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node(StorageId.ToUpperInvariant(), "stprod1a2b"));

        var report = ReconcileEngine.Reconcile(intent, new[] { Storage, Storage });

        var row = Assert.Single(report.Rows);
        Assert.Equal(DesignState.InBothSynced, row.State);
        Assert.Empty(report.DesignOnly);
    }

    [Fact]
    public void Reconcile_RowsAndDesignOnly_OrdinalRegardlessOfInputOrder()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node("b/id", "b"));
        intent.Nodes.Add(Node("a/id", "a"));

        var live = new[]
        {
            new AdoptableResource("/d/id", "Microsoft.Foo/bar", "d", null),
            new AdoptableResource("/c/id", "Microsoft.Foo/bar", "c", null),
        };

        var forward = ReconcileEngine.Reconcile(intent, live);
        var shuffled = ReconcileEngine.Reconcile(intent, live.Reverse().ToList());

        Assert.Equal(new[] { "c/id", "d/id" }, forward.Rows.Select(r => r.Id));
        Assert.Equal(new[] { "a/id", "b/id" }, forward.DesignOnly);
        Assert.Equal(string.Join(",", forward.Rows.Select(r => (r.Id, r.State))), string.Join(",", shuffled.Rows.Select(r => (r.Id, r.State))));
    }

    [Fact]
    public void Reconcile_EmptyInputs_AreEmptyReport()
    {
        var report = ReconcileEngine.Reconcile(new DiagramGraph(), Array.Empty<AdoptableResource>());

        Assert.Empty(report.Rows);
        Assert.Empty(report.DesignOnly);
        Assert.Equal(0, report.UntrackedDesignNodes);
    }

    // ---- [58] per-resource verbs: refresh takes live truth; detach drops identity only. ----

    private static AzureNodeDto ArmNode(string id, string armId, string name = "", string location = "")
    {
        var n = Node(id, name, location);
        n.ArmId = armId;
        return n;
    }

    private static LinkModelDto Link(string sourceId, string targetId) => new()
    {
        SourcePortParentId = sourceId,
        TargetPortParentId = targetId,
    };

    [Fact]
    public void RefreshFromLive_MatchedNodeTakesLiveNameAndLocation_OtherNodesAndInputUntouched()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(ArmNode("canvas-guid-1", StorageId, "old-name", "eastus"));
        var other = ArmNode("canvas-guid-2", "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/keep", "keep", "westeurope");
        intent.Nodes.Add(other);

        var (graph, changed) = ReconcileEngine.RefreshFromLive(intent, Storage);

        Assert.True(changed);
        var refreshed = (AzureNodeDto)graph.Nodes[0];
        Assert.Equal("stprod1a2b", refreshed.Name);
        Assert.Equal("westeurope", refreshed.Location);
        Assert.Equal("canvas-guid-1", refreshed.Id);                                         // identity is intent - never rewritten
        Assert.Equal("keep", ((AzureNodeDto)graph.Nodes[1]).Name);                            // siblings untouched
        Assert.Equal("old-name", ((AzureNodeDto)intent.Nodes[0]).Name);                       // input graph never mutated
    }

    [Fact]
    public void RefreshFromLive_BlankLiveLocationKeepsDesignLocation_AndAgreeingPairIsNoChange()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(ArmNode("g1", StorageId, "stprod1a2b", "northeurope"));
        var live = new AdoptableResource(StorageId, "Microsoft.Storage/storageAccounts", "stprod1a2b", null);

        var (graph, changed) = ReconcileEngine.RefreshFromLive(intent, live);

        Assert.False(changed);
        Assert.Equal("northeurope", ((AzureNodeDto)graph.Nodes[0]).Location);                 // never blanks out a known location
    }

    [Fact]
    public void RefreshFromLive_LegacyIdentityInId_StillMatches()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node(StorageId, "old-name"));                                        // pre-ArmId design: Id carries the ARM id

        var (graph, changed) = ReconcileEngine.RefreshFromLive(intent, Storage);

        Assert.True(changed);
        Assert.Equal("stprod1a2b", ((AzureNodeDto)graph.Nodes[0]).Name);
    }

    [Fact]
    public void Detach_ArmIdNode_LosesIdentityAndReconcilesAsUntrackedIntent()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(ArmNode("canvas-guid-1", StorageId, "stprod1a2b"));
        intent.Links.Add(Link("canvas-guid-1", "canvas-guid-2"));

        var (graph, detached) = ReconcileEngine.Detach(intent, new[] { StorageId });

        Assert.Equal(new[] { StorageId }, detached);
        var node = (AzureNodeDto)graph.Nodes[0];
        Assert.Equal(string.Empty, node.ArmId);
        Assert.Equal("canvas-guid-1", node.Id);                                               // canvas id is intent - kept
        Assert.Equal("canvas-guid-1", graph.Links[0].SourcePortParentId);                     // nothing to remap when the id stays
        var after = ReconcileEngine.Reconcile(graph, new[] { Storage });
        Assert.Equal(StorageId, after.Rows.Single().Id);                                      // live row still shows...
        Assert.Equal(DesignState.InLiveNotDesign, after.Rows.Single().State);                 // ...but as unmanaged by the design
        Assert.Contains("canvas-guid-1", after.DesignOnly);                                // the tile stays design-side as pure intent
        Assert.Equal(0, after.UntrackedDesignNodes);                                       // untracked counts only id-less nodes
        Assert.Equal(StorageId, ((AzureNodeDto)intent.Nodes[0]).ArmId);                       // input never mutated
    }

    [Fact]
    public void Detach_LegacyIdentityInId_RewritesIdAndRemapsLinksAndFrameMembership()
    {
        var intent = new DiagramGraph();
        var frame = Node(StorageId, "old-frame");                                             // legacy frame: ARM id in Id, no ArmId member
        intent.Nodes.Add(frame);
        intent.Nodes.Add(new AzureNodeDto
        {
            Id = "child-guid", TypeKey = "storage-account", Name = "child", GroupId = StorageId,
        });
        intent.Links.Add(Link(StorageId, "child-guid"));

        var (graph, detached) = ReconcileEngine.Detach(intent, new[] { StorageId });

        Assert.Equal(new[] { StorageId }, detached);
        var detachedFrame = (AzureNodeDto)graph.Nodes[0];
        Assert.Equal("detached-1", detachedFrame.Id);
        Assert.Equal("detached-1", ((AzureNodeDto)graph.Nodes[1]).GroupId);                   // child still frames inside
        Assert.Equal("detached-1", graph.Links[0].SourcePortParentId);                        // link follows the node
        Assert.Equal(StorageId, ((AzureNodeDto)intent.Nodes[0]).Id);                          // input never mutated
    }

    [Fact]
    public void Detach_SecondTarget_GetsNextDeterministicId_AndUnknownIdIsHonestNoOp()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node("subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/gone-app", "gone-app"));
        intent.Nodes.Add(Node("subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/dead-app", "dead-app"));

        var (graph, detached) = ReconcileEngine.Detach(intent, new[]
        {
            "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/gone-app",
            "/subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/dead-app/",
            "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/never-was",
        });

        Assert.Equal(2, detached.Count);                                                      // unknown id honestly absent
        Assert.Equal("detached-1", ((AzureNodeDto)graph.Nodes[0]).Id);
        Assert.Equal("detached-2", ((AzureNodeDto)graph.Nodes[1]).Id);
        var again = ReconcileEngine.Detach(intent, new[]
        {
            "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/gone-app",
            "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/dead-app",
        });
        Assert.Equal("detached-1", ((AzureNodeDto)again.Graph.Nodes[0]).Id);                  // same input, same ids
    }
}
