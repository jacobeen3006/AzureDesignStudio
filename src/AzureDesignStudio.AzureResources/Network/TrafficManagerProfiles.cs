// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Network
{
    /// <summary>
    /// Microsoft.Network/trafficmanagerprofiles
    /// </summary>
    [AzureResource("Microsoft.Network/trafficmanagerprofiles", "2018-08-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class TrafficManagerProfiles : ResourceBase
    {
        /// <summary>
        /// The resource id
        /// </summary>
        [JsonIgnore]
        public string Id { get; private set; }

        /// <summary>
        /// The resource type
        /// </summary>
        [JsonPropertyName("type")]
        public override string Type => "Microsoft.Network/trafficmanagerprofiles";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2018-08-01";

        /// <summary>
        /// The profile SKU.
        /// </summary>
        [JsonPropertyName("sku")]
        public TrafficManagerProfilesSku Sku { get; set; }

        /// <summary>
        /// The profile properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public TrafficManagerProfilesProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// The profile SKU.
    /// </summary>
    public partial class TrafficManagerProfilesSku
    {
        /// <summary>
        /// Gets SKU name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// The profile properties.
    /// </summary>
    public partial class TrafficManagerProfilesProperties
    {
        /// <summary>
        /// The traffic routing method.
        /// </summary>
        [JsonPropertyName("trafficRoutingMethod")]
        public string TrafficRoutingMethod { get; set; }

        /// <summary>
        /// The DNS configuration.
        /// </summary>
        [JsonPropertyName("dnsConfig")]
        public TrafficManagerProfilesDnsConfig DnsConfig { get; set; }
    }

    /// <summary>
    /// The DNS configuration.
    /// </summary>
    public partial class TrafficManagerProfilesDnsConfig
    {
        /// <summary>
        /// The time to live (TTL) in seconds.
        /// </summary>
        [JsonPropertyName("ttl")]
        public int Ttl { get; set; }
    }
}
