// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.AzureResources.ContainerService
{
    /// <summary>
    /// ManagedClustersClusterProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ManagedClustersClusterProperties
    {
        /// <summary>
        /// DNS prefix specified when creating the managed cluster.
        /// </summary>
        [JsonPropertyName("dnsPrefix")]
        public string DnsPrefix { get; set; }

        /// <summary>
        /// When populated, the desired Kubernetes version; omitted, ARM deploys its current default.
        /// </summary>
        [JsonPropertyName("kubernetesVersion")]
        public string KubernetesVersion { get; set; }

        /// <summary>
        /// The current version of Kubernetes running on the cluster.
        /// </summary>
        [JsonPropertyName("currentKubernetesVersion")]
        public string CurrentKubernetesVersion { get; private set; }

        /// <summary>
        /// FQDN of the managed cluster API server.
        /// </summary>
        [JsonPropertyName("fqdn")]
        public string Fqdn { get; private set; }

        /// <summary>
        /// Default agent pool properties.
        /// </summary>
        [JsonPropertyName("defaultAgentPoolProfile")]
        public ManagedClustersAgentPoolProfile DefaultAgentPoolProfile { get; set; }

        /// <summary>
        /// The current provisioning state.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; private set; }
    }
}
