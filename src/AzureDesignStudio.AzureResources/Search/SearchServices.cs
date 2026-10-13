// Licensed under the MIT License. See LICENSE in the repo root for license details.
using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Search
{
    /// <summary>Microsoft.Search/searchServices</summary>
    [AzureResource("Microsoft.Search/searchServices", "2023-11-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class SearchServices : ResourceBase
    {
        [JsonIgnore] public string Id { get; private set; }
        [JsonPropertyName("type")] public override string Type => "Microsoft.Search/searchServices";
        [JsonPropertyName("apiVersion")] public override string ApiVersion => "2023-11-01";
        [JsonPropertyName("sku")] public SearchServicesSku Sku { get; set; }
        [JsonPropertyName("properties")] public SearchServicesProperties Properties { get; set; }
        [JsonIgnore] public string Etag { get; private set; }
    }

    /// <summary>SearchServicesSku</summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class SearchServicesSku
    {
        [JsonPropertyName("name")] public string Name { get; set; }
    }

    /// <summary>SearchServicesProperties</summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class SearchServicesProperties
    {
        [JsonPropertyName("replicaCount")] public int ReplicaCount { get; set; }
        [JsonPropertyName("partitionCount")] public int PartitionCount { get; set; }
    }
}
