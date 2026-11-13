// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace AzureDesignStudio.AzureResources.Network
{
    public partial class SubnetPropertiesFormat
    {
        /// <summary>
        /// Whether the subnet gets Azure's default outbound access (an implicit public IP).
        /// Added in api-version 2022-09-01; null leaves the member out of the template.
        /// </summary>
        [JsonPropertyName("defaultOutboundAccess")]
        public bool? DefaultOutboundAccess { get; set; }
    }
}
