using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AutoMapper;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.Web;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Models;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// A design must come back from save/load, and from the live-plane merge, with the same
    /// structure and the same settings it went in with.
    /// </summary>
    public class DesignRoundTripTests
    {
        private static readonly IMapper Mapper =
            new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()).CreateMapper();

        private static BlazorDiagram NestedDesign()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            vnet.IpSpace = [new AddressSpace { AddressPrefix = "172.16.0.0/16" }];
            var firewallSubnet = new SubnetModel { Name = "AzureFirewallSubnet", AddressPrefix = "172.16.1.0/26" };
            var appSubnet = new SubnetModel { Name = "apps", AddressPrefix = "172.16.2.0/24" };
            var firewall = new AzureFirewallModel { Name = "fw" };
            var plan = new AppServicePlanModel { Name = "plan" };
            var web = new WebAppModel { Name = "web" };

            var diagram = new BlazorDiagram();
            diagram.Groups.Add(vnet);
            diagram.Groups.Add(firewallSubnet);
            diagram.Groups.Add(appSubnet);
            diagram.Groups.Add(plan);
            diagram.Nodes.Add(firewall);
            diagram.Nodes.Add(web);

            vnet.AddChild(firewallSubnet);
            vnet.AddChild(appSubnet);
            firewallSubnet.AddChild(firewall);
            appSubnet.AddChild(plan);
            plan.AddChild(web);
            return diagram;
        }

        // The persisted form: a design is stored as JSON and read back as JsonElement members.
        private static DiagramGraph ThroughJson(DiagramGraph graph) =>
            JsonSerializer.Deserialize<DiagramGraph>(JsonSerializer.Serialize(graph))!;

        private static BlazorDiagram Load(DiagramGraph graph)
        {
            var diagram = new BlazorDiagram();
            diagram.Options.Links.Factory = DataModelFactory.LinkModelFactory;
            DataModelFactory.LoadDiagramFromDto(diagram, graph, Mapper);
            return diagram;
        }

        // Child groups were never entered in the load-time lookup, so a firewall saved inside a
        // subnet - or an App Service plan inside one - came back at the canvas root.
        [Fact]
        public async Task ContentInsideNestedGroupsKeepsItsParentAcrossSaveAndLoad()
        {
            var saved = await DataModelFactory.SaveDiagramToDto(NestedDesign(), Mapper);

            var loaded = Load(ThroughJson(saved));

            var firewall = Assert.Single(loaded.Nodes.OfType<AzureFirewallModel>());
            var firewallSubnet = Assert.IsType<SubnetModel>(firewall.Group);
            Assert.Equal("AzureFirewallSubnet", firewallSubnet.Name);
            Assert.IsType<VirtualNetworkModel>(firewallSubnet.Group);

            var web = Assert.Single(loaded.Nodes.OfType<WebAppModel>());
            var plan = Assert.IsType<AppServicePlanModel>(web.Group);
            var appSubnet = Assert.IsType<SubnetModel>(plan.Group); // a child group inside a child group
            Assert.Equal("apps", appSubnet.Name);
            Assert.IsType<VirtualNetworkModel>(appSubnet.Group);
        }

        [Fact]
        public async Task SecondSaveAfterLoadKeepsTheHierarchy()
        {
            var first = await DataModelFactory.SaveDiagramToDto(NestedDesign(), Mapper);
            var second = await DataModelFactory.SaveDiagramToDto(Load(ThroughJson(first)), Mapper);

            static List<bool> Grouped(DiagramGraph g) =>
                g.Groups.Concat(g.Nodes).OfType<AzureNodeDto>().OrderBy(n => n.Name, StringComparer.Ordinal)
                    .Select(n => n.GroupId.Length > 0).ToList();

            Assert.Equal(Grouped(first), Grouped(second));
            Assert.Equal(5, Grouped(second).Count(inGroup => inGroup));
        }

        [Fact]
        public async Task FailedLoadDoesNotLeaveTheCanvasFrozen()
        {
            var saved = ThroughJson(await DataModelFactory.SaveDiagramToDto(NestedDesign(), Mapper));
            saved.Links.Add(new LinkModelDto { SourcePortParentId = "missing", TargetPortParentId = "also-missing" });

            var diagram = new BlazorDiagram();
            diagram.Options.Links.Factory = DataModelFactory.LinkModelFactory;

            Assert.ThrowsAny<Exception>(() => DataModelFactory.LoadDiagramFromDto(diagram, saved, Mapper));
            Assert.False(diagram.SuspendRefresh);
        }

        public static IEnumerable<object[]> MappedArmTypes() =>
            AdoptEngine.StencilKeyByArmType.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

        // Adopt emits a bare DTO per live resource. Its type-specific members used to arrive as
        // null/0 and were mapped onto the model, which threw for some types and emptied others.
        [Theory]
        [MemberData(nameof(MappedArmTypes))]
        public void AdoptedResourceOfEveryMappedTypeLoads(string armType)
        {
            var id = $"/subscriptions/s/resourceGroups/rg/providers/{armType}/res1";
            var adoption = AdoptEngine.Adopt([new AdoptableResource(id, armType, "res1", "westeurope")]);

            var diagram = Load(adoption.Graph);

            var model = Assert.Single(diagram.Nodes.Cast<NodeModel>().Concat(diagram.Groups));
            Assert.IsNotType<AzureDesignStudio.Core.Models.UnknownNodeModel>(model);
        }

        [Fact]
        public void AdoptedResourcesStartFromTheModelDefaults()
        {
            const string rg = "/subscriptions/s/resourceGroups/rg/providers/";
            var adoption = AdoptEngine.Adopt(
            [
                new AdoptableResource(rg + "Microsoft.Network/virtualNetworks/vnet1", "microsoft.network/virtualnetworks", "vnet1", "westeurope"),
                new AdoptableResource(rg + "Microsoft.Network/azureFirewalls/fw1", "microsoft.network/azurefirewalls", "fw1", "westeurope"),
            ]);

            var diagram = Load(adoption.Graph);

            var vnet = Assert.Single(diagram.Groups.OfType<VirtualNetworkModel>());
            Assert.Equal(new VirtualNetworkModel().IpSpace.Single().AddressPrefix, Assert.Single(vnet.IpSpace).AddressPrefix);
            Assert.Equal("vnet1", vnet.Name);

            var firewall = Assert.Single(diagram.Nodes.OfType<AzureFirewallModel>());
            Assert.Equal(new AzureFirewallModel().Sku, firewall.Sku);
            Assert.NotNull(firewall.FirewallPolicy);
        }

        [Fact]
        public void UnknownResourceKeepsItsPlaceOnTheCanvas()
        {
            var graph = new DiagramGraph();
            graph.Nodes.Add(new AzureNodeDto
            {
                Id = "n1",
                TypeKey = "microsoft.future/widgets",
                Name = "widget",
                Locked = true,
                ImagePath = "assets/azure/widget.svg",
                Position = new Blazor.Diagrams.Core.Geometry.Point(420, 260),
                Size = new Blazor.Diagrams.Core.Geometry.Size(120, 90),
            });

            var node = Assert.IsType<AzureDesignStudio.Core.Models.UnknownNodeModel>(Assert.Single(Load(ThroughJson(graph)).Nodes));

            Assert.Equal(420, node.Position.X);
            Assert.Equal(260, node.Position.Y);
            Assert.Equal(120, node.Size!.Width);
            Assert.True(node.Locked);
            Assert.Equal("assets/azure/widget.svg", node.ImagePath);
            Assert.Equal("microsoft.future/widgets", node.StencilKey);
        }

        // Merge, refresh-from-live and detach all start by copying the design. The copy used to
        // be a base AzureNodeDto carrying only name/position/etc., so every type-specific
        // setting was gone from the graph that was then saved.
        [Fact]
        public async Task LivePlaneMergeKeepsEveryTypeSpecificSetting()
        {
            var intent = await DataModelFactory.SaveDiagramToDto(NestedDesign(), Mapper);

            var merged = LivePlaneMerger.Merge(intent, []).Graph;

            Assert.Equal(JsonSerializer.Serialize(intent), JsonSerializer.Serialize(merged));
            var originals = intent.Groups.Concat(intent.Nodes).ToList();
            var copies = merged.Groups.Concat(merged.Nodes).ToList();
            Assert.All(originals.Zip(copies), pair =>
            {
                Assert.Equal(pair.First.GetType(), pair.Second.GetType());
                Assert.NotSame(pair.First, pair.Second); // still a copy: the input is never mutated
            });

            var vnet = Assert.Single(Load(merged).Groups.OfType<VirtualNetworkModel>());
            Assert.Equal("172.16.0.0/16", Assert.Single(vnet.IpSpace).AddressPrefix);
            Assert.Contains(vnet.Subnets, s => s.AddressPrefix == "172.16.1.0/26");
        }
    }
}

