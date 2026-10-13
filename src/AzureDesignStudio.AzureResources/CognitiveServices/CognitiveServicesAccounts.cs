// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.CognitiveServices
{
    /// <summary>
    /// Microsoft.CognitiveServices/accounts
    /// </summary>
    [AzureResource("Microsoft.CognitiveServices/accounts", "2024-10-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class CognitiveServicesAccounts : ResourceBase
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
        public override string Type => "Microsoft.CognitiveServices/accounts";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-10-01";

        /// <summary>
        /// The resource model definition representing SKU.
        /// </summary>
        [JsonPropertyName("sku")]
        public CognitiveServicesAccountsSku Sku { get; set; }

        /// <summary>
        /// The kind of the cognitive services account.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        /// <summary>
        /// An empty object for future extension.
        /// </summary>
        [JsonPropertyName("identity")]
        public object Identity { get; set; }

        /// <summary>
        /// The properties of the cognitive services account.
        /// </summary>
        [JsonPropertyName("properties")]
        public CognitiveServicesAccountsProperties Properties { get; set; }
    }

    /// <summary>
    /// CognitiveServicesAccountsSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class CognitiveServicesAccountsSku
    {
        /// <summary>
        /// The SKU name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// The SKU tier.
        /// </summary>
        [JsonPropertyName("tier")]
        public string Tier { get; set; }

        /// <summary>
        /// The SKU capacity.
        /// </summary>
        [JsonPropertyName("capacity")]
        public int Capacity { get; set; }
    }

    /// <summary>
    /// CognitiveServicesAccountsProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class CognitiveServicesAccountsProperties
    {
        /// <summary>
        /// The subdomain of the resource.
        /// </summary>
        [JsonPropertyName("customSubDomainName")]
        public string CustomSubDomainName { get; set; }

        /// <summary>
        /// Whether the Azure Cognitive Services resource supports authorization by ip
        /// rules or not. Disable to block requests from the Internet. Default value is
        /// 'Enabled'.
        /// </summary>
        [JsonPropertyName("publicNetworkAccess")]
        public string PublicNetworkAccess { get; set; }

        /// <summary>
        /// The endpoint URL specific to the resource.
        /// </summary>
        [JsonPropertyName("endpoint")]
        public string Endpoint { get; set; }

        /// <summary>
        /// The provisioning state of the cognitive services account.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
