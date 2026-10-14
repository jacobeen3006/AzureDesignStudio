using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using System.Threading.Tasks;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    [Trait("Category", "LiveAzure")]
    public class NetworkTests : TestBase
    {
        public NetworkTests() : base() { }

        [Fact]
        public async Task VnetTest()
        {
            var armTemplate = new ArmTemplate();

            var vnet = new VirtualNetworkModel()
            {
                Name = "ads-vnet",
            };

            armTemplate.AddResource(vnet.GetArmResources());
            await ValidateTemplate(armTemplate);

            vnet.UseResourceGroupLocation = false;
            vnet.Location = "westus";

            armTemplate.RemoveAllResources();
            armTemplate.AddResource(vnet.GetArmResources());
            await ValidateTemplate(armTemplate);

            vnet.UseResourceGroupLocation = true;
            vnet.AddSubnet(new SubnetModel()
            {
                Name = "subnet1",
                AddressPrefix = "10.0.0.0/24"
            });
            vnet.AddSubnet(new SubnetModel()
            {
                Name = "subnet2",
                AddressPrefix = "10.0.1.0/24"
            });

            armTemplate.RemoveAllResources();
            armTemplate.AddResource(vnet.GetArmResources());
            await ValidateTemplate(armTemplate);
        }

        [Fact]
        public async Task PipAndBastionTest()
        {
            var vnet = new VirtualNetworkModel()
            {
                Name = "ads-vnet",
            };
            var bastionSubnet = vnet.AddSubnet(new SubnetModel()
            {
                Name = "AzureBastionSubnet",
                AddressPrefix = "10.0.0.0/24"
            });
            var bastion = new BastionsModel()
            {
                Name = "bastion-host",
            };
            bastionSubnet.AddChild(bastion);

            var firewallSubnet = vnet.AddSubnet(new SubnetModel()
            {
                Name = "AzureFirewallSubnet",
                AddressPrefix = "10.0.1.0/24",
            });
            var firewall = new AzureFirewallModel()
            {
                Name = "firewall"
            };
            firewallSubnet.AddChild(firewall);

            var pip = new PublicIpModel()
            {
                Name = "bastion-pip",
            };
            var diagram = new BlazorDiagram();
            var link = new LinkModel(new SinglePortAnchor(pip.Ports[1]), new SinglePortAnchor(bastion.Ports[0]));
            diagram.Links.Add(link);

            var fwpip = new PublicIpModel()
            {
                Name = "firewall-pip",
            };
            var fwlink = new LinkModel(new SinglePortAnchor(fwpip.Ports[1]), new SinglePortAnchor(firewall.Ports[0]));
            diagram.Links.Add(fwlink);

            var armTemplate = new ArmTemplate();
            armTemplate.AddResource(vnet.GetArmResources());
            armTemplate.AddResource(pip.GetArmResources());
            armTemplate.AddResource(bastion.GetArmResources());
            armTemplate.AddResource(fwpip.GetArmResources());
            armTemplate.AddResource(firewall.GetArmResources());

            await ValidateTemplate(armTemplate);
        }

    }
}
