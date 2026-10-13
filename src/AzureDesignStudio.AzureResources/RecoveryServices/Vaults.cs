// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.RecoveryServices
{
    /// <summary>
    /// Microsoft.RecoveryServices/vaults
    /// </summary>
    [AzureResource("Microsoft.RecoveryServices/vaults", "2024-04-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class Vaults : ResourceBase
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
        public override string Type => "Microsoft.RecoveryServices/vaults";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-04-01";

        /// <summary>
        /// The properties of a Recovery Services vault.
        /// </summary>
        [JsonPropertyName("properties")]
        public VaultsProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// VaultsProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class VaultsProperties
    {
        /// <summary>
        /// The SKU name of the vault (RS0 or RS1).
        /// </summary>
        [JsonPropertyName("skuName")]
        public string SkuName { get; set; }

        /// <summary>
        /// The redundancy of the vault (GRS or ZRS).
        /// </summary>
        [JsonPropertyName("redundancy")]
        public string Redundancy { get; set; }

        /// <summary>
        /// The state of the vault.
        /// </summary>
        [JsonPropertyName("state")]
        public string State { get; set; }
    }
}
