// Licensed under the MIT License. See LICENSE in the repo root for license details.
using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.DataFactory
{
    /// <summary>Microsoft.DataFactory/factories</summary>
    [AzureResource("Microsoft.DataFactory/factories", "2018-06-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class Factories : ResourceBase
    {
        [JsonIgnore] public string Id { get; private set; }
        [JsonPropertyName("type")] public override string Type => "Microsoft.DataFactory/factories";
        [JsonPropertyName("apiVersion")] public override string ApiVersion => "2018-06-01";
        [JsonPropertyName("properties")] public FactoriesProperties Properties { get; set; }
        [JsonIgnore] public string Etag { get; private set; }
    }

    /// <summary>FactoriesProperties</summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class FactoriesProperties
    {
        [JsonPropertyName("publicDataEndpoint")] public string PublicDataEndpoint { get; set; }
    }
}
