// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.AzureResources.Network
{
    /// <summary>
    /// DnsZonePropertiesFormat
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class DnsZonePropertiesFormat
    {
        /// <summary>
        /// The maximum number of record sets that can be created in this DNS zone.
        /// </summary>
        [JsonPropertyName("maxNumberOfRecordSets")]
        public int MaxNumberOfRecordSets { get; set; }

        /// <summary>
        /// The current number of record sets in this DNS zone.
        /// </summary>
        [JsonPropertyName("numberOfRecordSets")]
        public int NumberOfRecordSets { get; set; }
    }
}
