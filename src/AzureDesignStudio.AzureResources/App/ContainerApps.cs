// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.CodeDom.Compiler;
using System.Text.Json.Serialization;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.AzureResources.App
{
    /// <summary>
    /// Microsoft.App/containerApps
    /// </summary>
    [AzureResource("Microsoft.App/containerApps", "2024-02-02")]
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerApps : ResourceBase
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
        public override string Type => "Microsoft.App/containerApps";

        /// <summary>
        /// The resource api version
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public override string ApiVersion => "2024-02-02";

        /// <summary>
        /// Container app specific properties.
        /// </summary>
        [JsonPropertyName("properties")]
        public ContainerAppProperties Properties { get; set; }

        /// <summary>
        /// A unique read-only string that changes whenever the resource is updated.
        /// </summary>
        [JsonIgnore]
        public string Etag { get; private set; }
    }

    /// <summary>
    /// ContainerAppProperties
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerAppProperties
    {
        /// <summary>
        /// Resource id of the managed environment that this container app belongs to.
        /// </summary>
        [JsonPropertyName("managedEnvironmentId")]
        public string ManagedEnvironmentId { get; set; }

        /// <summary>
        /// The container app configuration.
        /// </summary>
        [JsonPropertyName("configuration")]
        public ContainerAppConfiguration Configuration { get; set; }

        /// <summary>
        /// The container app revision template.
        /// </summary>
        [JsonPropertyName("template")]
        public ContainerAppTemplate Template { get; set; }

        /// <summary>
        /// The provisioning state of this container app.
        /// </summary>
        [JsonPropertyName("provisioningState")]
        public string ProvisioningState { get; set; }

        /// <summary>
        /// The current state of the container app.
        /// </summary>
        [JsonPropertyName("provisionedState")]
        public string ProvisionedState { get; set; }
    }

    /// <summary>
    /// ContainerAppConfiguration
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerAppConfiguration
    {
        /// <summary>
        /// Active revisions mode.
        /// </summary>
        [JsonPropertyName("activeRevisionsMode")]
        public string ActiveRevisionsMode { get; set; }

        /// <summary>
        /// Configures the ingress settings.
        /// </summary>
        [JsonPropertyName("ingress")]
        public ContainerAppIngress Ingress { get; set; }
    }

    /// <summary>
    /// ContainerAppIngress
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerAppIngress
    {
        /// <summary>
        /// Whether the ingress accepts external requests.
        /// </summary>
        [JsonPropertyName("external")]
        public bool External { get; set; }

        /// <summary>
        /// Target port of ingress target container.
        /// </summary>
        [JsonPropertyName("targetPort")]
        public int TargetPort { get; set; }
    }

    /// <summary>
    /// ContainerAppTemplate
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerAppTemplate
    {
        /// <summary>
        /// List of container images used by this container app revision.
        /// </summary>
        [JsonPropertyName("containers")]
        public IList<ContainerAppContainer> Containers { get; set; }
    }

    /// <summary>
    /// ContainerAppContainer
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerAppContainer
    {
        /// <summary>
        /// The container image name.
        /// </summary>
        [JsonPropertyName("image")]
        public string Image { get; set; }

        /// <summary>
        /// The container resource requirements.
        /// </summary>
        [JsonPropertyName("resources")]
        public ContainerAppContainerResources Resources { get; set; }
    }

    /// <summary>
    /// ContainerAppContainerResources
    /// </summary>
    [GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]
    public partial class ContainerAppContainerResources
    {
        /// <summary>
        /// Required CPU of the container.
        /// </summary>
        [JsonPropertyName("cpu")]
        public string Cpu { get; set; }

        /// <summary>
        /// Required memory (in GiB) of the container.
        /// </summary>
        [JsonPropertyName("memory")]
        public string Memory { get; set; }
    }
}
