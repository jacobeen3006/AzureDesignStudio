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
}
