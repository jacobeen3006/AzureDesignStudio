using System;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Core.AKS;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.ContainerInstance;
using AzureDesignStudio.Core.KeyVault;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// The resources Azure refused, or would have created as empty shells, as the export used
    /// to write them. Each shape below was checked with "az deployment group validate" against
    /// a real subscription; these tests keep it from drifting back.
    /// </summary>
    public class ResourceCompletionTests
    {
        private static JsonElement[] Export(params IAzureResource[] nodes)
        {
            var template = new ArmTemplate();
            foreach (var node in nodes)
            {
                template.AddParameters(node.GetArmParameters());
                template.AddResource(node.GetArmResources());
            }

            using var doc = JsonDocument.Parse(template.GenerateArmTemplate());
            return doc.RootElement.GetProperty("resources").EnumerateArray().Select(r => r.Clone()).ToArray();
        }

        private static JsonElement OfType(JsonElement[] resources, string type) =>
            Assert.Single(resources, r => r.GetProperty("type").GetString() == type);

        private static void Link(NodeModel from, NodeModel to)
        {
            var diagram = new BlazorDiagram();
            diagram.Links.Add(new LinkModel(new SinglePortAnchor(from.Ports[0]), new SinglePortAnchor(to.Ports[0])));
        }

        // --- virtual machine ---

        [Fact]
        public void VirtualMachineExportsWithItsNetworkInterfaceImageAndPasswordParameter()
        {
            var subnet = TestPlacement.NewSubnet();
            var vm = new VirtualMachineModel { Name = "vm-app_01" };
            subnet.AddChild(vm);

            var resources = Export(vm);

            var nic = OfType(resources, "Microsoft.Network/networkInterfaces");
            Assert.Equal("vm-app_01-nic", nic.GetProperty("name").GetString());
            Assert.Contains("ads-snet", nic.GetProperty("properties").GetProperty("ipConfigurations")[0].GetProperty("properties").GetProperty("subnet").GetProperty("id").GetString());
            Assert.True(nic.GetProperty("properties").GetProperty("enableAcceleratedNetworking").GetBoolean()); // the default size requires it

            var machine = OfType(resources, "Microsoft.Compute/virtualMachines").GetProperty("properties");
            Assert.Equal(VmImages.DefaultSize, machine.GetProperty("hardwareProfile").GetProperty("vmSize").GetString());
            Assert.Equal("Canonical", machine.GetProperty("storageProfile").GetProperty("imageReference").GetProperty("publisher").GetString());
            var os = machine.GetProperty("osProfile");
            Assert.Equal("vm-app-01", os.GetProperty("computerName").GetString()); // underscore is not a host-name character
            Assert.Equal("[parameters('adminPassword')]", os.GetProperty("adminPassword").GetString()); // never a stored secret
            Assert.Contains("vm-app_01-nic", machine.GetProperty("networkProfile").GetProperty("networkInterfaces")[0].GetProperty("id").GetString());
        }

        [Fact]
        public void PasswordIsASecureParameterAskedForOnce()
        {
            var subnet = TestPlacement.NewSubnet();
            var a = new VirtualMachineModel { Name = "vm-a" };
            var b = new VirtualMachineModel { Name = "vm-b" };
            subnet.AddChild(a);
            subnet.AddChild(b);
            var template = new ArmTemplate();
            template.AddParameters(a.GetArmParameters());
            template.AddParameters(b.GetArmParameters());

            using var doc = JsonDocument.Parse(template.GenerateArmTemplate());
            var parameter = doc.RootElement.GetProperty("parameters").GetProperty("adminPassword");

            Assert.Equal("securestring", parameter.GetProperty("type").GetString());
            Assert.False(parameter.TryGetProperty("defaultValue", out _));
        }

        [Fact]
        public void VirtualMachineWithoutANetworkIsNotExportable()
        {
            Assert.False(new VirtualMachineModel { Name = "vm1" }.IsValid);
        }

        [Fact]
        public void VirtualMachineInAnAvailabilitySetTakesTheSetsSubnet()
        {
            var subnet = TestPlacement.NewSubnet();
            var set = new AvailabilitySetModel { Name = "avset" };
            subnet.AddChild(set);
            var vm = new VirtualMachineModel { Name = "vm1" };
            set.AddChild(vm);

            Assert.Same(subnet, vm.Subnet);
            Assert.True(set.IsDrappable(subnet).result);
        }

        [Theory]
        [InlineData("windows-2022", "a-very-long-machine-name", "a-very-long-mac")] // NetBIOS: 15
        [InlineData("ubuntu-22.04", "123", "vm123")] // not all digits
        [InlineData("ubuntu-22.04", "--x--y--", "x-y")]
        public void ComputerNameFollowsTheOperatingSystemsRules(string image, string name, string expected)
        {
            Assert.Equal(expected, VmImages.ComputerName(name, VmImages.Resolve(image).IsWindows));
        }

        [Theory]
        [InlineData("azureuser", true)]
        [InlineData("admin", false)]
        [InlineData("root", false)]
        [InlineData("1user", false)]
        [InlineData("a-name-that-is-far-too-long", false)]
        public void AdministratorNamesAzureRefusesAreCaught(string name, bool valid)
        {
            Assert.Equal(valid, VmImages.IsValidAdminUsername(name));
        }

        [Theory]
        [InlineData("Standard_D2s_v6", true)]
        [InlineData("Standard_B2s_v2", true)]
        [InlineData("Standard_B2s", false)]
        [InlineData("Standard_A2_v2", false)]
        public void AcceleratedNetworkingOnlyWhereTheSizeSupportsIt(string size, bool expected)
        {
            Assert.Equal(expected, VmImages.UsesAcceleratedNetworking(size));
        }

        // --- scale set, container instance ---

        [Fact]
        public void ScaleSetExportsAMachineProfileInItsSubnet()
        {
            var subnet = TestPlacement.NewSubnet();
            var scaleSet = new VirtualMachineScaleSetModel { Name = "web-vmss" };
            subnet.AddChild(scaleSet);

            var properties = OfType(Export(scaleSet), "Microsoft.Compute/virtualMachineScaleSets").GetProperty("properties");

            Assert.Equal("Manual", properties.GetProperty("upgradePolicy").GetProperty("mode").GetString());
            var profile = properties.GetProperty("virtualMachineProfile");
            Assert.Equal("web-vmss", profile.GetProperty("osProfile").GetProperty("computerNamePrefix").GetString());
            Assert.Contains("ads-snet", profile.GetProperty("networkProfile").GetProperty("networkInterfaceConfigurations")[0]
                .GetProperty("properties").GetProperty("ipConfigurations")[0].GetProperty("properties").GetProperty("subnet").GetProperty("id").GetString());
        }

        [Fact]
        public void ScaleSetCanBeDroppedOnlyIntoASubnet()
        {
            var scaleSet = new VirtualMachineScaleSetModel { Name = "vmss" };

            Assert.True(scaleSet.IsDrappable(TestPlacement.NewSubnet()).result);
            Assert.False(scaleSet.IsDrappable(new VirtualNetworkModel { Name = "vnet" }).result);
            Assert.False(scaleSet.IsValid); // not placed yet
        }

        [Fact]
        public void ContainerGroupExportsItsContainerWithTheResourceRequests()
        {
            var group = new ContainerInstanceModel { Name = "hello", Cpu = "2", MemoryInGb = "3.5" };

            var properties = OfType(Export(group), "Microsoft.ContainerInstance/containerGroups").GetProperty("properties");

            Assert.False(properties.TryGetProperty("cpu", out _)); // not a member of the group
            var container = Assert.Single(properties.GetProperty("containers").EnumerateArray());
            Assert.Equal(ContainerInstanceModel.DefaultImage, container.GetProperty("properties").GetProperty("image").GetString());
            var requests = container.GetProperty("properties").GetProperty("resources").GetProperty("requests");
            Assert.Equal(2, requests.GetProperty("cpu").GetDouble());
            Assert.Equal(3.5, requests.GetProperty("memoryInGB").GetDouble());
        }

        // --- network ---

        [Fact]
        public void LoadBalancerFrontendUsesALinkedPublicIpOrItsSubnet()
        {
            var publicLb = new LoadBalancerModel { Name = "lb-public" };
            var address = new PublicIpModel { Name = "pip-lb" };
            Link(address, publicLb);
            var frontend = OfType(Export(publicLb), "Microsoft.Network/loadBalancers")
                .GetProperty("properties").GetProperty("frontendIPConfigurations")[0].GetProperty("properties");
            Assert.Contains("pip-lb", frontend.GetProperty("publicIPAddress").GetProperty("id").GetString());

            var internalLb = TestPlacement.MakeExportable(new LoadBalancerModel { Name = "lb-internal" });
            frontend = OfType(Export(internalLb), "Microsoft.Network/loadBalancers")
                .GetProperty("properties").GetProperty("frontendIPConfigurations")[0].GetProperty("properties");
            Assert.Contains("ads-snet", frontend.GetProperty("subnet").GetProperty("id").GetString());

            Assert.False(new LoadBalancerModel { Name = "lb-nowhere" }.IsValid);
        }

        [Fact]
        public void PrivateEndpointTakesItsTargetAndGroupIdFromALink()
        {
            var subnet = TestPlacement.NewSubnet();
            var endpoint = new PrivateEndpointModel { Name = "pe-vault" };
            subnet.AddChild(endpoint);
            var vault = new KeyVaultModel { Name = "kv-ads-01" };
            Assert.False(endpoint.IsValid); // no target yet
            Link(endpoint, vault);

            var properties = OfType(Export(endpoint), "Microsoft.Network/privateEndpoints").GetProperty("properties");

            Assert.Contains("ads-snet", properties.GetProperty("subnet").GetProperty("id").GetString());
            var connection = properties.GetProperty("privateLinkServiceConnections")[0].GetProperty("properties");
            Assert.Contains("kv-ads-01", connection.GetProperty("privateLinkServiceId").GetString());
            Assert.Equal("vault", connection.GetProperty("groupIds")[0].GetString());
        }

        [Fact]
        public void PrivateEndpointToAResourceOffTheCanvasUsesTheTypedId()
        {
            var endpoint = TestPlacement.MakeExportable(new PrivateEndpointModel { Name = "pe-sql" });

            var connection = OfType(Export(endpoint), "Microsoft.Network/privateEndpoints")
                .GetProperty("properties").GetProperty("privateLinkServiceConnections")[0].GetProperty("properties");

            Assert.Equal(TestPlacement.SampleTargetId, connection.GetProperty("privateLinkServiceId").GetString());
            Assert.Equal("sqlServer", connection.GetProperty("groupIds")[0].GetString());
        }

        [Fact]
        public void ExpressRouteWritesTheSkuAndProviderTheApiDefines()
        {
            var circuit = new ExpressRouteCircuitModel { Name = "er-1", BandwidthInMbps = 200, SkuTier = "Premium", SkuFamily = "UnlimitedData" };

            var resource = OfType(Export(circuit), "Microsoft.Network/expressRouteCircuits");

            var sku = resource.GetProperty("sku");
            Assert.Equal("Premium_UnlimitedData", sku.GetProperty("name").GetString()); // not "Standard50Mbps"
            Assert.Equal("UnlimitedData", sku.GetProperty("family").GetString());
            var provider = resource.GetProperty("properties").GetProperty("serviceProviderProperties");
            Assert.Equal(200, provider.GetProperty("bandwidthInMbps").GetInt32());
            Assert.Equal("Equinix", provider.GetProperty("serviceProviderName").GetString());
            Assert.False(resource.GetProperty("properties").TryGetProperty("serviceProvider", out _));
        }

        [Fact]
        public void ExpressRouteKeepsDefaultsForValuesItDoesNotKnow()
        {
            // A design saved with the old made-up SKU name still loads into something deployable.
            var circuit = new ExpressRouteCircuitModel { SkuTier = "Standard50Mbps", SkuFamily = "", BandwidthInMbps = 0 };

            Assert.Equal("Standard_MeteredData", circuit.SkuName);
            Assert.Equal(50, circuit.BandwidthInMbps);
        }

        private static (AppGatewayModel Gateway, SubnetModel Subnet) PlacedGateway()
        {
            var subnet = TestPlacement.NewSubnet(subnetName: "snet-appgw");
            var gateway = new AppGatewayModel { Name = "agw" };
            subnet.AddChild(gateway);
            Link(new PublicIpModel { Name = "pip-agw" }, gateway);
            return (gateway, subnet);
        }

        [Fact]
        public void ApplicationGatewayExportsAWorkingListenerToBackendChain()
        {
            var (gateway, _) = PlacedGateway();
            gateway.BackendAddress = "app1.contoso.com, app2.contoso.com";

            var properties = OfType(Export(gateway), "Microsoft.Network/applicationGateways").GetProperty("properties");

            foreach (var collection in new[] { "gatewayIPConfigurations", "frontendIPConfigurations", "frontendPorts",
                         "backendAddressPools", "backendHttpSettingsCollection", "httpListeners", "requestRoutingRules" })
                Assert.Single(properties.GetProperty(collection).EnumerateArray());

            Assert.Equal(2, properties.GetProperty("backendAddressPools")[0].GetProperty("properties").GetProperty("backendAddresses").GetArrayLength());
            var rule = properties.GetProperty("requestRoutingRules")[0].GetProperty("properties");
            Assert.Equal(100, rule.GetProperty("priority").GetInt32()); // required on v2
            Assert.EndsWith("'agw', 'http-listener')]", rule.GetProperty("httpListener").GetProperty("id").GetString());
        }

        [Fact]
        public void ApplicationGatewayWritesACapacityOrAnAutoscaleRangeNeverBoth()
        {
            // Both together is ApplicationGatewayV2SkuMustSpecifyEitherCapacityOrAutoscaleConfiguration.
            var (gateway, _) = PlacedGateway();
            var fixedSize = OfType(Export(gateway), "Microsoft.Network/applicationGateways").GetProperty("properties");
            Assert.Equal(2, fixedSize.GetProperty("sku").GetProperty("capacity").GetInt32());
            Assert.False(fixedSize.TryGetProperty("autoscaleConfiguration", out _));

            gateway.Autoscaling = true;
            gateway.MinCapacity = 0;
            var scaled = OfType(Export(gateway), "Microsoft.Network/applicationGateways").GetProperty("properties");
            Assert.False(scaled.GetProperty("sku").TryGetProperty("capacity", out _));
            Assert.Equal(0, scaled.GetProperty("autoscaleConfiguration").GetProperty("minCapacity").GetInt32()); // 0 is written
        }

        [Fact]
        public void WafTierCarriesAFirewallConfiguration()
        {
            var (gateway, _) = PlacedGateway();
            gateway.Tier = "WAF_v2";

            var waf = OfType(Export(gateway), "Microsoft.Network/applicationGateways")
                .GetProperty("properties").GetProperty("webApplicationFirewallConfiguration");

            Assert.True(waf.GetProperty("enabled").GetBoolean());
            Assert.Equal("OWASP", waf.GetProperty("ruleSetType").GetString());
        }

        [Fact]
        public void SubnetsOfOneNetworkAreCreatedInTurn()
        {
            // Concurrent subnet writes on one network fail with AnotherOperationInProgress.
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            var a = vnet.AddSubnet(new SubnetModel { Name = "a", AddressPrefix = "10.0.1.0/24" });
            var b = vnet.AddSubnet(new SubnetModel { Name = "b", AddressPrefix = "10.0.2.0/24" });
            var c = vnet.AddSubnet(new SubnetModel { Name = "c", AddressPrefix = "10.0.3.0/24" });

            var subnets = Export(vnet).Where(r => r.GetProperty("type").GetString()!.EndsWith("/subnets")).ToArray();

            string[] DependsOn(int i) => subnets[i].GetProperty("dependsOn").EnumerateArray().Select(d => d.GetString()!).ToArray();
            Assert.DoesNotContain(DependsOn(0), d => d.Contains("'a'") || d.Contains("'b'"));
            Assert.Contains(a.ResourceId, DependsOn(1));
            Assert.Contains(b.ResourceId, DependsOn(2));
            Assert.DoesNotContain(c.ResourceId, DependsOn(0));
        }

        // --- AKS ---

        [Fact]
        public void AksClusterHasAnIdentityAndItsPoolsAsTheApiArray()
        {
            var cluster = new AksClusterModel { Name = "aks1" };

            var resource = OfType(Export(cluster), "Microsoft.ContainerService/managedClusters");

            Assert.Equal("SystemAssigned", resource.GetProperty("identity").GetProperty("type").GetString());
            var properties = resource.GetProperty("properties");
            Assert.False(properties.TryGetProperty("defaultAgentPoolProfile", out _));
            var pool = Assert.Single(properties.GetProperty("agentPoolProfiles").EnumerateArray());
            Assert.Equal("System", pool.GetProperty("mode").GetString());
            Assert.False(properties.TryGetProperty("networkProfile", out _)); // not in a subnet: AKS picks its own network
        }

        [Fact]
        public void AksClusterInASubnetPutsItsNodesThere()
        {
            var subnet = TestPlacement.NewSubnet();
            var cluster = new AksClusterModel { Name = "aks1" };
            Assert.True(cluster.IsDrappable(subnet).result);
            subnet.AddChild(cluster);

            var properties = OfType(Export(cluster), "Microsoft.ContainerService/managedClusters").GetProperty("properties");

            Assert.Contains("ads-snet", properties.GetProperty("agentPoolProfiles")[0].GetProperty("vnetSubnetID").GetString());
            var network = properties.GetProperty("networkProfile");
            Assert.Equal("azure", network.GetProperty("networkPlugin").GetString());
            Assert.Equal("172.16.0.0/16", network.GetProperty("serviceCidr").GetString()); // outside the 10.0.0.0/16 network
        }

        // --- adoption ---

        [Fact]
        public void AdoptionKnowsTheTypeOfEveryModelEvenThoseThatCannotExportAlone()
        {
            // The table used to be read back from a test export, so a model that needs a subnet
            // or a link to export silently fell out of it.
            foreach (var (type, key) in new[]
            {
                ("Microsoft.Compute/virtualMachines", AdsConstants.VirtualMachine),
                ("Microsoft.Network/privateEndpoints", AdsConstants.PrivateEndpoint),
                ("Microsoft.Network/loadBalancers", AdsConstants.LoadBalancer),
                ("Microsoft.Compute/virtualMachineScaleSets", AdsConstants.VirtualMachineScaleSet),
                ("Microsoft.Network/applicationGateways", AdsConstants.AppGateway),
            })
                Assert.Equal(key, AdoptEngine.StencilKeyByArmType[type]);
        }
    }
}
