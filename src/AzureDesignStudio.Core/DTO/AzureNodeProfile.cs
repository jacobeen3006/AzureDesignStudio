using AutoMapper;
using AzureDesignStudio.Core.Network;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
namespace AzureDesignStudio.Core.DTO
{
    public partial class AzureNodeProfile : Profile
    {
        private void CreateMapForAzureNode<TSource, TTarget>(string typeKey) where TSource : NodeModel where TTarget : AzureNodeDto
        {
            CreateMap<TSource, TTarget>()
                .ForMember(d => d.GroupId, a => a.MapFrom(s => s.Group != null ? s.Group.Id : string.Empty))
                .ForMember(d => d.Size, a => a.MapFrom(s => s.Size ?? new Size(81, 81)))
                .AfterMap((s, d) => d.TypeKey = typeKey)
                .ReverseMap()
                .ForMember(d => d.Group, a => a.Ignore());
        }
        public AzureNodeProfile()
        {
            CreateMap<LinkModel, LinkModelDto>()
                .ForMember(d => d.SourcePortParentId, a => a.MapFrom(s => GetAnchorParentId(s.Source)))
                .ForMember(d => d.SourcePortAlignment, a => a.MapFrom(s => GetAnchorAlignment(s.Source)))
                .ForMember(d => d.TargetPortParentId, a => a.MapFrom(s => GetAnchorParentId(s.Target)))
                .ForMember(d => d.TargetPortAlignment, a => a.MapFrom(s => GetAnchorAlignment(s.Target)));

            CreateMap<FirewallPolicyModel, FirewallPolicyModelDto>()
                .ReverseMap();

            CreateAzureNodeMaps();
        }
        private partial void CreateAzureNodeMaps();
        private static string GetAnchorParentId(Anchor anchor) => anchor switch
        {
            SinglePortAnchor portAnchor => portAnchor.Port.Parent.Id,
            _ => anchor.Model is NodeModel n ? n.Id : string.Empty,
        };
        private static PortAlignment GetAnchorAlignment(Anchor anchor) => (anchor as SinglePortAnchor)?.Port.Alignment ?? PortAlignment.Bottom;
    }
}
