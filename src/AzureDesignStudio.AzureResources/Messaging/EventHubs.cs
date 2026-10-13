// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Messaging
{
    /// <summary>
    /// Microsoft.EventHub/namespaces
    /// </summary>
    [AzureResource("Microsoft.EventHub/namespaces", "2021-11-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class EventHubs : ResourceBase
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
        public override string Type => "Microsoft.EventHub/namespaces";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2021-11-01";

        /// <summary>
        /// SKU of the Event Hubs namespace.
        /// </summary>
        [JsonPropertyName("sku")]
        public EventHubsSku Sku { get; set; }

        /// <summary>
        /// Namespace description.
        /// </summary>
        [JsonPropertyName("properties")]
        public EventHubsNamespaceProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// EventHubsSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class EventHubsSku
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
    }

    /// <summary>
    /// EventHubsNamespaceProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class EventHubsNamespaceProperties
    {
        /// <summary>
        /// An alternate name for the namespace that can be used to disable auto-ingestion.
        /// </summary>
        [JsonPropertyName("disableAutoIngestion")]
        public bool DisableAutoIngestion { get; set; }

        /// <summary>
        /// Provisioning status of the Namespace.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
