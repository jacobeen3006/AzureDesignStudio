using AzureDesignStudio.Core;
using AzureDesignStudio.Core.Components;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
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
            History = this.EnsureHistory();
            // Replace the stock drag behavior with alignment snapping.
            UnregisterBehavior<DragMovablesBehavior>();
            Snapping = new SnappingDragBehavior(this);
            RegisterBehavior(Snapping);

            var shortcuts = GetBehavior<KeyboardShortcutsBehavior>();
            // Delete is handled by the UI context menu; Ctrl+Alt+G grouping stays disabled.
            shortcuts?.RemoveShortcut("Delete", false, false, false);
            shortcuts?.RemoveShortcut("g", true, false, true);
            // Undo/redo over the DiagramHistory command stack.
            shortcuts?.SetShortcut("z", true, false, false, UndoShortcutAsync);
            shortcuts?.SetShortcut("Z", true, false, false, UndoShortcutAsync);
            shortcuts?.SetShortcut("z", true, true, false, RedoShortcutAsync);
            shortcuts?.SetShortcut("Z", true, true, false, RedoShortcutAsync);
            shortcuts?.SetShortcut("y", true, false, false, RedoShortcutAsync);
            shortcuts?.SetShortcut("Y", true, false, false, RedoShortcutAsync);
            // Copy/paste over the clipboard DTO buffer.
            shortcuts?.SetShortcut("c", true, false, false, CopyShortcutAsync);
            shortcuts?.SetShortcut("v", true, false, false, PasteShortcutAsync);
        }

        public DiagramHistory History { get; }

        private static ValueTask UndoShortcutAsync(Diagram diagram)
        {
            diagram.GetHistory()?.Undo();
            return ValueTask.CompletedTask;
        }

        private static ValueTask RedoShortcutAsync(Diagram diagram)
        {
            diagram.GetHistory()?.Redo();
            return ValueTask.CompletedTask;
        }

        public SnappingDragBehavior Snapping { get; }

        /// <summary>Injected by <see cref="AdsContext"/>; enables copy/paste via the DTO pipeline.</summary>
        public AutoMapper.IMapper? Mapper { get; set; }

        private readonly List<AzureNodeDto> clipboard = new();
        private int pasteSequence;

        private static ValueTask CopyShortcutAsync(Diagram diagram)
        {
            if (diagram is AdsDiagram ads)
                ads.CopySelection();
            return ValueTask.CompletedTask;
        }

        private static ValueTask PasteShortcutAsync(Diagram diagram)
        {
            if (diagram is AdsDiagram ads)
                ads.Paste();
            return ValueTask.CompletedTask;
        }

        /// <summary>Serializes selected nodes (and children of selected groups) to the DTO clipboard.</summary>
        public void CopySelection()
        {
            if (Mapper is null)
                return;

            clipboard.Clear();
            pasteSequence = 0;
            foreach (var model in GetSelectedModels())
            {
                try
                {
                    if (model is GroupModel group)
                    {
                        // Subnet models don't implement GetNodeDto; they ride along via their parent.
                        if (model is IAzureNode azGroup)
                            clipboard.Add(azGroup.GetNodeDto(Mapper));
                        foreach (var child in group.Children.OfType<IAzureNode>())
                            clipboard.Add(child.GetNodeDto(Mapper));
                    }
                    else if (model is IAzureNode azNode)
                    {
                        clipboard.Add(azNode.GetNodeDto(Mapper));
                    }
                }
                catch (NotImplementedException)
                {
                    // Type without a DTO mapping is not copyable; skip silently.
                }
            }
        }

        /// <summary>Instantiates the clipboard buffer with fresh ids, offset from the originals.</summary>
        public void Paste()
        {
            if (Mapper is null || clipboard.Count == 0)
                return;

            var offset = 30d * (1 + pasteSequence++ % 8);
            History.Capture(() =>
            {
                var pasted = new Dictionary<string, NodeModel>();

                void Instantiate(AzureNodeDto dto)
                {
                    if (pasted.ContainsKey(dto.Id))
                        return;

                    var node = DataModelFactory.GetNodeModelFromDto(dto, Mapper);
                    if (node is null)
                        return;

                    if (node is AzureNodeBase azure)
                        azure.StencilKey = dto.TypeKey;
                    node.SetPosition(dto.Position.X + offset, dto.Position.Y + offset);
                    if (node is GroupModel group)
                        Groups.Add(group);
                    else
                        Nodes.Add(node);
                    pasted[dto.Id] = node;
                }

                foreach (var dto in clipboard.Where(d => string.IsNullOrEmpty(d.GroupId)))
                    Instantiate(dto);

                foreach (var dto in clipboard.Where(d => !string.IsNullOrEmpty(d.GroupId)))
                {
                    if (!pasted.TryGetValue(dto.GroupId, out var parent) || parent is not GroupModel parentGroup)
                    {
                        // Parent not in the buffer (e.g. copied a lone child): paste standalone
                        // and let the drop rules decide membership at the paste location.
                        Instantiate(dto);
                        var node = pasted[dto.Id];
                        var overlap = GetOverlappedGroup(node);
                        if (overlap is not null && node is IAzureNode az && az.IsDrappable(overlap).Item1)
                        {
                            overlap.AddChild(node);
                            History.RecordChildMembership(overlap, node, added: true);
                        }
                        continue;
                    }

                    Instantiate(dto);
                    if (pasted.TryGetValue(dto.Id, out var child))
                    {
                        parentGroup.AddChild(child);
                        History.RecordChildMembership(parentGroup, child, added: true);
                    }
                }
            });
        }

        private GroupModel? GetOverlappedGroup(NodeModel node) =>
            Groups.Where(g => g.GetBounds()?.ContainsPoint(node.Position) ?? false)
                .OrderBy(g => g.GetBounds()!.Width * g.GetBounds()!.Height)
                .FirstOrDefault(g => g != node);

        public void DeleteSelectedModels()
        {
            History.Capture(() =>
            {
                // Groups lose their children before Removed fires; snapshot for undo.
                foreach (var group in GetSelectedModels().OfType<GroupModel>())
                    History.SnapshotChildren(group);

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
