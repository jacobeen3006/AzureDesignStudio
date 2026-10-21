using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.SQL;
using AzureDesignStudio.Core.Storage;
using AutoMapper;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using System.Threading.Tasks;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>Offline coverage for endpoint-derived connection labels and link styling.</summary>
    public class LinkTypeTests
    {
        [Fact]
        public void VnetToVnetIsPeeringAndBidirectional()
        {
            var semantics = LinkTypeClassifier.Classify(new VirtualNetworkModel(), new VirtualNetworkModel());

            Assert.Equal("Peering", semantics.Label);
            Assert.True(semantics.Bidirectional);
        }

        [Theory]
        [InlineData("public-ip", "app-gateway", "Public IP")]
        [InlineData("private-endpoint", "storage-account", "Private Link")]
        [InlineData("private-endpoint", "dns-zone", "Private DNS")]
        [InlineData("network-security-group", "virtual-machine", "Network security")]
        [InlineData("virtual-network", "storage-account", "Network")]
        [InlineData("storage-account", "sql-server", "Connection")]
        public void ConnectionLabelFollowsEndpointTypes(string sourceKey, string targetKey, string expected)
        {
            var source = DataModelFactory.CreateNodeModelFromKey(sourceKey, "src", "assets/azure/test.svg");
            var target = DataModelFactory.CreateNodeModelFromKey(targetKey, "dst", "assets/azure/test.svg");

            Assert.Equal(expected, LinkTypeClassifier.Classify(source, target).Label);
        }

        [Fact]
        public void LinkFactoryAppliesLabelAndThickness()
        {
            var pip = new PublicIpModel { Name = "pip" };
            var firewall = new AzureFirewallModel { Name = "firewall" };
            var diagram = new BlazorDiagram();

            var link = DataModelFactory.LinkModelFactory(
                diagram, pip.Ports[1], new SinglePortAnchor(firewall.Ports[0]));

            var typed = Assert.IsType<LinkModel>(link);
            Assert.Equal(3, typed.Width);
            Assert.Equal("Public IP", Assert.Single(typed.Labels).Content);
            Assert.Equal(LinkMarker.Arrow, typed.TargetMarker);
            Assert.Null(typed.SourceMarker);
        }

        [Fact]
        public void VnetLinkGetsBothArrows()
        {
            var first = new VirtualNetworkModel { Name = "vnet-a" };
            var second = new VirtualNetworkModel { Name = "vnet-b" };
            var diagram = new BlazorDiagram();

            var link = Assert.IsType<LinkModel>(DataModelFactory.LinkModelFactory(
                diagram, first.Ports[0], new SinglePortAnchor(second.Ports[0])));

            Assert.Equal("Peering", Assert.Single(link.Labels).Content);
            Assert.Equal(LinkMarker.Arrow, link.SourceMarker);
            Assert.Equal(LinkMarker.Arrow, link.TargetMarker);
        }

        [Fact]
        public void AttachingTheTargetReclassifiesTheLink()
        {
            var first = new VirtualNetworkModel { Name = "vnet-a" };
            var second = new VirtualNetworkModel { Name = "vnet-b" };
            var diagram = new BlazorDiagram();

            // Interactive links start on a position anchor, then snap to a port.
            var link = Assert.IsType<LinkModel>(DataModelFactory.LinkModelFactory(
                diagram, first.Ports[0], new PositionAnchor(Blazor.Diagrams.Core.Geometry.Point.Zero)));

            Assert.Equal("Connection", Assert.Single(link.Labels).Content);

            link.SetTarget(new SinglePortAnchor(second.Ports[0]));

            Assert.Equal("Peering", Assert.Single(link.Labels).Content);
            Assert.Equal(LinkMarker.Arrow, link.SourceMarker);
        }

        [Fact]
        public async Task LinkLabelsAndEndpointsSurviveSaveAndLoad()
        {
            var first = new VirtualNetworkModel { Name = "vnet-a" };
            var second = new VirtualNetworkModel { Name = "vnet-b" };
            var diagram = new BlazorDiagram();
            diagram.Groups.Add(first);
            diagram.Groups.Add(second);
            diagram.Links.Add(DataModelFactory.LinkModelFactory(
                diagram, first.Ports[0], new SinglePortAnchor(second.Ports[0]))!);

            var mapper = new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()).CreateMapper();
            var graph = await DataModelFactory.SaveDiagramToDto(diagram, mapper);

            Assert.NotNull(graph);
            var savedLink = Assert.Single(graph!.Links);
            Assert.Equal(first.Id, savedLink.SourcePortParentId);
            Assert.Equal(second.Id, savedLink.TargetPortParentId);

            var loaded = new BlazorDiagram();
            loaded.Options.Links.Factory = DataModelFactory.LinkModelFactory;
            DataModelFactory.LoadDiagramFromDto(loaded, graph, mapper);

            var loadedLink = Assert.IsType<LinkModel>(Assert.Single(loaded.Links));
            Assert.Equal("Peering", Assert.Single(loadedLink.Labels).Content);
            Assert.Equal(LinkMarker.Arrow, loadedLink.SourceMarker);
            Assert.Equal(3, loadedLink.Width);
        }
    }
}
