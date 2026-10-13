// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.AzureResources.Network
{
    /// <summary>
    /// LoadBalancerPropertiesFormat
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class LoadBalancerPropertiesFormat
    {
        /// <summary>
        /// Reference to the list of frontend IP configurations.
        /// </summary>
        [JsonPropertyName("frontendIPConfigurations")]
        public IList<FrontendIPConfiguration> FrontendIPConfigurations { get; set; }

        /// <summary>
        /// Reference to the list of backend address pools.
        /// </summary>
        [JsonPropertyName("backendAddressPools")]
        public IList<BackendAddressPool> BackendAddressPools { get; set; }

        /// <summary>
        /// The provisioning state of the load balancer resource.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
