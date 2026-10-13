// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Messaging
{
    /// <summary>
    /// Microsoft.ServiceBus/namespaces
    /// </summary>
    [AzureResource("Microsoft.ServiceBus/namespaces", "2023-09-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ServiceBus : ResourceBase
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
        public override string Type => "Microsoft.ServiceBus/namespaces";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2023-09-01";

        /// <summary>
        /// SKU of the Service Bus namespace.
        /// </summary>
        [JsonPropertyName("sku")]
        public ServiceBusSku Sku { get; set; }

        /// <summary>
        /// MessageBus Namespace Properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public ServiceBusNamespaceProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// ServiceBusSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ServiceBusSku
    {
        /// <summary>
        /// Name of this SKU.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// The billing tier of this SKU.
        /// </summary>
        [JsonPropertyName("tier")]
        public string Tier { get; set; }

        /// <summary>
        /// The messaging throughput units. Only applicable to the Premium SKU.
        /// </summary>
        [JsonPropertyName("capacity")]
        public int Capacity { get; set; }
    }

    /// <summary>
    /// ServiceBusNamespaceProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ServiceBusNamespaceProperties
    {
        /// <summary>
        /// Enabled the namespace to be zone redundant.
        /// </summary>
        [JsonPropertyName("zoneRedundant")]
        public bool ZoneRedundant { get; set; }

        /// <summary>
        /// Provisioning status of the Namespace.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
