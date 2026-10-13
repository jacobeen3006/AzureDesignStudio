// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.App
{
    /// <summary>
    /// Microsoft.App/jobs
    /// </summary>
    [AzureResource("Microsoft.App/jobs", "2024-02-02")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class Jobs : ResourceBase
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
        public override string Type => "Microsoft.App/jobs";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-02-02";

        /// <summary>
        /// The container app job specific properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public JobProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// JobProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class JobProperties
    {
        /// <summary>
        /// Resource id of the managed environment that this job belongs to.
        /// </summary>
        [JsonPropertyName("environmentId")]
        public string EnvironmentId { get; set; }

        /// <summary>
        /// The container app job configuration.
        /// </summary>
        [JsonPropertyName("configuration")]
        public JobConfiguration Configuration { get; set; }

        /// <summary>
        /// The container app job execution template.
        /// </summary>
        [JsonPropertyName("template")]
        public JobTemplate Template { get; set; }

        /// <summary>
        /// The provisioning state of this container app job.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }
    }

    /// <summary>
    /// JobConfiguration
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class JobConfiguration
    {
        /// <summary>
        /// The type of job trigger.
        /// </summary>
        [JsonPropertyName("triggerType")]
        public string TriggerType { get; set; }

        /// <summary>
        /// A cron expression that specifies the schedule of a recurring job.
        /// </summary>
        [JsonPropertyName("schedule")]
        public string Schedule { get; set; }
    }

    /// <summary>
    /// JobTemplate
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class JobTemplate
    {
        /// <summary>
        /// List of container images used by this job execution.
        /// </summary>
        [JsonPropertyName("containers")]
        public IList<JobContainer> Containers { get; set; }
    }

    /// <summary>
    /// JobContainer
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class JobContainer
    {
        /// <summary>
        /// The container image name.
        /// </summary>
        [JsonPropertyName("image")]
        public string Image { get; set; }
    }
}
