using AzureDesignStudio.Core.Cache;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Fabric;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.Validation;
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
    }
}
