using AutoMapper;
using AzureDesignStudio.Core.App;
using AzureDesignStudio.Core.APIM;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.SQL;
using AzureDesignStudio.Core.Web;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Golden export coverage (item 97): every catalog stencil key must resolve to a
    /// real model and export ARM resources without a NotImplementedException anywhere
    /// on the path. Keys this build cannot resolve must degrade to UnknownNodeModel -
    /// never crash the designer, the export, or the load of a newer design file.
    /// </summary>
    public class StencilExportGoldenTests
    {
        // Models whose IsValid demands more than a name; keys not listed use "ads-golden".
        private static readonly Dictionary<string, string> NameOverrides = new()
        {
            [AdsConstants.DnsZone] = "ads-test.com",
            [AdsConstants.ContainerRegistry] = "adsacr01",
        };

        public static IEnumerable<object[]> CatalogKeys =>
        [
            [AdsConstants.StorageAccount], [AdsConstants.VirtualNetwork], [AdsConstants.Subnet],
            [AdsConstants.VirtualMachine], [AdsConstants.AzureFirewall], [AdsConstants.Bastions],
            [AdsConstants.PublicIp], [AdsConstants.SqlServer], [AdsConstants.SqlDatabase],
            [AdsConstants.AppServicePlan], [AdsConstants.APIM], [AdsConstants.AppGateway],
            [AdsConstants.LoadBalancer], [AdsConstants.PrivateEndpoint], [AdsConstants.DnsZone],
            [AdsConstants.AvailabilitySet], [AdsConstants.ServiceBus], [AdsConstants.EventHubs],
            [AdsConstants.EventGrid], [AdsConstants.ContainerAppsEnvironment], [AdsConstants.ContainerApp],
            [AdsConstants.ContainerAppsJob], [AdsConstants.ContainerRegistry], [AdsConstants.KeyVault],
            [AdsConstants.AzureRedis], [AdsConstants.AzureOpenAI], [AdsConstants.FabricCapacity],
            [AdsConstants.LogicApps], [AdsConstants.StaticWebApp], [AdsConstants.ApplicationInsights],
            [AdsConstants.AppConfiguration], [AdsConstants.CosmosDBAccount], [AdsConstants.AiSearch],
            [AdsConstants.DataFactory], [AdsConstants.SignalR], [AdsConstants.AzureContainerInstance],
            [AdsConstants.VirtualMachineScaleSet], [AdsConstants.RecoveryServicesVault],
            [AdsConstants.LogAnalyticsWorkspace], [AdsConstants.NatGateway],
            [AdsConstants.TrafficManagerProfile], [AdsConstants.ExpressRouteCircuit],
            [AdsConstants.NetworkSecurityGroup],
            [AdsConstants.WebApp], [AdsConstants.FunctionApp],
        ];

        private static IAzureResource CreateExportable(string key)
        {
            var name = NameOverrides.TryGetValue(key, out var n) ? n : "ads-golden";
            var model = DataModelFactory.CreateNodeModelFromKey(key, name, "assets/azure/test.svg");
            Assert.IsNotType<UnknownNodeModel>(model);

            switch (model)
            {
                case ContainerAppModel or ContainerAppsJobModel:
                    new ContainerAppsEnvironmentModel { Name = "ads-env" }.AddChild((AzureNodeBase)model);
                    break;
                // Subnets export only as part of a vnet (ARM child-resource nesting).
                case SubnetModel subnet:
                    subnet.AddressPrefix = "10.0.0.0/24";
                    new VirtualNetworkModel { Name = "ads-vnet" }.AddSubnet(subnet);
                    break;
                case SqlDatabaseModel:
                    new SqlServerModel { Name = "ads-sql" }.AddChild((AzureNodeBase)model);
                    break;
                // Bastion/firewall are only valid inside their Azure-preset subnet,
                // which itself must live in a vnet.
                case BastionsModel or AzureFirewallModel:
                {
                    var host = new VirtualNetworkModel { Name = "ads-vnet" }
                        .AddSubnet(new SubnetModel
                        {
                            Name = model is AzureFirewallModel ? "AzureFirewallSubnet" : "AzureBastionSubnet",
                            AddressPrefix = "10.0.0.0/24",
                        });
                    host.AddChild((AzureNodeBase)model);
                    // Both require a linked public IP (diagram port wiring).
                    var pip = new PublicIpModel { Name = "ads-host-pip" };
                    var diagram = new BlazorDiagram();
                    diagram.Links.Add(new Blazor.Diagrams.Core.Models.LinkModel(
                        new SinglePortAnchor(pip.Ports[1]), new SinglePortAnchor(((AzureNodeBase)model).Ports[0])));
                    break;
                }
                // APIM demands publisher contact info before it exports.
                case APIMModel apim:
                    apim.PublisherName = "ads-publisher";
                    apim.PublisherEmail = "publisher@contoso.com";
                    break;
                // Web apps live in a service plan; the plan carries the OS.
                case WebAppModel or FunctionAppModel:
                {
                    var plan = new AppServicePlanModel { Name = "ads-plan", Kind = "linux" };
                    plan.AddChild((AzureNodeBase)model);
                    if (model is FunctionAppModel func)
                        func.HostingPlan = "consumption";
                    ((dynamic)model).RuntimeStack = "dotnet|v6.0";
                    break;
                }
                // App gateway needs a linked public IP.
                case AppGatewayModel gw:
                {
                    var pip = new PublicIpModel { Name = "ads-gw-pip" };
                    var diagram = new BlazorDiagram();
                    diagram.Links.Add(new Blazor.Diagrams.Core.Models.LinkModel(new SinglePortAnchor(pip.Ports[1]), new SinglePortAnchor(gw.Ports[0])));
                    break;
                }
            }

            return (IAzureResource)model;
        }

        [Theory]
        [MemberData(nameof(CatalogKeys))]
        public void CatalogKeyExportsArmResourcesWithoutThrowing(string key)
        {
            var resource = CreateExportable(key);

            var resources = resource.GetArmResources();
            var parameters = resource.GetArmParameters();

            var template = new ArmTemplate();
            template.AddResource(resources);
            template.AddParameters(parameters);
            using var doc = JsonDocument.Parse(template.GenerateArmTemplate());

            Assert.NotEmpty(resources);
            foreach (var r in doc.RootElement.GetProperty("resources").EnumerateArray())
            {
                Assert.False(string.IsNullOrEmpty(r.GetProperty("type").GetString()));
            }
        }

        [Theory]
        [InlineData(AdsConstants.WebApp)]
        [InlineData(AdsConstants.FunctionApp)]
        public void UnknownRuntimeStackPassesThroughInsteadOfThrowing(string key)
        {
            var model = CreateExportable(key);
            ((dynamic)model).RuntimeStack = "dotnet|v9.9";

            var template = new ArmTemplate();
            template.AddResource(model.GetArmResources());
            Assert.Contains("dotnet|v9.9", template.GenerateArmTemplate());
        }

        [Fact]
        public void VirtualNetworkPeeringExportsItsArmResource()
        {
            var peering = new VirtualNetworkPeeringModel
            {
                Name = "ads-vnet1/ads-vnet2-peering",
                RemoteVirtualNetworkId = "[resourceId('Microsoft.Network/virtualNetworks', 'ads-vnet2')]",
            };
            peering.DependsOn.Add("[resourceId('Microsoft.Network/virtualNetworks', 'ads-vnet1')]");

            var resources = peering.GetArmResources();
            Assert.Single(resources);
            Assert.Equal("Microsoft.Network/virtualNetworks/virtualNetworkPeerings", resources[0].Type);
            Assert.Empty(peering.GetArmParameters());

            var template = new ArmTemplate();
            template.AddResource(resources);
            var json = template.GenerateArmTemplate();
            Assert.Contains("virtualNetworkPeerings", json);
            Assert.Contains("ads-vnet2", json);
        }

        [Fact]
        public void UnresolvableStencilKeyDegradesToUnknownNodeModel()
        {
            var model = DataModelFactory.CreateNodeModelFromKey("quantum-computer", "ads-q", "i.svg");

            var unknown = Assert.IsType<UnknownNodeModel>(model);
            Assert.Equal("quantum-computer", unknown.StencilKey);
            Assert.False(unknown.IsValid);
            Assert.Empty(unknown.GetArmResources());
            Assert.Empty(unknown.GetArmParameters());

            var template = new ArmTemplate();
            template.AddResource(((IAzureResource)model).GetArmResources());
            using var doc = JsonDocument.Parse(template.GenerateArmTemplate());
            Assert.Empty(doc.RootElement.GetProperty("resources").EnumerateArray());
        }

        [Fact]
        public async Task DesignWithUnknownTypeKeyLoadsAsPlaceholderAndSurvivesSave()
        {
            var diagram = new BlazorDiagram();
            DataModelFactory.RegisterAzureModels(diagram);
            var mapper = new Mapper(new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()));

            var graph = JsonSerializer.Deserialize<DiagramGraph>(
                """{"Groups":[],"Nodes":[{"TypeKey":"quantum-computer","Name":"ads-q","Id":"n1","ImagePath":"i.svg"}],"Links":[]}""")!;
            DataModelFactory.LoadDiagramFromDto(diagram, graph, mapper);

            Assert.Single(diagram.Nodes.OfType<UnknownNodeModel>());

            // Save must preserve the unknown key rather than drop or crash on it.
            var saved = await DataModelFactory.SaveDiagramToDto(diagram, mapper);
            var savedJson = JsonSerializer.Serialize(saved);
            Assert.Contains("quantum-computer", savedJson);

            var reloaded = JsonSerializer.Deserialize<DiagramGraph>(savedJson)!;
            var diagram2 = new BlazorDiagram();
            DataModelFactory.RegisterAzureModels(diagram2);
            DataModelFactory.LoadDiagramFromDto(diagram2, reloaded, mapper);
            Assert.Single(diagram2.Nodes.OfType<UnknownNodeModel>());
        }

        [Fact]
        public void ResourceConverterReadsKnownArmTypeAndRejectsUnknown()
        {
            var peering = new VirtualNetworkPeeringModel { Name = "a/b-peering", RemoteVirtualNetworkId = "x" };
            var options = new JsonSerializerOptions { Converters = { new ResourceBaseJsonConverter() } };

            var json = JsonSerializer.Serialize<AzureDesignStudio.AzureResources.Base.ResourceBase>(peering.GetArmResources()[0], options);
            var round = JsonSerializer.Deserialize<AzureDesignStudio.AzureResources.Base.ResourceBase>(json, options);

            Assert.NotNull(round);
            Assert.Equal("Microsoft.Network/virtualNetworks/virtualNetworkPeerings", round.Type);
            Assert.Equal("a/b-peering", round.Name);

            var bad = JsonSerializer.SerializeToElement(new { type = "Microsoft.Nowhere/whatevs" }).GetRawText();
            var ex = Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize<AzureDesignStudio.AzureResources.Base.ResourceBase>(bad, options));
            Assert.Contains("Microsoft.Nowhere/whatevs", ex.Message);
        }
    }
}
