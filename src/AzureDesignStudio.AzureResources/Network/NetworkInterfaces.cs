using AzureDesignStudio.AzureResources.Base;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.AzureResources.Network
{
    /// <summary>
    /// Microsoft.Network/networkInterfaces. Written by hand (the generated set stopped at the
    /// property bag): a virtual machine cannot be deployed without one.
    /// </summary>
    [AzureResource("Microsoft.Network/networkInterfaces", "2023-09-01")]
    public partial class NetworkInterfaces : ResourceBase
    {
        [JsonPropertyName("type")]
        public override string Type => "Microsoft.Network/networkInterfaces";

        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2023-09-01";

        [JsonPropertyName("properties")]
        public NetworkInterfacePropertiesFormat Properties { get; set; }
    }
}
