// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.App
{
    /// <summary>
    /// Microsoft.App/managedEnvironments
    /// </summary>
    [AzureResource("Microsoft.App/managedEnvironments", "2024-02-02")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ManagedEnvironments : ResourceBase
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
        public override string Type => "Microsoft.App/managedEnvironments";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-02-02";

        /// <summary>
        /// Managed environment specific properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public ManagedEnvironmentProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// ManagedEnvironmentProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ManagedEnvironmentProperties
    {
        /// <summary>
        /// Is the environment zone redundant.
        /// </summary>
        [JsonPropertyName("zoneRedundant")]
        public bool ZoneRedundant { get; set; }
    }
}
