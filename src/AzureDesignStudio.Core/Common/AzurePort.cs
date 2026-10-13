using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

namespace AzureDesignStudio.Core.Common
{
    /// <summary>
    /// A port that also consults the target's <see cref="PortModel.CanAttachTo"/>.
    /// 3.x link behavior only checks the source port, so this restores the fork's
    /// bidirectional attach validation.
    /// </summary>
    public class AzurePort : PortModel
    {
        private static bool _evaluatingAttach;

        public AzurePort(NodeModel parent, PortAlignment alignment = PortAlignment.Bottom, Point? position = null, Size? size = null)
            : base(parent, alignment, position, size)
        {
        }

        public override bool CanAttachTo(ILinkable other)
        {
            if (_evaluatingAttach)
                return base.CanAttachTo(other);

            _evaluatingAttach = true;
            try
            {
                return base.CanAttachTo(other) && (other is not PortModel p || p.CanAttachTo(this));
            }
            finally
            {
                _evaluatingAttach = false;
            }
        }
    }
}
