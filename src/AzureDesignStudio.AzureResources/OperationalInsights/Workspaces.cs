// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.OperationalInsights
{
    /// <summary>
    /// Microsoft.OperationalInsights/workspaces
    /// </summary>
    [AzureResource("Microsoft.OperationalInsights/workspaces", "2023-09-01")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class Workspaces : ResourceBase
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
        public override string Type => "Microsoft.OperationalInsights/workspaces";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2023-09-01";

        /// <summary>
        /// The SKU of the workspace.
        /// </summary>
        [JsonPropertyName("sku")]
        public WorkspacesSku Sku { get; set; }

        /// <summary>
        /// The properties of a Log Analytics workspace.
        /// </summary>
        [JsonPropertyName("properties")]
        public WorkspacesProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// WorkspacesSku
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class WorkspacesSku
    {
        /// <summary>
        /// The SKU name of the workspace.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// WorkspacesProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class WorkspacesProperties
    {
        /// <summary>
        /// The data retention in days for the workspace.
        /// </summary>
        [JsonPropertyName("retentionInDays")]
        public int RetentionInDays { get; set; }

        /// <summary>
        /// Whether interactive queries are enabled for the workspace.
        /// </summary>
        [JsonPropertyName("enableInteractiveQuery")]
        public bool EnableInteractiveQuery { get; set; }

        /// <summary>
        /// The provisioning state of the workspace.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }
}
