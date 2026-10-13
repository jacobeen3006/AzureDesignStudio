// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Web
{
    /// <summary>
    /// Microsoft.Web/staticSites
    /// </summary>
    [AzureResource("Microsoft.Web/staticSites", "2023-12-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class StaticSites : ResourceBase
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
        public override string Type => "Microsoft.Web/staticSites";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2023-12-01";

        /// <summary>
        /// SKU details.
        /// </summary>
        [JsonPropertyName("sku")]
        public StaticSitesSku Sku { get; set; }

        /// <summary>
        /// Properties describing the Static Web App as a resource
        /// </summary>
        [JsonPropertyName("properties")]
        public StaticSitesProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// StaticSitesSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class StaticSitesSku
    {
        /// <summary>
        /// SKU name to choose.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// StaticSitesProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class StaticSitesProperties
    {
        /// <summary>
        /// The maximum size in GB of the static web app.
        /// </summary>
        [JsonPropertyName("skuSizeCap")]
        public string SkuSizeCap { get; set; }
    }
}
