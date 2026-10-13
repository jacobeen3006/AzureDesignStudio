// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.ContainerRegistry
{
    /// <summary>
    /// Microsoft.ContainerRegistry/registries
    /// </summary>
    [AzureResource("Microsoft.ContainerRegistry/registries", "2023-07-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class Registries : ResourceBase
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
        public override string Type => "Microsoft.ContainerRegistry/registries";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2023-07-01";

        /// <summary>
        /// The SKU of the registry.
        /// </summary>
        [JsonPropertyName("sku")]
        public ContainerRegistrySku Sku { get; set; }

        /// <summary>
        /// The registry properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public ContainerRegistryProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// ContainerRegistrySku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerRegistrySku
    {
        /// <summary>
        /// The SKU name of the registry.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// ContainerRegistryProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerRegistryProperties
    {
        /// <summary>
        /// Whether the admin user is enabled.
        /// </summary>
        [JsonPropertyName("adminUserEnabled")]
        public bool AdminUserEnabled { get; set; }

        /// <summary>
        /// The login server hostname of the registry.
        /// </summary>
        [JsonPropertyName("loginServer")]
        public string LoginServer { get; set; }

        /// <summary>
        /// The status of the registry at the time of the operation.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
