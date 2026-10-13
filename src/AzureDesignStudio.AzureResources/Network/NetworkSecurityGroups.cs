// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Network
{
    /// <summary>
    /// Microsoft.Network/networkSecurityGroups
    /// </summary>
    [AzureResource("Microsoft.Network/networkSecurityGroups", "2024-01-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class NetworkSecurityGroups : ResourceBase
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
        public override string Type => "Microsoft.Network/networkSecurityGroups";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-01-01";

        /// <summary>
        /// The network security group properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public NetworkSecurityGroupsProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// The network security group properties.
    /// </summary>
    public partial class NetworkSecurityGroupsProperties
    {
        /// <summary>
        /// A collection of security rules in the network security group.
        /// </summary>
        [JsonPropertyName("securityRules")]
        public IList<object> SecurityRules { get; set; }
    }
}
