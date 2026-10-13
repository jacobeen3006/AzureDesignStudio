// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.Logic
{
    /// <summary>
    /// Microsoft.Logic/workflows
    /// </summary>
    [AzureResource("Microsoft.Logic/workflows", "2019-05-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class Workflows : ResourceBase
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
        public override string Type => "Microsoft.Logic/workflows";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2019-05-01";

        /// <summary>
        /// Properties describing the Logic App workflow as a resource
        /// </summary>
        [JsonPropertyName("properties")]
        public WorkflowsProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// WorkflowsProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class WorkflowsProperties
    {
        /// <summary>
        /// The state of the workflow.
        /// </summary>
        [JsonPropertyName("state")]
        public string State { get; set; }

        /// <summary>
        /// The integration account to be used by the logic app.
        /// </summary>
        [JsonPropertyName("integrationAccount")]
        public string IntegrationAccount { get; set; }
    }
}
