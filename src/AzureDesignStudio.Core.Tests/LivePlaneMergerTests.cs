using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Blazor.Diagrams;
using AzureDesignStudio.Core;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Geometry;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 12.1 live-plane refresh: live truth folds in, intent (position/links/frames) is never destroyed.</summary>
public class LivePlaneMergerTests
{
    private const string Sub = "/subscriptions/SUB-1";

    private static AdoptableResource Res(string type, string rg, string name, string? location = "westeurope") =>
        new($"{Sub}/resourceGroups/{rg}/providers/{type}/{name}", type, name, location);

    private static AdoptableResource Vnet(string name = "vnet-a", string location = "westeurope") =>
        Res("Microsoft.Network/virtualNetworks", "rg-a", name, location);

    private static AdoptableResource SubnetOf(string vnetName = "vnet-a", string name = "default") =>
        Res("Microsoft.Network/virtualNetworks/subnets", "rg-a", name) with
        { Id = $"{Sub}/resourceGroups/rg-a/providers/Microsoft.Network/virtualNetworks/{vnetName}/subnets/{name}" };

    private static AdoptableResource Storage(string name = "strefresh1a2b") =>
        Res("Microsoft.Storage/storageAccounts", "rg-a", name);

    private static AzureNodeDto Node(string id, string typeKey, string name, double x, double y,
        string groupId = "", double w = 81, double h = 81) => new()
    {
        Id = id,
        TypeKey = typeKey,
        Name = name,
        Position = new Point(x, y),
        Size = new Size(w, h),
        GroupId = groupId,
    };

    private static string Key(AzureNodeDto n) => $"{n.Id}@{n.Position.X},{n.Position.Y}|g:{n.GroupId}";

    private static string Canonical(PlaneMergeResult r) =>
        string.Join(";", r.Graph.Nodes.OfType<AzureNodeDto>().Select(Key)) + "#" +
        string.Join(";", r.Graph.Groups.OfType<AzureNodeDto>().Select(Key)) + "#" +
        $"{r.Updated}/{string.Join(",", r.Added)}/{string.Join(",", r.MissingInLive)}";

    [Fact]
    public void Merge_MatchedNode_TakesLiveNameLocation_KeepsIntentLayout()
    {
        var id = "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Storage/storageAccounts/strefresh1a2b";
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node(id, "storage-account", "old-name", 999, 555, w: 42, h: 43));

        var result = LivePlaneMerger.Merge(intent, new[] { Storage() with { Location = "northeurope" } });

        var node = Assert.IsType<AzureNodeDto>(Assert.Single(result.Graph.Nodes));
        Assert.Equal("strefresh1a2b", node.Name);            // live truth
        Assert.Equal("northeurope", node.Location);          // live truth
        Assert.Equal(999, node.Position.X);                  // intent untouched
        Assert.Equal(555, node.Position.Y);
        Assert.Equal(42, node.Size.Width);
        Assert.Equal(43, node.Size.Height);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Added);
        Assert.Empty(result.MissingInLive);
    }

    [Fact]
    public void Merge_MissingInLive_KeptOnCanvasAndReported()
    {
        var gone = Node("subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/ghost", "web-app", "ghost", 0, 0);
        var intent = new DiagramGraph();
        intent.Nodes.Add(gone);

        var result = LivePlaneMerger.Merge(intent, Array.Empty<AdoptableResource>());

        var kept = Assert.IsType<AzureNodeDto>(Assert.Single(result.Graph.Nodes)); // merge works on a clone
        Assert.Equal(gone.Id, kept.Id);                                     // refresh never deletes intent
        Assert.Equal(gone.Id, result.MissingInLive[0]);
    }

    [Fact]
    public void Merge_LiveOnlyTopLevel_AppendsOneColumnPastBoundingBox()
    {
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node("some/intent/node", "web-app", "mine", 10, 10)); // right edge 91 → next column 260

        var result = LivePlaneMerger.Merge(intent, new[] { Storage() });

        var added = result.Graph.Nodes.OfType<AzureNodeDto>().Single(n => n.Id == result.Added[0]);
        Assert.Equal(260, added.Position.X);
        Assert.Equal(0, added.Position.Y);
        Assert.Equal("storage-account", added.TypeKey);
        Assert.Equal(91, 10 + 81); // sanity of the bounding-box arithmetic the rule uses
    }

    [Fact]
    public void Merge_EmptyDesign_PlacesAdditionsOnAdoptGrid()
    {
        var result = LivePlaneMerger.Merge(new DiagramGraph(), new[] { Storage(), Vnet() });

        var byId = result.Graph.Nodes.Concat(result.Graph.Groups).OfType<AzureNodeDto>()
            .ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, byId["subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Network/virtualNetworks/vnet-a"].Position.X);
        Assert.Equal(260, byId["subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Storage/storageAccounts/strefresh1a2b"].Position.X); // ordinal: vnet slot 0, storage slot 1
    }

    [Fact]
    public void Merge_LiveOnlyChild_JoinsExistingParentFrameAfterExistingChildren()
    {
        var vnetId = "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Network/virtualNetworks/vnet-a";
        var existingSubnetId = vnetId + "/subnets/existing";
        var intent = new DiagramGraph();
        intent.Groups.Add(Node(vnetId, "virtual-network", "vnet-a", 40, 60, w: 141, h: 141));
        intent.Nodes.Add(Node(existingSubnetId, "subnet", "existing", 64, 84, groupId: vnetId));

        var result = LivePlaneMerger.Merge(intent, new[] { Vnet(), SubnetOf(), SubnetOf(name: "new") });

        var added = result.Graph.Groups.OfType<AzureNodeDto>().Single(n => n.Id == vnetId + "/subnets/new");
        Assert.Equal(vnetId, added.GroupId);                                 // subnets are child groups (loader convention)
        Assert.Equal(40 + 24, added.Position.X);                                 // sibling index 2 wraps: column 0 of the second child row
        Assert.Equal(60 + 24 + 120, added.Position.Y);                           // (default took index 1 beside the existing one)
        Assert.Equal(1, result.Updated);                                         // the vnet frame itself matched
        Assert.Equal(new[] { vnetId + "/subnets/default", vnetId + "/subnets/new" }, result.Added);
    }

    [Fact]
    public void Merge_NewParentFrame_PlacedBeforeItsChildren()
    {
        var result = LivePlaneMerger.Merge(new DiagramGraph(), new[] { SubnetOf(), Vnet() }); // shuffled input order

        var vnet = Assert.Single(result.Graph.Groups.OfType<AzureNodeDto>(), g => g.GroupId.Length == 0);
        var subnet = Assert.Single(result.Graph.Groups.OfType<AzureNodeDto>(), g => g.GroupId.Length > 0);
        Assert.Empty(result.Graph.Nodes);
        Assert.Equal(vnet.Id, subnet.GroupId);
        Assert.Equal(0, vnet.Position.X);
        Assert.Equal(vnet.Position.X + 24, subnet.Position.X);
    }

    [Fact]
    public void Merge_LinksAndIntentFields_PreservedByteForByte()
    {
        var id = "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Storage/storageAccounts/strefresh1a2b";
        var link = new LinkModelDto();
        var intent = new DiagramGraph();
        var stored = Node(id, "storage-account", "stale", 5, 5);
        intent.Nodes.Add(stored);
        intent.Nodes.Add(Node("other/id", "web-app", "app", 300, 0));
        intent.Links.Add(link);

        var result = LivePlaneMerger.Merge(intent, new[] { Storage() });

        Assert.Same(link, Assert.Single(result.Graph.Links));
        Assert.Equal(2, result.Graph.Nodes.Count);
    }

    [Fact]
    public void Merge_DoesNotMutateInputGraph()
    {
        var id = "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Storage/storageAccounts/strefresh1a2b";
        var intent = new DiagramGraph();
        var original = Node(id, "storage-account", "old-name", 5, 5);
        intent.Nodes.Add(original);

        LivePlaneMerger.Merge(intent, new[] { Storage(), Vnet() });

        Assert.Equal("old-name", original.Name);   // updates land on the clone
        Assert.Single(intent.Nodes);               // additions land on the clone
    }

    [Fact]
    public void Merge_SameInventoryAnyOrder_ProducesIdenticalMerge()
    {
        var inventory = new[] { Storage(), Vnet(), SubnetOf() };
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node("hand/placed/web", "web-app", "kept", 700, 700));

        var forward = Canonical(LivePlaneMerger.Merge(intent, inventory));
        var shuffled = Canonical(LivePlaneMerger.Merge(intent, inventory.Reverse().ToList()));

        Assert.Equal(forward, shuffled);
    }

    [Fact]
    public void Merge_IdentityIsCaseInsensitive_AndDedupesRepeatedLiveIds()
    {
        var id = "subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Storage/storageAccounts/strefresh1a2b";
        var intent = new DiagramGraph();
        intent.Nodes.Add(Node(id.ToUpperInvariant(), "storage-account", "old", 5, 5));

        var result = LivePlaneMerger.Merge(intent, new[] { Storage(), Storage() });

        var node = Assert.IsType<AzureNodeDto>(Assert.Single(result.Graph.Nodes));
        Assert.Equal("strefresh1a2b", node.Name);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Added);
    }

    [Fact]
    public void Merge_UnknownLiveType_AddsOpenablePlaceholderWithoutIcon()
    {
        var exotic = Res("Microsoft.Novel/widgets", "rg-a", "weird");

        var result = LivePlaneMerger.Merge(new DiagramGraph(), new[] { exotic }, _ => "icons/any.svg");

        var node = Assert.IsType<AzureNodeDto>(Assert.Single(result.Graph.Nodes));
        Assert.Equal("Microsoft.Novel/widgets", node.TypeKey);
        Assert.Equal(string.Empty, node.ImagePath); // never a broken image path for unmapped types
    }

    [Fact]
    public void Merge_IconSeamUsedForMappedAdditions()
    {
        var result = LivePlaneMerger.Merge(new DiagramGraph(), new[] { Storage() }, key => $"custom/{key}.svg");

        var node = Assert.IsType<AzureNodeDto>(Assert.Single(result.Graph.Nodes));
        Assert.Equal("custom/storage-account.svg", node.ImagePath);
    }

    // The whole refresh story keys on the trimmed ARM id surviving the canvas round-trip.
    // Model.Id is a session Guid (read-only in Z.Blazor.Diagrams), so identity rides on
    // ArmId through the AutoMapper name convention. If this ever breaks, a refresh after
    // reopen would re-add every resource as a duplicate - so it is pinned.
    [Fact]
    public async Task CanvasRoundTrip_PreservesArmIdentity_AcrossSaveLoad()
    {
        var mapper = new Mapper(new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()));
        var adoption = AdoptEngine.Adopt(new[] { Storage(), Vnet(), SubnetOf() });
        var diagram = new BlazorDiagram();
        // Production feeds the loader serialized graphs (JsonElement members), not live DTOs.
        var loaded = AdsDesignText.Read(AdsDesignText.Write(adoption.Graph)).Graph;
        DataModelFactory.LoadDiagramFromDto(diagram, loaded, mapper);
        Assert.Equal(3, diagram.Nodes.Count + diagram.Groups.Count);

        var saved = await DataModelFactory.SaveDiagramToDto(diagram, mapper);

        var savedArmIds = saved.Nodes.Concat(saved.Groups).OfType<AzureNodeDto>().Select(n => n.ArmId).ToList();
        var adoptedIds = adoption.Graph.Nodes.Concat(adoption.Graph.Groups).OfType<AzureNodeDto>().Select(n => n.Id).ToList();
        Assert.Equal(3, adoptedIds.Count);
        Assert.Equal(adoptedIds.OrderBy(x => x, StringComparer.Ordinal), savedArmIds.OrderBy(x => x, StringComparer.Ordinal));
    }
}
