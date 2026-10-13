using AzureDesignStudio.Core.Components;
using Blazor.Diagrams;
using Blazor.Diagrams.Options;
using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Behaviors;
using Blazor.Diagrams.Core.Extensions;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using System.Linq;

namespace AzureDesignStudio.Services
{
    /// <summary>
    /// App-specific diagram: fork-only APIs (delete selection, print capture) on top of
    /// <see cref="BlazorDiagram"/> 3.x.
    /// </summary>
    public class AdsDiagram : BlazorDiagram
    {
        private Rectangle? _savedContainer;
        private Point _savedPan = Point.Zero;
        private double _savedZoom = 1;

        public AdsDiagram(BlazorDiagramOptions options) : base(options)
        {
            RegisterComponent<NodeModel, AzureNodeComponent>();
            // Delete is handled by the UI context menu; Ctrl+Alt+G grouping stays disabled.
            var shortcuts = GetBehavior<KeyboardShortcutsBehavior>();
            shortcuts?.RemoveShortcut("Delete", false, false, false);
            shortcuts?.RemoveShortcut("g", true, false, true);
        }

        public void DeleteSelectedModels()
        {
            Batch(() =>
            {
                foreach (var sm in GetSelectedModels().ToList())
                {
                    if (sm.Locked)
                        continue;

                    if (sm is GroupModel group)
                        Groups.Delete(group);
                    else if (sm is NodeModel node)
                        Nodes.Remove(node);
                    else if (sm is BaseLinkModel link)
                        Links.Remove(link);
                }
            });

            UnselectAll();
        }

        public Task<Rectangle?> BeforePrint(double margin = 50)
        {
            _savedContainer = Container;
            _savedPan = Pan;
            _savedZoom = Zoom;

            var nodes = Nodes.Cast<NodeModel>().Concat(Groups).ToList();
            var bounds = nodes.Count > 0 ? nodes.GetBounds() : Rectangle.Zero;

            // A4 at 300 dpi, orientation follows the content aspect ratio.
            var landscape = bounds.Width >= bounds.Height;
            var pageW = landscape ? 3508 : 2480;
            var pageH = landscape ? 2480 : 3508;
            var page = new Rectangle(0, 0, pageW, pageH);

            Batch(() =>
            {
                SetContainer(page);
                var w = Math.Max(bounds.Width, 1);
                var h = Math.Max(bounds.Height, 1);
                SetZoom(Math.Min((pageW - 2 * margin) / w, (pageH - 2 * margin) / h));

                // Center the content bounds in the page.
                var cx = pageW / 2.0 - (bounds.Left + bounds.Width / 2) * Zoom;
                var cy = pageH / 2.0 - (bounds.Top + bounds.Height / 2) * Zoom;
                UpdatePan(cx, cy);
            });

            return Task.FromResult<Rectangle?>(page);
        }

        public void AfterPrint()
        {
            Batch(() =>
            {
                if (_savedContainer != null)
                    SetContainer(_savedContainer);
                SetZoom(_savedZoom);
                SetPan(_savedPan.X, _savedPan.Y);
            });
        }
    }
}
