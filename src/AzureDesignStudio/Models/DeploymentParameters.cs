using AzureDesignStudio.SharedModels.Protos;
using System.ComponentModel.DataAnnotations;

namespace AzureDesignStudio.Models
{
    public record ArmParameter
    {
        public string Name { get; set; } = default!;
    }
    public record DeploymentParameters
    {
        // Required-ness is target-aware (RG name only matters for the resource-group target), so
        // the drawer handler validates per target instead of via a blanket [Required].
        public string ResourceGroup { get; set; } = default!;
        public string ParametersJson { get; set; } = "{}";

        // Approved deployment target (v2 11.2): the same scope the what-if preview, plan hash and
        // monitor bind to. RESOURCE_GROUP is the legacy default - untouched forms change nothing.
        public DeployTarget Target { get; set; } = DeployTarget.ResourceGroup;

        /// <summary>Required for a MANAGEMENT_GROUP target; ignored by every other kind.</summary>
        public string ManagementGroupId { get; set; } = "";

        /// <summary>Required for every non-resource-group target: ARM has no implicit home region
        /// outside a group, so the caller must name where the deployment itself lives.</summary>
        public string Location { get; set; } = "";
    }
}
