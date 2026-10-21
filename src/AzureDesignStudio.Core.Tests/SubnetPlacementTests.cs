using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.SQL;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Offline coverage for creating subnets from the stencil panel and dropping
    /// network appliances into their Azure-preset subnet.
    /// </summary>
    public class SubnetPlacementTests
    {
        [Fact]
        public void SubnetCanOnlyBeDroppedInsideVirtualNetwork()
        {
            var subnet = new SubnetModel { Name = "subnet" };

            Assert.False(subnet.IsDrappable(null!).result);
            Assert.False(subnet.IsDrappable(new SqlServerModel()).result);
            Assert.True(subnet.IsDrappable(new VirtualNetworkModel()).result);
        }

        [Fact]
        public void FirewallDropsOnlyIntoEmptyAzureFirewallSubnet()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "AzureFirewallSubnet" });
            var firewall = new AzureFirewallModel();

            Assert.True(firewall.IsDrappable(subnet).result);

            subnet.Name = "subnet";
            Assert.False(firewall.IsDrappable(subnet).result);

            subnet.Name = "AzureFirewallSubnet";
            subnet.AddChild(new PublicIpModel());
            Assert.False(firewall.IsDrappable(subnet).result);
        }

        [Fact]
        public void SubnetStencilKeyResolvesToEditableModel()
        {
            var model = DataModelFactory.CreateNodeModelFromKey(AdsConstants.Subnet, "subnet", "assets/azure/subnet.svg");

            var subnet = Assert.IsType<SubnetModel>(model);
            Assert.NotNull(subnet.DataFormType);
        }

        [Fact]
        public void NextFreeSubnetPrefixSkipsPrefixesAlreadyInUse()
        {
            var vnet = new VirtualNetworkModel();
            Assert.Equal("10.0.0.0/24", vnet.NextFreeSubnetPrefix());

            vnet.AddSubnet(new SubnetModel { Name = "one", AddressPrefix = "10.0.0.0/24" });
            vnet.AddSubnet(new SubnetModel { Name = "two", AddressPrefix = "10.0.1.0/24" });
            Assert.Equal("10.0.2.0/24", vnet.NextFreeSubnetPrefix());

            var narrow = new VirtualNetworkModel
            {
                IpSpace = [new AddressSpace { AddressPrefix = "192.168.5.0/24" }],
            };
            Assert.Equal("192.168.5.0/24", narrow.NextFreeSubnetPrefix());
        }

        [Fact]
        public void FirewallNeedsALinkedPublicIpToBeValid()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "AzureFirewallSubnet" });
            var firewall = new AzureFirewallModel { Name = "firewall" };
            subnet.AddChild(firewall);

            Assert.False(firewall.HasPublicIp);
            Assert.False(firewall.IsValid);

            var pip = new PublicIpModel { Name = "firewall-pip" };
            var diagram = new BlazorDiagram();
            diagram.Links.Add(new LinkModel(
                new SinglePortAnchor(pip.Ports[1]), new SinglePortAnchor(firewall.Ports[0])));

            Assert.True(firewall.HasPublicIp);
            Assert.True(firewall.IsValid);
            Assert.NotEmpty(firewall.GetArmResources());
        }
    }
}
