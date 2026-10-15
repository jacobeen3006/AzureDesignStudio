using AzureDesignStudio.Core.App;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Offline ARM contract tests for catalog resource types beyond the original set:
    /// stencil key -> model -> ARM resource type@apiVersion.
    /// </summary>
    public class NewResourceTypesArmTests
    {
        private static JsonDocument Generate(ArmTemplate template) =>
            JsonDocument.Parse(template.GenerateArmTemplate());

        public static IEnumerable<object[]> NewTypes =>
        [
            [AdsConstants.LoadBalancer, "Microsoft.Network/loadBalancers", "2023-09-01", "ads-lb"],
            [AdsConstants.PrivateEndpoint, "Microsoft.Network/privateEndpoints", "2023-09-01", "ads-pe"],
            [AdsConstants.DnsZone, "Microsoft.Network/dnsZones", "2023-07-01", "ads-test.com"],
            [AdsConstants.AvailabilitySet, "Microsoft.Compute/availabilitySets", "2024-03-01", "ads-as"],
            [AdsConstants.ServiceBus, "Microsoft.ServiceBus/namespaces", "2023-09-01", "ads-sb"],
            [AdsConstants.EventHubs, "Microsoft.EventHub/namespaces", "2021-11-01", "ads-eh"],
            [AdsConstants.EventGrid, "Microsoft.EventGrid/topics", "2024-06-01-preview", "ads-eg"],
            [AdsConstants.ContainerAppsEnvironment, "Microsoft.App/managedEnvironments", "2024-02-02", "ads-env"],
            [AdsConstants.ContainerApp, "Microsoft.App/containerApps", "2024-02-02", "ads-ca"],
            [AdsConstants.ContainerAppsJob, "Microsoft.App/jobs", "2024-02-02", "ads-job"],
            [AdsConstants.ContainerRegistry, "Microsoft.ContainerRegistry/registries", "2023-07-01", "adsacr"],
            [AdsConstants.KeyVault, "Microsoft.KeyVault/vaults", "2023-07-01", "ads-kv"],
            [AdsConstants.AzureRedis, "Microsoft.Cache/redis", "2023-08-01", "ads-redis"],
            [AdsConstants.AzureOpenAI, "Microsoft.CognitiveServices/accounts", "2024-10-01", "ads-openai"],
            [AdsConstants.FabricCapacity, "Microsoft.Fabric/capacities", "2023-11-11", "ads-fabric"],
            [AdsConstants.NatGateway, "Microsoft.Network/natGateways", "2024-01-01", "ads-nat"],
            [AdsConstants.TrafficManagerProfile, "Microsoft.Network/trafficmanagerprofiles", "2018-08-01", "ads-tm"],
            [AdsConstants.ExpressRouteCircuit, "Microsoft.Network/expressRouteCircuits", "2024-01-01", "ads-er"],
            [AdsConstants.NetworkSecurityGroup, "Microsoft.Network/networkSecurityGroups", "2024-01-01", "ads-nsg"],
            [AdsConstants.AzureContainerInstance, "Microsoft.ContainerInstance/containerGroups", "2024-05-01", "ads-aci"],
            [AdsConstants.VirtualMachineScaleSet, "Microsoft.Compute/virtualMachineScaleSets", "2024-03-01", "ads-vmss"],
            [AdsConstants.RecoveryServicesVault, "Microsoft.RecoveryServices/vaults", "2024-04-01", "ads-rsv"],
            [AdsConstants.LogAnalyticsWorkspace, "Microsoft.OperationalInsights/workspaces", "2023-09-01", "ads-law"],
            [AdsConstants.LogicApps, "Microsoft.Logic/workflows", "2019-05-01", "ads-logic"],
            [AdsConstants.StaticWebApp, "Microsoft.Web/staticSites", "2023-12-01", "ads-swa"],
            [AdsConstants.ApplicationInsights, "Microsoft.Insights/components", "2020-02-02", "ads-appi"],
            [AdsConstants.AppConfiguration, "Microsoft.AppConfiguration/configurationStores", "2024-05-01", "ads-appconf"],
            [AdsConstants.CosmosDBAccount, "Microsoft.DocumentDB/databaseAccounts", "2023-04-15", "ads-cosmos"],
            [AdsConstants.AiSearch, "Microsoft.Search/searchServices", "2023-11-01", "ads-search"],
            [AdsConstants.DataFactory, "Microsoft.DataFactory/factories", "2018-06-01", "ads-adf"],
            [AdsConstants.SignalR, "Microsoft.SignalRService/signalR", "2024-03-01", "ads-signalr"],
            [AdsConstants.AKSCluster, "Microsoft.ContainerService/managedClusters", "2023-11-01", "ads-aks"],
        ];

        [Theory]
        [MemberData(nameof(NewTypes))]
        public void StencilKeyProducesArmResourceWithContractedApiVersion(string key, string armType, string apiVersion, string name)
        {
            var model = DataModelFactory.CreateNodeModelFromKey(key, name, "assets/azure/test.svg");
            Assert.NotNull(model);

            if (model is ContainerAppModel ca)
                new ContainerAppsEnvironmentModel { Name = "ads-env" }.AddChild(ca);
            if (model is ContainerAppsJobModel job)
                new ContainerAppsEnvironmentModel { Name = "ads-env" }.AddChild(job);

            var template = new ArmTemplate();
            template.AddResource(((IAzureResource)model).GetArmResources());

            using var doc = Generate(template);
            var resource = doc.RootElement.GetProperty("resources").EnumerateArray()
                .SingleOrDefault(r => r.GetProperty("type").GetString() == armType);

            Assert.NotEqual(JsonValueKind.Undefined, resource.ValueKind);
            Assert.Equal(apiVersion, resource.GetProperty("apiVersion").GetString());
        }

        [Fact]
        public void ContainerAppLinksToEnvironment()
        {
            var env = new ContainerAppsEnvironmentModel { Name = "ads-env" };
            var ca = new ContainerAppModel { Name = "ads-ca" };
            env.AddChild(ca);

            var template = new ArmTemplate();
            template.AddResource(env.GetArmResources());
            template.AddResource(ca.GetArmResources());

            using var doc = Generate(template);
            var resources = doc.RootElement.GetProperty("resources").EnumerateArray().ToList();
            var caResource = resources.Single(r => r.GetProperty("type").GetString() == "Microsoft.App/containerApps");

            Assert.Contains("ads-env", caResource.GetProperty("properties").GetProperty("managedEnvironmentId").GetString());
        }

        [Fact]
        public void SubnetDelegationEmitsArmDelegation()
        {
            var vnet = new VirtualNetworkModel { Name = "ads-vnet" };
            var subnet = new SubnetModel { Name = "ads-subnet", Delegation = "Microsoft.App/environments" };
            vnet.AddSubnet(subnet);

            var template = new ArmTemplate();
            template.AddResource(vnet.GetArmResources());

            using var doc = Generate(template);
            var subnetResource = doc.RootElement.GetProperty("resources").EnumerateArray()
                .Single(r => r.GetProperty("type").GetString() == "Microsoft.Network/virtualNetworks/subnets");

            Assert.Equal("Microsoft.App/environments", subnetResource
                .GetProperty("properties").GetProperty("delegations")[0]
                .GetProperty("properties").GetProperty("serviceName").GetString());
        }
    }
}
