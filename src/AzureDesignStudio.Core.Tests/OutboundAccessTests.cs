using AutoMapper;
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.Validation;
using AzureDesignStudio.Core.Web;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using System.Collections.Generic;
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

        private static JsonElement Resource(JsonDocument doc, string type) =>
            doc.RootElement.GetProperty("resources").EnumerateArray()
                .First(r => r.GetProperty("type").GetString() == type);

        private static (JsonDocument doc, SubnetModel subnet, JsonElement resource) ExportSubnet(VirtualNetworkModel vnet)
        {
            var template = new ArmTemplate();
            template.AddResource(vnet.GetArmResources());
            var doc = JsonDocument.Parse(template.GenerateArmTemplate());
            return (doc, vnet.Subnets[0], Resource(doc, "Microsoft.Network/virtualNetworks/subnets"));
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
            var resource = Resource(doc, "Microsoft.Network/natGateways");

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

        private static readonly IMapper Mapper =
            new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()).CreateMapper();

        private static VirtualNetworkModel VnetWith(SubnetModel subnet)
        {
            var vnet = new VirtualNetworkModel { Name = "vnet1" };
            vnet.AddSubnet(subnet);
            return vnet;
        }

        [Fact]
        public void Subnet_Default_ExportsDefaultOutboundAccessTrue()
        {
            var (doc, _, resource) = ExportSubnet(VnetWith(new SubnetModel { Name = "snet1" }));
            using (doc)
            {
                Assert.True(resource.GetProperty("properties").GetProperty("defaultOutboundAccess").GetBoolean());
                Assert.Equal("2024-05-01", resource.GetProperty("apiVersion").GetString());
                Assert.Equal("2024-05-01", Resource(doc, "Microsoft.Network/virtualNetworks").GetProperty("apiVersion").GetString());
            }
        }

        [Fact]
        public void Subnet_Private_ExportsDefaultOutboundAccessFalse()
        {
            var (doc, _, resource) = ExportSubnet(VnetWith(new SubnetModel { Name = "snet1", PrivateSubnet = true }));
            using (doc)
                Assert.False(resource.GetProperty("properties").GetProperty("defaultOutboundAccess").GetBoolean());
        }

        [Fact]
        public void Subnet_Delegated_OmitsDefaultOutboundAccess()
        {
            var subnet = new SubnetModel { Name = "snet1", Delegation = "Microsoft.App/environments", PrivateSubnet = true };
            var (doc, _, resource) = ExportSubnet(VnetWith(subnet));
            using (doc)
            {
                Assert.False(subnet.SupportsPrivateSubnet);
                Assert.False(resource.GetProperty("properties").TryGetProperty("defaultOutboundAccess", out _));
            }
        }

        [Theory]
        [InlineData("AzureFirewallSubnet")]
        [InlineData("GatewaySubnet")]
        [InlineData("AzureBastionSubnet")]
        public void Subnet_ReservedName_OmitsDefaultOutboundAccess(string name)
        {
            var subnet = new SubnetModel { Name = name };
            var (doc, _, resource) = ExportSubnet(VnetWith(subnet));
            using (doc)
            {
                Assert.False(subnet.SupportsPrivateSubnet);
                Assert.False(resource.GetProperty("properties").TryGetProperty("defaultOutboundAccess", out _));
            }
        }

        [Fact]
        public void Subnet_WithAppServicePlanChild_DoesNotSupportPrivateSubnet()
        {
            var subnet = new SubnetModel { Name = "snet1" };
            VnetWith(subnet);
            subnet.AddChild(new AppServicePlanModel { Name = "plan1" });
            var (doc, _, resource) = ExportSubnet((VirtualNetworkModel)subnet.Group!);
            using (doc)
            {
                Assert.False(subnet.SupportsPrivateSubnet);
                Assert.False(resource.GetProperty("properties").TryGetProperty("defaultOutboundAccess", out _));
            }
        }

        [Fact]
        public void Subnet_LoadedFromLegacyDto_IsNotPrivate()
        {
            var json = JsonSerializer.Serialize(Mapper.Map<SubnetModelDto>(new SubnetModel { Name = "snet1" }));
            var legacy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            Assert.True(legacy.Remove("privateSubnet") || legacy.Remove("PrivateSubnet"));

            var dto = JsonSerializer.Deserialize<SubnetModelDto>(JsonSerializer.Serialize(legacy))!;
            Assert.False(Mapper.Map<SubnetModel>(dto).PrivateSubnet);
        }

        [Fact]
        public void Subnet_PrivateSubnet_RoundTripsThroughDto()
        {
            var dto = Mapper.Map<SubnetModelDto>(new SubnetModel { Name = "snet1", PrivateSubnet = true });
            var back = JsonSerializer.Deserialize<SubnetModelDto>(JsonSerializer.Serialize(dto))!;
            Assert.True(Mapper.Map<SubnetModel>(back).PrivateSubnet);
        }
    }
}
