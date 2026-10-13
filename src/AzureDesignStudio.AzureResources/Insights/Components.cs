// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Insights
{
    /// <summary>
    /// Microsoft.Insights/components
    /// </summary>
    [AzureResource("Microsoft.Insights/components", "2020-02-02")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class Components : ResourceBase
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
        public override string Type => "Microsoft.Insights/components";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2020-02-02";

        /// <summary>
        /// The kind of the resource.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        /// <summary>
        /// Properties describing the Application Insights component as a resource
        /// </summary>
        [JsonPropertyName("properties")]
        public ComponentsProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// ComponentsProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ComponentsProperties
    {
        /// <summary>
        /// The ingestion mode of the Application Insights component.
        /// </summary>
        [JsonPropertyName("ingestionMode")]
        public string IngestionMode { get; set; }
    }
}
