using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.SQL;
using AzureDesignStudio.Core.Web;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Offline ARM generation tests: exercise model -> template JSON without any Azure call.
    /// </summary>
    public class OfflineArmTemplateTests
    {
        private static JsonDocument Generate(ArmTemplate template) =>
            JsonDocument.Parse(template.GenerateArmTemplate());

        private static List<string> ResourceTypes(JsonElement template) =>
            [.. template.GetProperty("resources").EnumerateArray()
                .Select(r => r.GetProperty("type").GetString()!)];

        [Fact]
        public void TemplateHasSchemaAndLocationParameter()
        {
            using var doc = Generate(new ArmTemplate());
            var root = doc.RootElement;

            Assert.Equal("https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
                root.GetProperty("$schema").GetString());
            Assert.Equal("1.0.0.0", root.GetProperty("contentVersion").GetString());
            Assert.Equal("[resourceGroup().location]",
                root.GetProperty("parameters").GetProperty("location").GetProperty("defaultValue").GetString());
        }

        [Fact]
        public void VnetWithSubnetsEmitsSeparateSubnetResources()
        {
            var template = new ArmTemplate();
            var vnet = new VirtualNetworkModel { Name = "ads-vnet" };
            vnet.AddSubnet(new SubnetModel { Name = "ads-subnet-a" });
            vnet.AddSubnet(new SubnetModel { Name = "ads-subnet-b" });
            template.AddResource(vnet.GetArmResources());

            using var doc = Generate(template);
            var types = ResourceTypes(doc.RootElement);

            Assert.Equal(1, types.Count(t => t == "Microsoft.Network/virtualNetworks"));
            Assert.Equal(2, types.Count(t => t == "Microsoft.Network/virtualNetworks/subnets"));

            var vnetResource = doc.RootElement.GetProperty("resources").EnumerateArray()
                .First(r => r.GetProperty("type").GetString() == "Microsoft.Network/virtualNetworks");
            Assert.Contains("10.0.0.0/16", vnetResource
                .GetProperty("properties").GetProperty("addressSpace").GetProperty("addressPrefixes")
                .EnumerateArray().Select(p => p.GetString()));
        }

        [Fact]
        public void LocationSwitchesBetweenParameterAndLiteral()
        {
            var vnet = new VirtualNetworkModel { Name = "ads-vnet" };

            var template = new ArmTemplate();
            template.AddResource(vnet.GetArmResources());
            using var docDefault = Generate(template);
            Assert.Equal("[parameters('location')]",
                docDefault.RootElement.GetProperty("resources").EnumerateArray()
                    .First(r => r.GetProperty("type").GetString() == "Microsoft.Network/virtualNetworks")
                    .GetProperty("location").GetString());

            vnet.UseResourceGroupLocation = false;
            vnet.Location = "westus";
            template.RemoveAllResources();
            template.AddResource(vnet.GetArmResources());
            using var docLiteral = Generate(template);
            Assert.Equal("westus",
                docLiteral.RootElement.GetProperty("resources").EnumerateArray()
                    .First(r => r.GetProperty("type").GetString() == "Microsoft.Network/virtualNetworks")
                    .GetProperty("location").GetString());
        }

        [Fact]
        public void GenerationIsDeterministic()
        {
            var vnet = new VirtualNetworkModel { Name = "ads-vnet" };
            vnet.AddSubnet(new SubnetModel { Name = "ads-subnet-a" });

            var first = new ArmTemplate();
            first.AddResource(vnet.GetArmResources());
            var second = new ArmTemplate();
            second.AddResource(vnet.GetArmResources());

            Assert.Equal(first.GenerateArmTemplate(), second.GenerateArmTemplate());
        }

        [Fact]
        public void SqlServerWithDatabaseEmitsNestedResourceName()
        {
            var template = new ArmTemplate();
            var server = new SqlServerModel { Name = "ads-sql" };
            template.AddResource(server.GetArmResources());
            var db = new SqlDatabaseModel { Name = "ads-db", Group = server };
            template.AddResource(db.GetArmResources());

            using var doc = Generate(template);
            var resources = doc.RootElement.GetProperty("resources").EnumerateArray().ToList();

            Assert.Contains(resources, r => r.GetProperty("type").GetString() == "Microsoft.Sql/servers");
            var dbResource = resources.Single(r => r.GetProperty("type").GetString() == "Microsoft.Sql/servers/databases");
            Assert.Equal("ads-sql/ads-db", dbResource.GetProperty("name").GetString());
        }

        [Fact]
        public void CoreResourceTypesMatchArmContract()
        {
            var template = new ArmTemplate();
            var plan = new AppServicePlanModel { Name = "ads-plan" };
            template.AddResource(plan.GetArmResources());
            template.AddResource(new PublicIpModel { Name = "ads-ip" }.GetArmResources());
            template.AddResource(new VirtualMachineModel { Name = "ads-vm" }.GetArmResources());
            template.AddResource(new WebAppModel { Name = "ads-web", RuntimeStack = "NODE|14-lts", Group = plan }.GetArmResources());

            using var doc = Generate(template);
            var types = ResourceTypes(doc.RootElement);

            Assert.Contains("Microsoft.Network/publicIPAddresses", types);
            Assert.Contains("Microsoft.Compute/virtualMachines", types);
            Assert.Contains("Microsoft.Web/serverfarms", types);
            Assert.Contains("Microsoft.Web/sites", types);
        }


        [Fact]
        public void InvalidModelThrowsBeforeGeneration()
        {
            var template = new ArmTemplate();
            Assert.Throws<System.Exception>(
                () => template.AddResource(new WebAppModel { Name = "ads-web" }.GetArmResources()));
        }
        [Fact]
        public void EveryResourceCarriesNameAndApiVersion()
        {
            var template = new ArmTemplate();
            var vnet = new VirtualNetworkModel { Name = "ads-vnet" };
            vnet.AddSubnet(new SubnetModel { Name = "ads-subnet-a" });
            template.AddResource(vnet.GetArmResources());
            var server = new SqlServerModel { Name = "ads-sql" };
            template.AddResource(server.GetArmResources());
            template.AddResource(new SqlDatabaseModel { Name = "ads-db", Group = server }.GetArmResources());

            using var doc = Generate(template);
            foreach (var resource in doc.RootElement.GetProperty("resources").EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(resource.GetProperty("name").GetString()),
                    $"Resource without name: {resource}");
                var apiVersion = resource.GetProperty("apiVersion").GetString()!;
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}(-preview)?$", apiVersion);
            }
        }
    }
}
