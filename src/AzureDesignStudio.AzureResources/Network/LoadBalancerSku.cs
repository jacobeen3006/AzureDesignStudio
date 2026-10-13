// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.AzureResources.Network
{
    /// <summary>
    /// LoadBalancerSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class LoadBalancerSku
    {
        /// <summary>
        /// The SKU name of the the load balancer.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// The tier of the load balancer.
        /// </summary>
        [JsonPropertyName("tier")]
        public string Tier { get; set; }
    }
}
