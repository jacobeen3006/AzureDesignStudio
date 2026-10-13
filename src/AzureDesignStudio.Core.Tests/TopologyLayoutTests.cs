using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Discovery.Analysis;
using AzureDesignStudio.Discovery.Inventory;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// 8.2 framing + identity-stable layout. The rescan contract is the point: same ids ⇒
/// same positions; a new resource lands somewhere new without moving anyone else.
/// </summary>
public class TopologyLayoutTests
{
    private const string Sub = "/subscriptions/sub1";
    private const string Vnet = Sub + "/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/vnet1";
    private const string SubnetA = Vnet + "/subnets/subnetA";
    private const string Nic = Sub + "/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/nic1";
    private const string Peer = "/subscriptions/other/resourceGroups/other-rg/providers/Microsoft.Network/virtualNetworks/vnet-peer";

    private static InventoryItem Item(string id, string type, string? props = null) =>
        new(id, id.Split('/')[^1], type, "westeurope",
            id.Contains("/resourceGroups/", StringComparison.OrdinalIgnoreCase)
                ? id.Split('/')[3] : null,
            id.Split('/')[2],
            new Dictionary<string, string>(),
            props is null ? null : System.Text.Json.JsonDocument.Parse(props).RootElement.Clone());

    private static TopologyGraph Graph(params InventoryItem[] items) => TopologyDeriver.Derive(items);

    private static InventoryItem VnetItem() => Item(Vnet, "Microsoft.Network/virtualNetworks",
        """{"subnets":[{"name":"subnetA","properties":{}}]}""");

    private static Dictionary<string, TopologyLayout.SavedPos> AsSaved(TopologyLayoutResult layout) =>
        layout.Nodes.ToDictionary(
            n => n.Node.Id,
            n =>
            {
                // a container-node's persisted frame is its PARENT box (local offset within it)
                var frameId = n.Containers[0] == n.Node.Id && n.Containers.Count > 1 ? n.Containers[1] : n.Containers[0];
                var frame = layout.ContainersById[frameId];
                return new TopologyLayout.SavedPos(frame.Id, n.X - frame.X, n.Y - frame.Y);
            },
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SameGraph_Twice_ProducesIdenticalCoordinates()
    {
        var graph = Graph(VnetItem(), Item(Nic, "Microsoft.Network/networkInterfaces",
            """{"ipConfigurations":[{"properties":{"subnet":{"id":"__S__"}}}]}""".Replace("__S__", SubnetA)));

        var a = TopologyLayout.Layout(graph);
        var b = TopologyLayout.Layout(graph);

        foreach (var nodeA in a.Nodes)
        {
            var nodeB = b.Nodes.Single(n => n.Node.Id == nodeA.Node.Id);
            Assert.Equal((nodeA.X, nodeA.Y), (nodeB.X, nodeB.Y));
        }
    }

    [Fact]
    public void RescanWithNewResource_SavedNodesDoNotMove()
    {
        var graph = Graph(VnetItem(), Item(Nic, "Microsoft.Network/networkInterfaces"));
        var first = TopologyLayout.Layout(graph);
        var saved = AsSaved(first);

        // rescan discovers an extra resource group + node
        var extra = Item("/subscriptions/sub1/resourceGroups/rg2/providers/Microsoft.Web/sites/app",
            "Microsoft.Web/sites");
        var second = TopologyLayout.Layout(Graph(VnetItem(),
            Item(Nic, "Microsoft.Network/networkInterfaces"), extra), saved);

        foreach (var old in first.Nodes)
        {
            var again = second.Nodes.Single(n => n.Node.Id == old.Node.Id);
            Assert.Equal((old.X, old.Y), (again.X, again.Y));
        }

        Assert.Contains(second.Nodes, n => n.Node.Id == extra.Id);
    }

    [Fact]
    public void SavedOffset_InWrongContainer_IsIgnored()
    {
        var graph = Graph(VnetItem());
        var bogus = new Dictionary<string, TopologyLayout.SavedPos>
        {
            [Vnet] = new("rg:somewhere|else", 9999, 9999),
        };

        var layout = TopologyLayout.Layout(graph, bogus);

        Assert.DoesNotContain(layout.Nodes, n => n.X > 9000);
    }

    [Fact]
    public void Subnets_NestInsideVnetBox_WhichNestsInsideRg()
    {
        var layout = TopologyLayout.Layout(Graph(VnetItem()));

        var vnetBox = layout.ContainersById[Vnet];
        var rgBox = layout.ContainersById[vnetBox.Parent!];
        var subBox = layout.ContainersById[rgBox.Parent!];
        Assert.StartsWith("rg:", rgBox.Id);
        Assert.StartsWith("sub:", subBox.Id);

        var subnet = layout.Nodes.Single(n => n.Node.Id == SubnetA);
        Assert.Equal(Vnet, subnet.Containers[0]);
        Assert.InRange(subnet.X, vnetBox.X, vnetBox.X + vnetBox.W - subnet.W);
        Assert.InRange(subnet.Y, vnetBox.Y, vnetBox.Y + vnetBox.H - subnet.H);
        Assert.InRange(vnetBox.X, rgBox.X, rgBox.X + rgBox.W - vnetBox.W);
    }

    [Fact]
    public void SqlChildDatabase_NestsInsideServerBox()
    {
        var server = "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.Sql/servers/sqlsrv";
        var db = server + "/databases/mydb";
        var layout = TopologyLayout.Layout(Graph(
            Item(server, "Microsoft.Sql/servers"),
            Item(db, "Microsoft.Sql/servers/databases")));

        var dbNode = layout.Nodes.Single(n => n.Node.Id == db);
        Assert.Equal(server, dbNode.Containers[0]);
    }

    [Fact]
    public void CrossScopeGhost_SitsInExternalLane_NotInsideRealFrames()
    {
        var graph = Graph(Item(Vnet, "Microsoft.Network/virtualNetworks",
            """{"virtualNetworkPeerings":[{"name":"p","properties":{"remoteVirtualNetwork":{"id":"__P__"}}}]}""".Replace("__P__", Peer)));

        var layout = TopologyLayout.Layout(graph);

        var ghost = layout.Nodes.Single(n => n.Node.Id == Peer);
        Assert.Equal(TopologyLayout.ExternalLane, ghost.Containers[0]);
        var lane = layout.ContainersById[TopologyLayout.ExternalLane];
        var subBox = layout.ContainersById[layout.ContainersById.Values
            .First(c => c.Id.StartsWith("sub:")).Id];
        Assert.True(lane.Y > subBox.Y + subBox.H, "external lane must sit below scanned frames");
    }

    [Fact]
    public void EmptyGraph_StillLaysOut()
    {
        var layout = TopologyLayout.Layout(Graph());
        Assert.Empty(layout.Nodes);
        Assert.Empty(layout.Containers);
    }
}
