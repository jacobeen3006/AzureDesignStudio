// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Fabric
{
    /// <summary>
    /// Microsoft.Fabric/capacities
    /// </summary>
    [AzureResource("Microsoft.Fabric/capacities", "2023-11-11")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class FabricCapacities : ResourceBase
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
        public override string Type => "Microsoft.Fabric/capacities";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2023-11-11";

        /// <summary>
        /// SKU representation.
        /// </summary>
        [JsonPropertyName("sku")]
        public FabricCapacitiesSku Sku { get; set; }

        /// <summary>
        /// Capacity properties that can be configured by the user.
        /// </summary>
        [JsonPropertyName("properties")]
        public FabricCapacitiesProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// FabricCapacitiesSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class FabricCapacitiesSku
    {
        /// <summary>
        /// The SKU name, e.g. F4, F8, F64.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// The SKU tier, always 'Fabric' for Fabric capacities.
        /// </summary>
        [JsonPropertyName("tier")]
        public string Tier { get; set; }
    }

    /// <summary>
    /// FabricCapacitiesProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class FabricCapacitiesProperties
    {
        /// <summary>
        /// The mode of the capacity. Either 'Active' (pay-as-you-go subscription) or
        /// 'Passive' (reserved instance).
        /// </summary>
        [JsonPropertyName("administrationMode")]
        public string AdministrationMode { get; set; }

        /// <summary>
        /// The provisioning state of the capacity.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
