using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Core.Cache;
using AzureDesignStudio.Core.Messaging;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.Storage;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// The exported template for resource types whose model passed validation but wrote a shape
    /// the ARM schema does not accept. Each expectation follows the ARM template reference.
    /// </summary>
    public class ArmModelShapeTests
    {
        private static JsonElement Export(AzureNodeBase node)
        {
            var template = new ArmTemplate();
            template.AddResource(node.GetArmResources());
            using var doc = JsonDocument.Parse(template.GenerateArmTemplate());
            return doc.RootElement.GetProperty("resources")[0].Clone();
        }

        // Microsoft.Cache/redis: sku is properties.sku; name is Basic/Standard/Premium.
        [Theory]
        [InlineData("C", 1, "Standard")]
        [InlineData("C", 0, "Standard")] // C0: capacity 0 is a real size and a required property
        [InlineData("P", 2, "Premium")]
        public void RedisSkuSitsUnderPropertiesWithAValidName(string family, int capacity, string expectedName)
        {
            var resource = Export(new AzureRedisModel { Name = "cache1", Family = family, Capacity = capacity });

            Assert.False(resource.TryGetProperty("sku", out _));
            var sku = resource.GetProperty("properties").GetProperty("sku");
            Assert.Equal(expectedName, sku.GetProperty("name").GetString());
            Assert.Equal(family, sku.GetProperty("family").GetString());
            Assert.Equal(capacity, sku.GetProperty("capacity").GetInt32());
        }

        [Theory]
        [InlineData("C", 6, true)]
        [InlineData("C", 7, false)]
        [InlineData("P", 0, false)] // there is no P0
        [InlineData("P", 5, true)]
        public void RedisCapacityRangeFollowsTheFamily(string family, int capacity, bool valid)
        {
            Assert.Equal(valid, new AzureRedisModel { Name = "cache1", Family = family, Capacity = capacity }.IsValid);
        }

        // Microsoft.EventHub / Microsoft.ServiceBus namespaces: tier equals the sku name.
        [Theory]
        [InlineData("Basic")]
        [InlineData("Standard")]
        public void EventHubsTierFollowsTheSku(string sku)
        {
            var exported = Export(new EventHubsModel { Name = "events-ns", Sku = sku }).GetProperty("sku");

            Assert.Equal(sku, exported.GetProperty("name").GetString());
            Assert.Equal(sku, exported.GetProperty("tier").GetString());
        }

        [Theory]
        [InlineData("Standard")]
        [InlineData("Premium")]
        public void ServiceBusTierFollowsTheSku(string sku)
        {
            var exported = Export(new ServiceBusModel { Name = "orders-ns", Sku = sku }).GetProperty("sku");

            Assert.Equal(sku, exported.GetProperty("name").GetString());
            Assert.Equal(sku, exported.GetProperty("tier").GetString());
        }

        [Fact]
        public void DnsZoneIsDeployedToTheGlobalLocation()
        {
            Assert.Equal("global", Export(new DnsZoneModel { Name = "contoso.com" }).GetProperty("location").GetString());
        }

        [Fact]
        public void TrafficManagerProfileIsGlobalAndCarriesItsDnsNameAndMonitor()
        {
            var resource = Export(new TrafficManagerProfileModel { Name = "Shop-TM" });
            var properties = resource.GetProperty("properties");

            Assert.Equal("global", resource.GetProperty("location").GetString());
            Assert.Equal("shop-tm", properties.GetProperty("dnsConfig").GetProperty("relativeName").GetString());
            Assert.Equal(300, properties.GetProperty("dnsConfig").GetProperty("ttl").GetInt32());
            var monitor = properties.GetProperty("monitorConfig");
            Assert.Equal("HTTPS", monitor.GetProperty("protocol").GetString());
            Assert.Equal(443, monitor.GetProperty("port").GetInt32());
            Assert.Equal("/", monitor.GetProperty("path").GetString());
        }

        // sku.name and kind are required; a freshly dropped account used to export "sku": {}.
        [Fact]
        public void NewStorageAccountIsDeployableAsDropped()
        {
            var resource = Export(new StorageAccountModel { Name = "store1" });

            Assert.Equal("Standard_LRS", resource.GetProperty("sku").GetProperty("name").GetString());
            Assert.Equal("StorageV2", resource.GetProperty("kind").GetString());
        }

        [Fact]
        public void StorageAccountSavedWithoutSkuOrKindKeepsTheDefaults()
        {
            // What mapping an older saved design onto the model does.
            var account = new StorageAccountModel { Name = "store1", SkuName = null!, Kind = null! };

            Assert.Equal("Standard_LRS", account.SkuName);
            Assert.Equal("StorageV2", account.Kind);
        }

        [Theory]
        [InlineData("StorageV2", "Standard_GRS", true)]
        [InlineData("BlockBlobStorage", "Premium_LRS", true)]
        [InlineData("FileStorage", "Standard_LRS", false)]
        public void PremiumOnlyStorageKindsNeedAPremiumSku(string kind, string sku, bool valid)
        {
            Assert.Equal(valid, new StorageAccountModel { Name = "store1", Kind = kind, SkuName = sku }.IsValid);
        }
    }
}
