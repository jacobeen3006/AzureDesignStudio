// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.ContainerInstance
{
    /// <summary>
    /// Microsoft.ContainerInstance/containerGroups
    /// </summary>
    [AzureResource("Microsoft.ContainerInstance/containerGroups", "2024-05-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerGroups : ResourceBase
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
        public override string Type => "Microsoft.ContainerInstance/containerGroups";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-05-01";

        /// <summary>
        /// The properties of a container group.
        /// </summary>
        [JsonPropertyName("properties")]
        public ContainerGroupsProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// ContainerGroupsProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerGroupsProperties
    {
        /// <summary>
        /// The OS type of the containers in the container group.
        /// </summary>
        [JsonPropertyName("osType")]
        public string OsType { get; set; }

        /// <summary>
        /// The number of CPUs to allocate to the container group.
        /// </summary>
        [JsonPropertyName("cpu")]
        public double Cpu { get; set; }

        /// <summary>
        /// The amount of memory in GB to allocate to the container group.
        /// </summary>
        [JsonPropertyName("memoryInGb")]
        public double MemoryInGb { get; set; }

        /// <summary>
        /// An array of containers assigned to the container group.
        /// </summary>
        [JsonPropertyName("containers")]
        public IList<object> Containers { get; set; }

        /// <summary>
        /// The current state of the container group.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
