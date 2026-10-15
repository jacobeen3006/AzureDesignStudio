// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.AzureResources.ContainerService
{
    /// <summary>
    /// ManagedClustersAgentPoolProfile
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ManagedClustersAgentPoolProfile
    {
        /// <summary>
        /// Unique name identifying the agent pool within the cluster.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Number of agents (VMs) to host docker containers.
        /// </summary>
        [JsonPropertyName("count")]
        public int Count { get; set; }

        /// <summary>
        /// Size of agent VMs.
        /// </summary>
        [JsonPropertyName("vmSize")]
        public string VmSize { get; set; }

        /// <summary>
        /// OS Disk Size in GB to be used to specify the disk size for every machine in the cluster.
        /// </summary>
        [JsonPropertyName("osDiskSizeGB")]
        public int OsDiskSizeGB { get; set; }

        /// <summary>
        /// The OS type of the agent pool.
        /// </summary>
        [JsonPropertyName("osType")]
        public string OsType { get; set; }

        /// <summary>
        /// The mode of an agent pool: System or User.
        /// </summary>
        [JsonPropertyName("mode")]
        public string Mode { get; set; }

        /// <summary>
        /// Whether to enable auto-scaling for the agent pool.
        /// </summary>
        [JsonPropertyName("enableAutoScaling")]
        public bool EnableAutoScaling { get; set; }

        /// <summary>
        /// Minimum number of nodes for auto-scaling.
        /// </summary>
        [JsonPropertyName("minCount")]
        public int MinCount { get; set; }

        /// <summary>
        /// Maximum number of nodes for auto-scaling.
        /// </summary>
        [JsonPropertyName("maxCount")]
        public int MaxCount { get; set; }
    }
}
