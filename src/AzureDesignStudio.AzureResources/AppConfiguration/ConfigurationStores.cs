// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.AppConfiguration
{
    /// <summary>
    /// Microsoft.AppConfiguration/configurationStores
    /// </summary>
    [AzureResource("Microsoft.AppConfiguration/configurationStores", "2024-05-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ConfigurationStores : ResourceBase
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
        public override string Type => "Microsoft.AppConfiguration/configurationStores";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-05-01";

        /// <summary>
        /// SKU details.
        /// </summary>
        [JsonPropertyName("sku")]
        public ConfigurationStoresSku Sku { get; set; }

        /// <summary>
        /// Properties describing the App Configuration store as a resource
        /// </summary>
        [JsonPropertyName("properties")]
        public ConfigurationStoresProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// ConfigurationStoresSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ConfigurationStoresSku
    {
        /// <summary>
        /// SKU name to choose.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// ConfigurationStoresProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ConfigurationStoresProperties
    {
        /// <summary>
        /// Disables local authentication.
        /// </summary>
        [JsonPropertyName("disableLocalAuth")]
        public bool DisableLocalAuth { get; set; }
    }
}
