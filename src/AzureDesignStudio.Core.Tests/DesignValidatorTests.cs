using AzureDesignStudio.Core.Cache;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Fabric;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.Validation;
using AzureDesignStudio.Core.Compute;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Offline regression tests for DesignValidator rule categories:
    /// naming, placement, required children, SKU/region, delegation, node config.
    /// </summary>
    public class DesignValidatorTests
    {
        private static NodeModel Create(string key, string name) =>
            DataModelFactory.CreateNodeModelFromKey(key, name, "assets/azure/test.svg");

        private static ValidationIssue[] Validate(params NodeModel[] nodes) =>
            DesignValidator.Validate(nodes).ToArray();

        private static ValidationIssue[] Rules(IEnumerable<ValidationIssue> issues, string ruleId) =>
            issues.Where(i => i.RuleId == ruleId).ToArray();
        [Fact]
        public void InvalidStorageAccountName_ProducesNamingError()
        {
            var issues = Validate(Create(AdsConstants.StorageAccount, "Bad_Name!"));
            var naming = Rules(issues, "naming");
            Assert.Single(naming);
            Assert.Equal(ValidationSeverity.Error, naming[0].Severity);
            Assert.Contains("3-24 lowercase", naming[0].Message);
        }

        [Fact]
        public void ValidNames_ProduceNoNamingErrors()
        {
            var issues = Validate(
                Create(AdsConstants.StorageAccount, "adsstorage01"),
                Create(AdsConstants.SqlServer, "ads-sql-srv"),
                Create(AdsConstants.WebApp, "ads-web-01"));
            Assert.Empty(Rules(issues, "naming"));
        }

        [Fact]
        public void SqlDatabaseOutsideServer_ProducesPlacementError()
        {
            var db = Create(AdsConstants.SqlDatabase, "adsdb");
            Assert.Single(Rules(Validate(db), "placement"));

            var server = (GroupModel)Create(AdsConstants.SqlServer, "ads-sql-srv");
            server.AddChild(db);
            Assert.Empty(Rules(Validate(server, db), "placement"));
        }

        [Fact]
        public void ContainerAppOutsideEnvironment_ProducesPlacementError()
        {
            var ca = Create(AdsConstants.ContainerApp, "ads-ca");
            Assert.Single(Rules(Validate(ca), "placement"));

            var env = (GroupModel)Create(AdsConstants.ContainerAppsEnvironment, "ads-env");
            env.AddChild(ca);
            Assert.Empty(Rules(Validate(env, ca), "placement"));
        }

        [Fact]
        public void SubnetOutsideVnet_ProducesPlacementError()
        {
            var subnet = Create(AdsConstants.Subnet, "ads-subnet");
            Assert.Single(Rules(Validate(subnet), "placement"));

            var vnet = (GroupModel)Create(AdsConstants.VirtualNetwork, "ads-vnet");
            vnet.AddChild(subnet);
            Assert.Empty(Rules(Validate(vnet, subnet), "placement"));
        }

        [Fact]
        public void AvailabilitySetWithNonVmChild_ProducesPlacementError()
        {
            var set = (GroupModel)Create(AdsConstants.AvailabilitySet, "ads-as");
            var sa = Create(AdsConstants.StorageAccount, "adsstorage01");
            set.AddChild(sa);

            var issues = Validate(set, sa);
            var placement = Rules(issues, "placement");
            Assert.Single(placement);
            Assert.Contains("only contain virtual machines", placement[0].Message);

            var vm = Create(AdsConstants.VirtualMachine, "ads-vm");
            set.RemoveChild(sa);
            set.AddChild(vm);
            Assert.Empty(Rules(Validate(set, vm), "placement"));
        }

        [Fact]
        public void EmptyContainers_ProduceRequiredChildrenWarnings()
        {
            var plan = Create(AdsConstants.AppServicePlan, "ads-plan");
            var set = Create(AdsConstants.AvailabilitySet, "ads-as");
            var env = Create(AdsConstants.ContainerAppsEnvironment, "ads-env");

            var issues = Validate(plan, set, env);
            Assert.Equal(3, Rules(issues, "required-children").Length);
            Assert.All(Rules(issues, "required-children"), i => Assert.Equal(ValidationSeverity.Warning, i.Severity));
        }

        [Fact]
        public void PopulatedContainers_ProduceNoRequiredChildrenWarnings()
        {
            var plan = (GroupModel)Create(AdsConstants.AppServicePlan, "ads-plan");
            var app = Create(AdsConstants.WebApp, "ads-web-01");
            plan.AddChild(app);

            var set = (GroupModel)Create(AdsConstants.AvailabilitySet, "ads-as");
            var vm = Create(AdsConstants.VirtualMachine, "ads-vm");
            set.AddChild(vm);

            var env = (GroupModel)Create(AdsConstants.ContainerAppsEnvironment, "ads-env");
            var ca = Create(AdsConstants.ContainerApp, "ads-ca");
            env.AddChild(ca);

            var issues = Validate(plan, app, set, vm, env, ca);
            Assert.Empty(Rules(issues, "required-children"));
        }

        [Fact]
        public void FabricInUnsupportedRegion_ProducesSkuRegionWarning()
        {
            var fabric = (FabricCapacityModel)Create(AdsConstants.FabricCapacity, "ads-fabric");
            fabric.UseResourceGroupLocation = false;
            fabric.Location = "japanwest";
            fabric.Sku = "F64";

            var warning = Assert.Single(Rules(Validate(fabric), "sku-region"));
            Assert.Equal(ValidationSeverity.Warning, warning.Severity);

            fabric.Location = "westeurope";
            Assert.Empty(Rules(Validate(fabric), "sku-region"));
        }

        [Fact]
        public void RedisPremiumInUnsupportedRegion_ProducesWarning_BasicFamilyDoesNot()
        {
            var redis = (AzureRedisModel)Create(AdsConstants.AzureRedis, "adsredis");
            redis.UseResourceGroupLocation = false;
            redis.Location = "norwaywest";
            redis.Family = "P";
            Assert.Single(Rules(Validate(redis), "sku-region"));

            redis.Family = "C";
            Assert.Empty(Rules(Validate(redis), "sku-region"));
        }

        [Fact]
        public void ParameterizedLocation_SkipsSkuRegionCheck()
        {
            var fabric = (FabricCapacityModel)Create(AdsConstants.FabricCapacity, "ads-fabric");
            fabric.Sku = "F64"; // UseResourceGroupLocation=true -> location is a parameter token
            Assert.Empty(Rules(Validate(fabric), "sku-region"));
        }

        [Fact]
        public void EnvironmentWithoutDelegatedSubnet_ProducesDelegationWarning()
        {
            var env = Create(AdsConstants.ContainerAppsEnvironment, "ads-env");
            Assert.Single(Rules(Validate(env), "delegation"));

            var subnet = (SubnetModel)Create(AdsConstants.Subnet, "ads-subnet");
            subnet.Delegation = "Microsoft.App/environments";
            Assert.Empty(Rules(Validate(env, subnet), "delegation"));
        }

        [Fact]
        public void InvalidNodeConfig_ProducesNodeConfigError()
        {
            // Container registry names reject hyphens.
            var issues = Validate(Create(AdsConstants.ContainerRegistry, "bad-name"));
            var config = Rules(issues, "node-config");
            Assert.Single(config);
            Assert.Equal(ValidationSeverity.Error, config[0].Severity);

            Assert.Empty(Rules(Validate(Create(AdsConstants.ContainerRegistry, "adsacr01")), "node-config"));
        }

        private static void LinkPorts(BlazorDiagram diagram, NodeModel a, NodeModel b) =>
            diagram.Links.Add(new LinkModel(new SinglePortAnchor(a.Ports[1]), new SinglePortAnchor(b.Ports[0])));

        private static (VirtualNetworkModel Vnet, SubnetModel Subnet, VirtualMachineModel Vm) MachineSubnet(string vnetName = "vnet1")
        {
            var vnet = new VirtualNetworkModel { Name = vnetName };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "snet1" });
            var vm = new VirtualMachineModel { Name = "vm-" + vnetName };
            subnet.AddChild(vm);
            return (vnet, subnet, vm);
        }

        private static ValidationIssue[] Outbound(params NodeModel[] nodes) =>
            Rules(Validate(nodes), "outbound-access");

        [Fact]
        public void MachineSubnet_WithNatGateway_ProducesNoOutboundIssue()
        {
            var (vnet, subnet, vm) = MachineSubnet();
            LinkPorts(new BlazorDiagram(), new NatGatewayModel { Name = "nat1" }, vm);
            Assert.Empty(Outbound(vnet, subnet, vm));
        }

        [Fact]
        public void MachineSubnet_NotPrivate_WithoutPath_WarnsAboutDefaultOutbound()
        {
            var (vnet, subnet, vm) = MachineSubnet();
            var issue = Assert.Single(Outbound(vnet, subnet, vm));
            Assert.Equal(ValidationSeverity.Warning, issue.Severity);
            Assert.Contains("'snet1' relies on default outbound access", issue.Message);
        }

        [Fact]
        public void MachineSubnet_Private_WithoutPath_WarnsNoOutboundPath()
        {
            var (vnet, subnet, vm) = MachineSubnet();
            subnet.PrivateSubnet = true;
            var issue = Assert.Single(Outbound(vnet, subnet, vm));
            Assert.Equal(ValidationSeverity.Warning, issue.Severity);
            Assert.Contains("'snet1' is private and has no outbound path", issue.Message);
        }

        [Fact]
        public void MachineSubnet_InAvailabilitySet_IsChecked()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet1" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "snet1" });
            var set = new AvailabilitySetModel { Name = "as1" };
            subnet.AddChild(set);
            var vm = new VirtualMachineModel { Name = "vm1" };
            set.AddChild(vm);
            var issue = Assert.Single(Outbound(vnet, subnet, set, vm));
            Assert.Equal(ValidationSeverity.Warning, issue.Severity);
            Assert.Contains("snet1", issue.Message);
        }

        [Fact]
        public void MachineSubnet_WithFirewallInSameNetwork_IsInfo()
        {
            var (vnet, subnet, vm) = MachineSubnet();
            var fwSubnet = vnet.AddSubnet(new SubnetModel { Name = "AzureFirewallSubnet" });
            var fw = new AzureFirewallModel { Name = "fw1" };
            fwSubnet.AddChild(fw);
            var issue = Assert.Single(Outbound(vnet, subnet, vm, fwSubnet, fw));
            Assert.Equal(ValidationSeverity.Info, issue.Severity);
            Assert.Contains("Subnet 'snet1' has no NAT gateway", issue.Message);
        }

        [Fact]
        public void ScaleSetSubnet_WithoutPath_WarnsAboutDefaultOutbound()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet1" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "snet1" });
            var set = new VirtualMachineScaleSetModel { Name = "vmss1" };
            subnet.AddChild(set);
            var issue = Assert.Single(Outbound(vnet, subnet, set));
            Assert.Equal(ValidationSeverity.Warning, issue.Severity);
            Assert.Contains("'snet1' relies on default outbound access", issue.Message);
        }

        [Fact]
        public void MachineSubnet_Private_WithFirewallInSameNetwork_IsInfoNotWarning()
        {
            var (vnet, subnet, vm) = MachineSubnet();
            subnet.PrivateSubnet = true;
            var fwSubnet = vnet.AddSubnet(new SubnetModel { Name = "AzureFirewallSubnet" });
            var fw = new AzureFirewallModel { Name = "fw1" };
            fwSubnet.AddChild(fw);
            var issue = Assert.Single(Outbound(vnet, subnet, vm, fwSubnet, fw));
            Assert.Equal(ValidationSeverity.Info, issue.Severity);
        }

        [Fact]
        public void MachineSubnet_WithFirewallInPeeredNetwork_IsInfo()
        {
            var (vnet, subnet, vm) = MachineSubnet();
            var hub = new VirtualNetworkModel { Name = "hub" };
            var fwSubnet = hub.AddSubnet(new SubnetModel { Name = "AzureFirewallSubnet" });
            var fw = new AzureFirewallModel { Name = "fw1" };
            fwSubnet.AddChild(fw);
            LinkPorts(new BlazorDiagram(), vnet, hub);
            var issue = Assert.Single(Outbound(vnet, subnet, vm, hub, fwSubnet, fw));
            Assert.Equal(ValidationSeverity.Info, issue.Severity);
        }

        [Fact]
        public void Subnet_WithoutMachines_ProducesNoOutboundIssue()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet1" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "snet1" });
            Assert.Empty(Outbound(vnet, subnet));
        }

        [Fact]
        public void DelegatedSubnet_ProducesNoOutboundIssue()
        {
            var (vnet, subnet, vm) = MachineSubnet();
            subnet.Delegation = "Microsoft.App/environments";
            Assert.Empty(Outbound(vnet, subnet, vm));
        }

        private static ValidationIssue[] Retired(params NodeModel[] nodes) =>
            Rules(Validate(nodes), "retired-sku");

        [Fact]
        public void BasicPublicIp_ProducesRetiredSkuError()
        {
            var issue = Assert.Single(Retired(new PublicIpModel { Name = "pip1", Sku = "Basic" }));
            Assert.Equal(ValidationSeverity.Error, issue.Severity);
            Assert.Contains("2025-09-30", issue.Message);
            Assert.Contains("Standard", issue.Message);
        }

        [Fact]
        public void BasicLoadBalancer_ProducesRetiredSkuError()
        {
            var issue = Assert.Single(Retired(new LoadBalancerModel { Name = "lb1", Sku = "basic" }));
            Assert.Equal(ValidationSeverity.Error, issue.Severity);
            Assert.Contains("2025-09-30", issue.Message);
            Assert.Contains("Standard", issue.Message);
        }

        [Theory]
        [InlineData("Standard", "Standard_v2")]
        [InlineData("WAF", "WAF_v2")]
        public void AppGatewayV1Tier_ProducesRetiredSkuError(string tier, string replacement)
        {
            var issue = Assert.Single(Retired(new AppGatewayModel { Name = "agw1", Tier = tier }));
            Assert.Equal(ValidationSeverity.Error, issue.Severity);
            Assert.Contains("2026-04-28", issue.Message);
            Assert.Contains(replacement, issue.Message);
        }

        [Fact]
        public void StandardSkus_ProduceNoRetiredSkuIssue()
        {
            Assert.Empty(Retired(
                new PublicIpModel { Name = "pip1", Sku = "Standard" },
                new LoadBalancerModel { Name = "lb1", Sku = "Standard" },
                new AppGatewayModel { Name = "agw1" },
                new AppGatewayModel { Name = "agw2", Tier = "WAF_v2" }));
        }
    }
}
