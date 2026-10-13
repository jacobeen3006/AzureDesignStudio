// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Network
{
    /// <summary>
    /// Microsoft.Network/privateEndpoints
    /// </summary>
    [AzureResource("Microsoft.Network/privateEndpoints", "2023-09-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class PrivateEndpoints : ResourceBase
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
        public override string Type => "Microsoft.Network/privateEndpoints";

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
        /// Properties of the private endpoint.
        /// </summary>
        [JsonPropertyName("properties")]
        public PrivateEndpointProperties Properties { get; set; }

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
