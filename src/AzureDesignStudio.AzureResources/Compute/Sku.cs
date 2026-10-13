// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.AzureResources.Compute
{
    /// <summary>
    /// Sku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class Sku
    {
        /// <summary>
        /// The SKU name. Only 'Aligned' is supported for availability sets.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// If the SKU supports scale out/in then the capacity integer should be included.
        /// </summary>
        [JsonPropertyName("capacity")]
        public int Capacity { get; set; }
    }
}
