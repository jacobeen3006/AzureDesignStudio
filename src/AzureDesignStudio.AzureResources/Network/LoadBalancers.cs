// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Network
{
    /// <summary>
    /// Microsoft.Network/loadBalancers
    /// </summary>
    [AzureResource("Microsoft.Network/loadBalancers", "2023-09-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class LoadBalancers : ResourceBase
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
        public override string Type => "Microsoft.Network/loadBalancers";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2023-09-01";

        /// <summary>
        /// The extended location of the load balancer.
        /// </summary>
        [JsonPropertyName("extendedLocation")]
        public ExtendedLocation ExtendedLocation { get; set; }

        /// <summary>
        /// The load balancer SKU.
        /// </summary>
        [JsonPropertyName("sku")]
        public LoadBalancerSku Sku { get; set; }

        /// <summary>
        /// Load Balancer properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public LoadBalancerPropertiesFormat Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }

        /// <summary>
        /// A list of availability zones denoting to which the resource belongs.
        /// </summary>
        [JsonPropertyName("zones")]
        public IList<string> Zones { get; set; }
    }
}
