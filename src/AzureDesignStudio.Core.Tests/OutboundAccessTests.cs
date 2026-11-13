using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.Validation;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>A NAT gateway is a real outbound path: it carries its public IPs and the
    /// subnet that hosts a linked machine references it.</summary>
    public class OutboundAccessTests
    {
        private const string NatId = "[resourceId('Microsoft.Network/natGateways', 'nat1')]";
        private const string PipId = "[resourceId('Microsoft.Network/publicIPAddresses', 'pip1')]";

        private static void Link(BlazorDiagram diagram, NodeModel a, NodeModel b) =>
            diagram.Links.Add(new LinkModel(new SinglePortAnchor(a.Ports[1]), new SinglePortAnchor(b.Ports[0])));

        private static JsonElement Resource(ArmTemplate template, JsonDocument doc, string type) =>
            doc.RootElement.GetProperty("resources").EnumerateArray()
                .First(r => r.GetProperty("type").GetString() == type);

        private static (JsonDocument doc, SubnetModel subnet, JsonElement resource) ExportSubnet(VirtualNetworkModel vnet)
        {
            var template = new ArmTemplate();
            template.AddResource(vnet.GetArmResources());
            var doc = JsonDocument.Parse(template.GenerateArmTemplate());
            return (doc, vnet.Subnets[0], Resource(template, doc, "Microsoft.Network/virtualNetworks/subnets"));
        }

        [Fact]
        public void NatGateway_LinkedToPublicIp_ExportsThatAddress()
        {
            var nat = new NatGatewayModel { Name = "nat1" };
            var pip = new PublicIpModel { Name = "pip1" };
            Link(new BlazorDiagram(), pip, nat);

            var template = new ArmTemplate();
            template.AddResource(nat.GetArmResources());
            using var doc = JsonDocument.Parse(template.GenerateArmTemplate());
            var resource = Resource(template, doc, "Microsoft.Network/natGateways");

            var addresses = resource.GetProperty("properties").GetProperty("publicIpAddresses").EnumerateArray().ToList();
            Assert.Equal(PipId, Assert.Single(addresses).GetProperty("id").GetString());
            Assert.Contains(PipId, resource.GetProperty("dependsOn").EnumerateArray().Select(d => d.GetString()));
        }

        [Fact]
        public void Subnet_WithMachineLinkedToNatGateway_ReferencesIt()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet1" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "snet1" });
            var vm = new VirtualMachineModel { Name = "vm1" };
            subnet.AddChild(vm);
            var nat = new NatGatewayModel { Name = "nat1" };
            Link(new BlazorDiagram(), nat, vm);

            var (doc, _, resource) = ExportSubnet(vnet);
            using (doc)
            {
                Assert.Same(nat, subnet.NatGateway);
                Assert.Equal(NatId, resource.GetProperty("properties").GetProperty("natGateway").GetProperty("id").GetString());
                Assert.Contains(NatId, resource.GetProperty("dependsOn").EnumerateArray().Select(d => d.GetString()));
            }
        }

        [Fact]
        public void Subnet_MachineInAvailabilitySetLinkedToNatGateway_ReferencesIt()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet1" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "snet1" });
            var set = new AvailabilitySetModel { Name = "as1" };
            subnet.AddChild(set);
            var vm = new VirtualMachineModel { Name = "vm1" };
            set.AddChild(vm);
            var nat = new NatGatewayModel { Name = "nat1" };
            Link(new BlazorDiagram(), vm, nat);

            var (doc, _, resource) = ExportSubnet(vnet);
            using (doc)
            {
                Assert.Same(nat, subnet.NatGateway);
                Assert.Equal(NatId, resource.GetProperty("properties").GetProperty("natGateway").GetProperty("id").GetString());
                Assert.Contains(NatId, resource.GetProperty("dependsOn").EnumerateArray().Select(d => d.GetString()));
            }
        }

        [Fact]
        public void Subnet_WithoutNatGateway_OmitsTheMember()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet1" };
            vnet.AddSubnet(new SubnetModel { Name = "snet1" }).AddChild(new VirtualMachineModel { Name = "vm1" });

            var (doc, subnet, resource) = ExportSubnet(vnet);
            using (doc)
            {
                Assert.Null(subnet.NatGateway);
                Assert.False(resource.GetProperty("properties").TryGetProperty("natGateway", out _));
            }
        }

        [Fact]
        public void NatGateway_WithoutPublicIp_IsInvalid()
        {
            var nat = new NatGatewayModel { Name = "nat1" };
            Assert.False(nat.IsValid);
            Assert.Contains(DesignValidator.Validate([nat]), i => i.RuleId == "node-config");

            Link(new BlazorDiagram(), new PublicIpModel { Name = "pip1" }, nat);
            Assert.True(nat.IsValid);
        }
    }
}
