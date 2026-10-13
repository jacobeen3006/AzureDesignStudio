// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Compute
{
    /// <summary>
    /// Microsoft.Compute/virtualMachineScaleSets
    /// </summary>
    [AzureResource("Microsoft.Compute/virtualMachineScaleSets", "2024-03-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class VirtualMachineScaleSets : ResourceBase
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
        public override string Type => "Microsoft.Compute/virtualMachineScaleSets";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-03-01";

        /// <summary>
        /// The scale set SKU.
        /// </summary>
        [JsonPropertyName("sku")]
        public VirtualMachineScaleSetsSku Sku { get; set; }

        /// <summary>
        /// The properties of a virtual machine scale set.
        /// </summary>
        [JsonPropertyName("properties")]
        public VirtualMachineScaleSetsProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// VirtualMachineScaleSetsSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class VirtualMachineScaleSetsSku
    {
        /// <summary>
        /// The name of the virtual machine scale set SKU.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// The number of VMs in the scale set.
        /// </summary>
        [JsonPropertyName("capacity")]
        public int Capacity { get; set; }
    }

    /// <summary>
    /// VirtualMachineScaleSetsProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class VirtualMachineScaleSetsProperties
    {
        /// <summary>
        /// Specifies whether the Virtual Machine Scale Set should be overprovisioned.
        /// </summary>
        [JsonPropertyName("overprovision")]
        public bool Overprovision { get; set; }

        /// <summary>
        /// An array of virtual machine profiles.
        /// </summary>
        [JsonPropertyName("virtualMachineProfile")]
        public object VirtualMachineProfile { get; set; }

        /// <summary>
        /// The provisioning state of the virtual machine scale set.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
