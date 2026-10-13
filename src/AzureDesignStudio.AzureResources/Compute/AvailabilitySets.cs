// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Compute
{
    /// <summary>
    /// Microsoft.Compute/availabilitySets
    /// </summary>
    [AzureResource("Microsoft.Compute/availabilitySets", "2024-03-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class AvailabilitySets : ResourceBase
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
        public override string Type => "Microsoft.Compute/availabilitySets";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-03-01";

        /// <summary>
        /// Sku type which is used for publishing platform such as on demand and reserved.
        /// </summary>
        [JsonPropertyName("sku")]
        public Sku Sku { get; set; }

        /// <summary>
        /// The availability set properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public AvailabilitySetProperties Properties { get; set; }
    }
}
