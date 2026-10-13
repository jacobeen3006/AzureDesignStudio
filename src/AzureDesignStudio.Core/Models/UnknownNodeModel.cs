using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.Core.DTO;
using Blazor.Diagrams.Core.Geometry;

namespace AzureDesignStudio.Core.Models
{
    /// <summary>
    /// Placeholder node for a stencil/DTO key this build cannot resolve.
    /// Renders with its catalog icon and degrades to no export rather than crashing:
    /// designs saved by a newer version stay openable, editable and saveable
    /// (the unknown node itself is preserved via <see cref="AzureNodeBase.StencilKey"/>).
    /// </summary>
    public class UnknownNodeModel : AzureNodeBase
    {
        public UnknownNodeModel()
        {
        }

        public UnknownNodeModel(string typeKey)
        {
            StencilKey = typeKey;
        }

        public override string ServiceName => "Unknown resource";
        public override Type? DataFormType => null;
        public override bool IsValid => false;
        public override string ResourceId => string.Empty;
        public override string Location { get => string.Empty; set { } }

        public override IList<ResourceBase> GetArmResources() => new List<ResourceBase>();

        public override IDictionary<string, Parameter> GetArmParameters() => new Dictionary<string, Parameter>();

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return new AzureNodeDto
            {
                TypeKey = StencilKey,
                ImagePath = ImagePath,
                Id = Id,
                Locked = Locked,
                Position = Position ?? Point.Zero,
                Size = Size ?? new Size(81, 81),
                GroupId = Group?.Id ?? string.Empty,
                Name = Name,
                UseResourceGroupLocation = UseResourceGroupLocation,
            };
        }
    }
}
