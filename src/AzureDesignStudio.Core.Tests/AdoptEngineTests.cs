using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 12.2 read-only adopt core: type table derived from the catalog, deterministic canvas-loadable graph, honest unknown degradation.</summary>
public class AdoptEngineTests
{
    private const string Sub = "/subscriptions/SUB-1";

    private static AdoptableResource Res(string type, string rg, string name, string? tail = null, string? location = "westeurope") =>
        new($"{Sub}/resourceGroups/{rg}/providers/{type}/{name}{tail}", type, name, location);

    private static readonly AdoptableResource Storage = Res("Microsoft.Storage/storageAccounts", "rg-a", "stadoptprod1a2b");
    private static readonly AdoptableResource Vnet = Res("Microsoft.Network/virtualNetworks", "rg-a", "vnet-a");
    private static AdoptableResource SubnetOf(string vnetName = "vnet-a", string name = "default") =>
        Res("Microsoft.Network/virtualNetworks/subnets", "rg-a", name, null) with
        { Id = $"{Sub}/resourceGroups/rg-a/providers/Microsoft.Network/virtualNetworks/{vnetName}/subnets/{name}" };
    private static readonly AdoptableResource SqlServer = Res("Microsoft.Sql/servers", "rg-b", "sql-adopt-prod");
    private static AdoptableResource SqlDbOf(string server = "sql-adopt-prod", string name = "db1") =>
        Res("Microsoft.Sql/servers/databases", "rg-b", name, null) with
        { Id = $"{Sub}/resourceGroups/rg-b/providers/Microsoft.Sql/servers/{server}/databases/{name}" };

    [Fact]
    public void TypeTable_IsDerivedFromCatalog_CaseInsensitive()
    {
        Assert.Equal("storage-account", AdoptEngine.StencilKeyByArmType["microsoft.STORAGE/storageAccounts"]);
        Assert.Equal("virtual-network", AdoptEngine.StencilKeyByArmType["Microsoft.Network/virtualNetworks"]);
        Assert.Equal("subnet", AdoptEngine.StencilKeyByArmType["Microsoft.Network/virtualNetworks/subnets"]);
        Assert.Equal("sql-database", AdoptEngine.StencilKeyByArmType["Microsoft.Sql/servers/databases"]);
        Assert.Equal("web-app", AdoptEngine.StencilKeyByArmType["microsoft.web/sites"]); // sites can't self-identify web vs function; web-app is the general container
        Assert.Equal("dns-zone", AdoptEngine.StencilKeyByArmType["Microsoft.Network/dnsZones"]);
        // child-scoped models (would throw bare) are mapped via the probe wrapper
        Assert.Contains("virtual-network", AdoptEngine.GroupStencilKeys);
        Assert.Contains("sql-server", AdoptEngine.GroupStencilKeys);
        Assert.DoesNotContain("storage-account", AdoptEngine.GroupStencilKeys);
    }

    [Fact]
    public void Adopt_SameInventoryInAnyOrder_ProducesByteIdenticalCanonicalText()
    {
        var inventory = new[] { Storage, Vnet, SubnetOf(), SqlServer, SqlDbOf() };

        var forward = AdsDesignText.Write(AdoptEngine.Adopt(inventory).Graph, "adopted");
        var shuffled = AdsDesignText.Write(AdoptEngine.Adopt(inventory.Reverse().ToList()).Graph, "adopted");

        Assert.Equal(forward, shuffled);
    }

    [Fact]
    public void Adopt_MappedResource_CarriesArmFactsAndCanvasFields()
    {
        var file = AdsDesignText.Read(AdsDesignText.Write(AdoptEngine.Adopt(new[] { Storage }).Graph));
        var node = Assert.IsType<JsonElement>(Assert.Single(file.Graph.Nodes)).Clone();

        Assert.Equal("storage-account", node.GetProperty("TypeKey").GetString());
        Assert.Equal("subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Storage/storageAccounts/stadoptprod1a2b", node.GetProperty("Id").GetString()); // leading slash trimmed, rest kept
        Assert.Equal(node.GetProperty("Id").GetString(), node.GetProperty("ArmId").GetString()); // durable identity rides alongside the canvas id
        Assert.Equal("stadoptprod1a2b", node.GetProperty("Name").GetString());
        Assert.Equal("westeurope", node.GetProperty("Location").GetString());
        Assert.Equal("assets/azure/storage-account.svg", node.GetProperty("ImagePath").GetString()); // catalog icon convention
        Assert.Equal(81d, node.GetProperty("Size").GetProperty("Width").GetDouble());
        Assert.Equal(0d, node.GetProperty("Position").GetProperty("X").GetDouble()); // first slot
    }

    [Fact]
    public void Adopt_ArmNestedChildren_LandInsideTheirParentFrameWithGroupId()
    {
        var result = AdoptEngine.Adopt(new[] { Vnet, SubnetOf(), SqlServer, SqlDbOf() });

        var groups = result.Graph.Groups.Cast<AzureNodeDto>().ToDictionary(g => g.Id);
        var nodes = result.Graph.Nodes.Cast<AzureNodeDto>().ToDictionary(n => n.Id);

        var subnetId = SubnetOf().Id.TrimStart('/');
        Assert.True(groups.ContainsKey(Vnet.Id.TrimStart('/')));
        Assert.Contains(subnetId, groups.Keys); // subnet renders as a frame too - groups section
        Assert.Equal(Vnet.Id.TrimStart('/'), groups[subnetId].GroupId);
        Assert.True(groups[subnetId].Position.X > groups[Vnet.Id.TrimStart('/')].Position.X); // inside the parent, not on its slot

        var dbId = SqlDbOf().Id.TrimStart('/');
        Assert.Contains(dbId, nodes.Keys); // plain node inside a group
        Assert.Equal(SqlServer.Id.TrimStart('/'), nodes[dbId].GroupId);
    }

    [Fact]
    public void Adopt_UnknownType_DegradesToOpenableUnknownAndIsReported()
    {
        var odd = Res("Microsoft.UniqueThing/accounts", "rg-c", "weird-one");
        var result = AdoptEngine.Adopt(new[] { odd });

        var item = Assert.Single(result.Items);
        Assert.False(item.Mapped);
        Assert.Equal("Microsoft.UniqueThing/accounts", item.TypeKey); // full ARM type travels - UnknownNodeModel preserves it

        var node = Assert.Single(result.Graph.Nodes.Cast<AzureNodeDto>());
        Assert.Equal("Microsoft.UniqueThing/accounts", node.TypeKey);
        Assert.Equal(string.Empty, node.ImagePath); // fallback icon, never a broken path
    }

    [Fact]
    public void Adopt_TopLevelSlots_AreDeterministicGrid_AndIconSeamHonored()
    {
        var a = Res("Microsoft.Network/networkSecurityGroups", "rg-a", "nsg-a");
        var b = Res("Microsoft.Network/loadBalancers", "rg-a", "lb-a");

        var result = AdoptEngine.Adopt(new[] { b, a }, key => key == "network-security-group" ? "custom/nsg.svg" : null);
        var nodes = result.Graph.Nodes.Cast<AzureNodeDto>().OrderBy(n => n.Id, StringComparer.Ordinal).ToList();

        Assert.Equal((0d, 0d), (nodes[0].Position.X, nodes[0].Position.Y)); // id-ordinal-first resource owns slot zero
        Assert.Equal(260d, nodes[1].Position.X);
        Assert.Equal("assets/azure/load-balancer.svg", nodes[0].ImagePath); // "loadBalancers" sorts before "networkSecurityGroups"
        Assert.Equal("custom/nsg.svg", nodes[1].ImagePath); // host catalog seam wins where it speaks
    }

    [Fact]
    public void Adopt_EmptyAndBlankInput_YieldsEmptyValidGraph()
    {
        var result = AdoptEngine.Adopt(new[] { new AdoptableResource(" ", "Microsoft.Storage/storageAccounts", "x"), });
        Assert.Empty(result.Items);
        Assert.Empty(result.Graph.Nodes);
        Assert.Contains("\"version\": 1", AdsDesignText.Write(result.Graph)); // still valid .ads.json
    }

    [Fact]
    public void TypeTable_ClaimsWiredNetworkStencilsAndAks()
    {
        // These three were skipped by early probes: their exports only succeed with a
        // throwaway subnet/public-IP wiring, which CreateProbe now fabricates.
        Assert.Equal("app-gateway", AdoptEngine.StencilKeyByArmType["Microsoft.Network/applicationGateways"]);
        Assert.Equal("azure-bastions", AdoptEngine.StencilKeyByArmType["Microsoft.Network/bastionHosts"]);
        Assert.Equal("azure-firewall", AdoptEngine.StencilKeyByArmType["Microsoft.Network/azureFirewalls"]);
        // aks-cluster shipped its ARM model in the follow-up slice: live clusters now adopt
        // into editable nodes, closing the last placeholder the probe left open.
        Assert.Equal("aks-cluster", AdoptEngine.StencilKeyByArmType["Microsoft.ContainerService/managedClusters"]);
    }
}
