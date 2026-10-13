// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Network
{
    /// <summary>
    /// Microsoft.Network/natGateways
    /// </summary>
    [AzureResource("Microsoft.Network/natGateways", "2024-01-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class NatGateways : ResourceBase
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
        public override string Type => "Microsoft.Network/natGateways";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-01-01";

        /// <summary>
        /// The nat gateway SKU.
        /// </summary>
        [JsonPropertyName("sku")]
        public NatGatewaysSku Sku { get; set; }

        /// <summary>
        /// Nat Gateway properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public NatGatewaysProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// The nat gateway SKU.
    /// </summary>
    public partial class NatGatewaysSku
    {
        /// <summary>
        /// Gets SKU name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// Nat Gateway properties.
    /// </summary>
    public partial class NatGatewaysProperties
    {
        /// <summary>
        /// The number of minutes of inactivity after which the NAT gateway is removed from the idle flow tracking table.
        /// </summary>
        [JsonPropertyName("idleTimeoutInMinutes")]
        public int IdleTimeoutInMinutes { get; set; }

        /// <summary>
        /// A list of references to public IP addresses.
        /// </summary>
        [JsonPropertyName("publicIpAddresses")]
        public IList<object> PublicIpAddresses { get; set; }
    }
}
