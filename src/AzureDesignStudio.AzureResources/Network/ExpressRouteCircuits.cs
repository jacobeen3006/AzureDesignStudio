// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Network
{
    /// <summary>
    /// Microsoft.Network/expressRouteCircuits
    /// </summary>
    [AzureResource("Microsoft.Network/expressRouteCircuits", "2024-01-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ExpressRouteCircuits : ResourceBase
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
        public override string Type => "Microsoft.Network/expressRouteCircuits";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-01-01";

        /// <summary>
        /// The ExpressRoute circuit SKU.
        /// </summary>
        [JsonPropertyName("sku")]
        public ExpressRouteCircuitsSku Sku { get; set; }

        /// <summary>
        /// The ExpressRoute circuit properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public ExpressRouteCircuitsProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// The ExpressRoute circuit SKU.
    /// </summary>
    public partial class ExpressRouteCircuitsSku
    {
        /// <summary>
        /// Gets SKU name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Gets SKU tier.
        /// </summary>
        [JsonPropertyName("tier")]
        public string Tier { get; set; }
    }

    /// <summary>
    /// The ExpressRoute circuit properties.
    /// </summary>
    public partial class ExpressRouteCircuitsProperties
    {
        /// <summary>
        /// The service provider through which the ExpressRoute circuit is provisioned.
        /// </summary>
        [JsonPropertyName("serviceProvider")]
        public string ServiceProvider { get; set; }
    }
}
