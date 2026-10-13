// Licensed under the MIT License. See LICENSE in the repo root for license details.
using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.DocumentDB
{
    /// <summary>Microsoft.DocumentDB/databaseAccounts</summary>
    [AzureResource("Microsoft.DocumentDB/databaseAccounts", "2023-04-15")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class DatabaseAccounts : ResourceBase
    {
        [JsonIgnore] public string Id { get; private set; }
        [JsonPropertyName("type")] public override string Type => "Microsoft.DocumentDB/databaseAccounts";
        [JsonPropertyName("apiVersion")] public override string ApiVersion => "2023-04-15";
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("properties")] public DatabaseAccountsProperties Properties { get; set; }
        [JsonIgnore] public string Etag { get; private set; }
    }

    /// <summary>DatabaseAccountsProperties</summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class DatabaseAccountsProperties
    {
        [JsonPropertyName("consistencyPolicy")] public DatabaseAccountsConsistencyPolicy ConsistencyPolicy { get; set; }
        [JsonPropertyName("enableMultipleWriteLocations")] public bool EnableMultipleWriteLocations { get; set; }
    }

    /// <summary>DatabaseAccountsConsistencyPolicy</summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class DatabaseAccountsConsistencyPolicy
    {
        [JsonPropertyName("defaultConsistencyLevel")] public string DefaultConsistencyLevel { get; set; }
    }
}
