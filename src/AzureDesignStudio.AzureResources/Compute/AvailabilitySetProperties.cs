// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.AzureResources.Compute
{
    /// <summary>
    /// AvailabilitySetProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class AvailabilitySetProperties
    {
        /// <summary>
        /// The number of fault domains for the availability set.
        /// </summary>
        [JsonPropertyName("platformFaultDomainCount")]
        public int PlatformFaultDomainCount { get; set; }

        /// <summary>
        /// The number of update domains for the availability set.
        /// </summary>
        [JsonPropertyName("platformUpdateDomainCount")]
        public int PlatformUpdateDomainCount { get; set; }

        /// <summary>
        /// The provisioning state of the availability set resource.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
