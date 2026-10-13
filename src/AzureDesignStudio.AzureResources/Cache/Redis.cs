// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Cache
{
    /// <summary>
    /// Microsoft.Cache/redis
    /// </summary>
    [AzureResource("Microsoft.Cache/redis", "2023-08-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class Redis : ResourceBase
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
        public override string Type => "Microsoft.Cache/redis";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2023-08-01";

        /// <summary>
        /// The type of Redis cache to deploy. Valid values include (A) Basic, (B) Standard, (C) Premium, (P)
        /// Premium, (E) Enterprise, (F) EnterpriseFlash.
        /// </summary>
        [JsonPropertyName("sku")]
        public RedisSku Sku { get; set; }

        /// <summary>
        /// Redis resource properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public RedisProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }

        /// <summary>
        /// If true, this Redis Cache will have only one master and no slave.
        /// </summary>
        [JsonPropertyName("zones")]
        public IList<string> Zones { get; set; }
    }

    /// <summary>
    /// RedisSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class RedisSku
    {
        /// <summary>
        /// The type of Redis cache to deploy. Valid values include (A) Basic, (B) Standard, (C) Premium, (P)
        /// Premium, (E) Enterprise, (F) EnterpriseFlash.
        /// </summary>
        [JsonPropertyName("family")]
        public string Family { get; set; }

        /// <summary>
        /// The size of the Redis cache to deploy. Valid values: for C (Basic/Standard) family (0, 1, 2, 3, 4,
        /// 5), for P (Premium) family (0, 1, 2, 3, 4, 5).
        /// </summary>
        [JsonPropertyName("capacity")]
        public int Capacity { get; set; }

        /// <summary>
        /// The name of the Redis cache to deploy.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// RedisProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class RedisProperties
    {
        /// <summary>
        /// The minimum TLS version.
        /// </summary>
        [JsonPropertyName("minimumTlsVersion")]
        public string MinimumTlsVersion { get; set; }

        /// <summary>
        /// Enable the non SSL port.
        /// </summary>
        [JsonPropertyName("enableNonSslPort")]
        public bool EnableNonSslPort { get; set; }

        /// <summary>
        /// The provisioning state of the Redis cache.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }

        /// <summary>
        /// The host name of the Redis cache.
        /// </summary>
        [JsonPropertyName("hostname")]
        public string HostName { get; set; }
    }
}
