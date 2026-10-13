// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.KeyVault
{
    /// <summary>
    /// Microsoft.KeyVault/vaults
    /// </summary>
    [AzureResource("Microsoft.KeyVault/vaults", "2023-07-01")]
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
        public override string Type => "Microsoft.KeyVault/vaults";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2023-07-01";

        /// <summary>
        /// SKU details.
        /// </summary>
        [JsonPropertyName("sku")]
        public VaultsSku Sku { get; set; }

        /// <summary>
        /// Properties describing the Key vault as a resource
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
    /// VaultsSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class VaultsSku
    {
        /// <summary>
        /// SKU family to choose. Currently, the only supported family is 'A'.
        /// </summary>
        [JsonPropertyName("family")]
        public string Family { get; set; }

        /// <summary>
        /// SKU name to choose.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// VaultsProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class VaultsProperties
    {
        /// <summary>
        /// An object containing info about the tenant for the vault.
        /// </summary>
        [JsonPropertyName("tenantId")]
        public string TenantId { get; set; }

        /// <summary>
        /// Property to specify whether Azure Key Vault uses Azure RBAC for authorization for data plane operations.
        /// </summary>
        [JsonPropertyName("enableRbacAuthorization")]
        public bool EnableRbacAuthorization { get; set; }

        /// <summary>
        /// An array of 0 to 1024 objects that specifies the access rules to keys, secrets and certificates.
        /// </summary>
        [JsonPropertyName("accessPolicies")]
        public IList<object> AccessPolicies { get; set; }

        /// <summary>
        /// The URI of the vault, assigned by the cloud provider at creation.
        /// </summary>
        [JsonPropertyName("vaultUri")]
        public string VaultUri { get; set; }

        /// <summary>
        /// The provisioning state of the vault in Azure Resource Manager.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
