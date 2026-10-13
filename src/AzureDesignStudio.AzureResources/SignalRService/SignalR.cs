// Licensed under the MIT License. See LICENSE in the repo root for license details.
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.SignalRService
{
    /// <summary>Microsoft.SignalRService/signalR</summary>
    [AzureResource("Microsoft.SignalRService/signalR", "2024-03-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class SignalR : ResourceBase
    {
        [JsonIgnore] public string Id { get; private set; }
        [JsonPropertyName("type")] public override string Type => "Microsoft.SignalRService/signalR";
        [JsonPropertyName("apiVersion")] public override string ApiVersion => "2024-03-01";
        [JsonPropertyName("sku")] public SignalRSku Sku { get; set; }
        [JsonPropertyName("properties")] public SignalRProperties Properties { get; set; }
        [JsonIgnore] public string Etag { get; private set; }
    }

    /// <summary>SignalRSku</summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class SignalRSku
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("capacity")] public int Capacity { get; set; }
    }

    /// <summary>SignalRProperties</summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class SignalRProperties
    {
        [JsonPropertyName("features")] public List<SignalRFeature> Features { get; set; }
    }

    /// <summary>SignalRFeature</summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class SignalRFeature
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("value")] public string Value { get; set; }
    }
}
