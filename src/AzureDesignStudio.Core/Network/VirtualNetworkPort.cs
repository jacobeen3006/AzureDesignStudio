using AzureDesignStudio.Core.Common;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

namespace AzureDesignStudio.Core.Network
{
    public class VirtualNetworkPort : AzurePort
    {
        public VirtualNetworkPort(NodeModel parent, PortAlignment alignment, Point? position = null, Size? size = null)
            : base(parent, alignment, position, size)
        {
        }

        public override bool CanAttachTo(ILinkable other)
        {
            if (other is not PortModel port || !base.CanAttachTo(port) || port is not VirtualNetworkPort)
                return false;

            foreach (var sibling in Parent.Ports)
            {
                // Only one vnet-peering can be established.
                if (sibling.Links.Any(l =>
                    (l.Source.Model is PortModel sp && sp.Parent.Id == port.Parent.Id)
                    || (l.Target.Model is PortModel tp && tp.Parent.Id == port.Parent.Id)))
                    return false;
            }

            return true;
        }
    }
}
