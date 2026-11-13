// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.ContainerService
{
    /// <summary>
    /// Microsoft.ContainerService/managedClusters
    /// </summary>
    [AzureResource("Microsoft.ContainerService/managedClusters", "2023-11-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ManagedClusters : ResourceBase
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
        public override string Type => "Microsoft.ContainerService/managedClusters";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2023-11-01";

        /// <summary>
        /// The properties of the managed cluster.
        /// </summary>
        [JsonPropertyName("identity")]
        public ManagedClustersIdentity Identity { get; set; }

        [JsonPropertyName("properties")]
        public ManagedClustersClusterProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    public partial class ManagedClustersIdentity
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }
    }

    public partial class ManagedClustersNetworkProfile
    {
        [JsonPropertyName("networkPlugin")]
        public string NetworkPlugin { get; set; }

        [JsonPropertyName("serviceCidr")]
        public string ServiceCidr { get; set; }

        [JsonPropertyName("dnsServiceIP")]
        public string DnsServiceIP { get; set; }
    }
}
