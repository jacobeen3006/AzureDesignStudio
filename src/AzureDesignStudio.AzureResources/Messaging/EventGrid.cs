// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Messaging
{
    /// <summary>
    /// Microsoft.EventGrid/topics
    /// </summary>
    [AzureResource("Microsoft.EventGrid/topics", "2024-06-01-preview")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class EventGrid : ResourceBase
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
        public override string Type => "Microsoft.EventGrid/topics";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-06-01-preview";

        /// <summary>
        /// Topic event details.
        /// </summary>
        [JsonPropertyName("properties")]
        public EventGridTopicProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// EventGridTopicProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class EventGridTopicProperties
    {
        /// <summary>
        /// The schema of the events that are sent to this topic.
        /// </summary>
        [JsonPropertyName("inputSchema")]
        public string InputSchema { get; set; }

        /// <summary>
        /// The default time to live in minutes for the events.
        /// </summary>
        [JsonPropertyName("defaultTimeToLiveInMinutes")]
        public int DefaultTimeToLiveInMinutes { get; set; }

        /// <summary>
        /// Name of the provisioning state of the topic.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
