using System;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Analysis;
using AzureDesignStudio.Discovery.Inventory;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// 8.1 relationship derivation. JSON fixtures mirror real ARM property shapes; the core
/// invariant is the plan gate: edges exist only where config carries a real reference.
/// </summary>
public class TopologyDeriverTests
{
    private const string Sub = "/subscriptions/sub1";
    private const string Vnet = Sub + "/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/vnet1";
    private const string SubnetA = Vnet + "/subnets/subnetA";
    private const string Nic = Sub + "/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/nic1";
    private const string Vm = Sub + "/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1";
    private const string Nsg = Sub + "/resourceGroups/rg/providers/Microsoft.Network/networkSecurityGroups/nsg1";

    private static InventoryItem Item(string id, string type, string? propsJson = null)
    {
        JsonElement? raw = null;
        if (propsJson is not null)
            raw = System.Text.Json.JsonDocument.Parse(propsJson).RootElement.Clone();
        return new InventoryItem(id, id.Split('/')[^1], type, "westeurope", "rg", "sub1",
            new System.Collections.Generic.Dictionary<string, string>(), raw);
    }

    private static TopologyGraph Derive(params InventoryItem[] items) => TopologyDeriver.Derive(items);

    /// <summary>Plain JSON fixtures + token fill: raw-interpolated literals fight JSON brace runs.</summary>
    private static string Fill(string template) => template
        .Replace("__SUB__", Sub).Replace("__VM__", Vm).Replace("__NIC__", Nic)
        .Replace("__VNET__", Vnet).Replace("__SUBNET__", SubnetA).Replace("__NSG__", Nsg);

    [Fact]
    public void VmNicSubnetChain_DerivesEachHopFromConfig()
    {
        var vm = Item(Vm, "Microsoft.Compute/virtualMachines",
            Fill("""{"networkProfile":{"networkInterfaces":[{"id":"__NIC__"}]}}"""));
        var nic = Item(Nic, "Microsoft.Network/networkInterfaces",
            Fill("""{"ipConfigurations":[{"name":"ipconfig1","properties":{"subnet":{"id":"__SUBNET__"}}}]}"""));
        var vnet = Item(Vnet, "Microsoft.Network/virtualNetworks",
            """{"subnets":[{"name":"subnetA","properties":{}}]}""");

        var g = Derive(vm, nic, vnet);

        Assert.Contains(g.Edges, e => e.From == Vm && e.To == Nic && e.Kind == TopologyEdgeKind.Uses);
        Assert.Contains(g.Edges, e => e.From == Nic && e.To == SubnetA && e.Kind == TopologyEdgeKind.InSubnet);
        // subnet materialized as a real node even though ARG never lists subnets
        Assert.False(g.Nodes[SubnetA].External);
        Assert.Equal("Microsoft.Network/virtualNetworks/subnets", g.Nodes[SubnetA].Type);
    }

    [Fact]
    public void SubnetNsgAndUdr_ProtectedByAndRoutesVia()
    {
        var vnet = Item(Vnet, "Microsoft.Network/virtualNetworks",
            Fill("""{"subnets":[{"name":"subnetA","properties":{"networkSecurityGroup":{"id":"__NSG__"},"routeTable":{"id":"__SUB__/resourceGroups/rg/providers/Microsoft.Network/routeTables/rt1"}}}]}"""));

        var g = Derive(vnet, Item(Nsg, "Microsoft.Network/networkSecurityGroups"));

        Assert.Contains(g.Edges, e => e.From == SubnetA && e.To == Nsg && e.Kind == TopologyEdgeKind.ProtectedBy);
        Assert.Contains(g.Edges, e => e.Kind == TopologyEdgeKind.RoutesVia && e.To.EndsWith("/routeTables/rt1"));
    }

    [Fact]
    public void CrossScopePeering_BecomesExternalGhost_NotDropped()
    {
        var remote = "/subscriptions/OTHER/resourceGroups/other-rg/providers/Microsoft.Network/virtualNetworks/vnet-peer";
        var vnet = Item(Vnet, "Microsoft.Network/virtualNetworks",
            Fill("""{"virtualNetworkPeerings":[{"name":"toPeer","properties":{"remoteVirtualNetwork":{"id":"__REMOTE__"}}}]}""")
                .Replace("__REMOTE__", remote));

        var g = Derive(vnet);

        var edge = Assert.Single(g.Edges.Where(e => e.Kind == TopologyEdgeKind.PeersWith));
        Assert.Equal(remote, edge.To);
        Assert.True(g.Nodes[edge.To].External);
        Assert.Equal("toPeer", edge.Detail);
        Assert.Equal("Microsoft.Network/virtualNetworks", g.Nodes[edge.To].Type);
    }

    [Fact]
    public void GhostPromoted_WhenRealRowScannedAfterReference()
    {
        // NIC processed before its vnet: subnet starts as ghost, vnet row promotes it.
        var nic = Item(Nic, "Microsoft.Network/networkInterfaces",
            Fill("""{"ipConfigurations":[{"properties":{"subnet":{"id":"__SUBNET__"}}}]}"""));
        var vnet = Item(Vnet, "Microsoft.Network/virtualNetworks",
            """{"subnets":[{"name":"subnetA","properties":{}}]}""");

        var g = Derive(nic, vnet);

        Assert.False(g.Nodes[SubnetA].External);
        // one node only, casing preserved from first registration
        Assert.Single(g.Nodes.Values.Where(n => n.Id.Equals(SubnetA, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void LoadBalancer_BackendPoolTargetsOwningNic_FrontendUsesPublicIp()
    {
        var ipCfgId = Nic + "/ipConfigurations/ipconfig1";
        var pip = Sub + "/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/pip1";
        var lb = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Network/loadBalancers/lb1",
            "Microsoft.Network/loadBalancers",
            Fill("""{"backendAddressPools":[{"name":"bepool","properties":{"backendIPConfigurations":[{"id":"__IPCFG__"}]}}],"frontendIPConfigurations":[{"name":"fe","properties":{"publicIPAddress":{"id":"__PIP__"}}}]}""")
                .Replace("__IPCFG__", ipCfgId).Replace("__PIP__", pip));

        var g = Derive(lb);

        Assert.Contains(g.Edges, e => e.Kind == TopologyEdgeKind.LoadBalances && e.To == Nic && e.Detail == "bepool");
        Assert.Contains(g.Edges, e => e.Kind == TopologyEdgeKind.LoadBalances && e.To == pip && e.Detail == "frontend IP");
    }

    [Fact]
    public void PrivateEndpoint_SubnetAndServiceLinks()
    {
        var sql = Sub + "/resourceGroups/rg/providers/Microsoft.Sql/servers/sqlsrv";
        var pe = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Network/privateEndpoints/pe1",
            "Microsoft.Network/privateEndpoints",
            Fill("""{"subnet":{"id":"__SUBNET__"},"manualPrivateLinkServiceConnections":[{"name":"c","properties":{"privateLinkServiceId":"__SQL__"}}]}""")
                .Replace("__SQL__", sql));

        var g = Derive(pe);

        Assert.Contains(g.Edges, e => e.From == pe.Id && e.To == SubnetA && e.Kind == TopologyEdgeKind.InSubnet);
        // privateLinkServiceId lives inside properties of the connection object
        Assert.Contains(g.Edges, e => e.From == pe.Id && e.To == sql && e.Kind == TopologyEdgeKind.ConnectsTo);
    }

    [Fact]
    public void AppService_HostedInFarm()
    {
        var farm = Sub + "/resourceGroups/rg/providers/Microsoft.Web/serverfarms/plan1";
        var site = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Web/sites/mysite",
            "Microsoft.Web/sites", $$"""{"serverFarmId":"{{farm}}"}""");

        var g = Derive(site, Item(farm, "Microsoft.Web/serverfarms"));

        Assert.Contains(g.Edges, e => e.Kind == TopologyEdgeKind.HostedIn && e.From == site.Id && e.To == farm);
        Assert.False(g.Nodes[farm].External);
    }

    [Fact]
    public void Disks_ManagedByAndDataDiskNameReconstruction()
    {
        var disk = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Compute/disks/datadisk1",
            "Microsoft.Compute/disks", $$"""{"managedBy":"{{Vm}}"}""");
        var vm = Item(Vm, "Microsoft.Compute/virtualMachines",
            """{"storageProfile":{"dataDisks":[{"lun":0,"name":"datadisk1","managedDisk":{"storageAccountType":"Premium_LRS","name":"datadisk1"}}]}}""");

        var g = Derive(vm, disk);

        Assert.Contains(g.Edges, e => e.From == disk.Id && e.To == Vm && e.Kind == TopologyEdgeKind.AttachedTo);
        Assert.Contains(g.Edges, e => e.From == Vm && e.To == disk.Id && e.Kind == TopologyEdgeKind.AttachedTo);
    }

    [Fact]
    public void DiagnosticSettings_MonitoredResourceToWorkspace()
    {
        var ws = Sub + "/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/la1";
        var ds = Item(Sub + "/resourceGroups/rg/providers/microsoft.insights/components/key/diagnosticSettings/ds1",
            "Microsoft.Insights/diagnosticSettings",
            $$"""{"resourceUri":"{{Vm}}","workspaceId":"{{ws}}"}""");

        var g = Derive(ds);

        Assert.Contains(g.Edges, e => e.From == Vm && e.To == ws && e.Kind == TopologyEdgeKind.DiagnosticsTo);
    }

    [Fact]
    public void NoDecorativeEdges_UnrelatedResourcesStayDisconnected()
    {
        var disk = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Compute/disks/orphan",
            "Microsoft.Compute/disks", "{}");
        var pip = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/free",
            "Microsoft.Network/publicIPAddresses", "{}");

        var g = Derive(disk, pip);

        Assert.Empty(g.Edges);
        Assert.Equal(2, g.Nodes.Count);
    }

    [Fact]
    public void MissingProperties_RawNullProducesNodesWithoutEdges()
    {
        var g = Derive(Item(Vm, "Microsoft.Compute/virtualMachines"));
        Assert.Single(g.Nodes);
        Assert.Empty(g.Edges);
    }
}
