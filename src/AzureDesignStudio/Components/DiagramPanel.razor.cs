using AntDesign;
using AzureDesignStudio.Core;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Services;
using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Events;
using PointerEventArgs = Blazor.Diagrams.Core.Events.PointerEventArgs;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using System.Net.Http.Json;

namespace AzureDesignStudio.Components
{
    public partial class DiagramPanel : IDisposable
    {
        private AdsDiagram diagram = null!;
        protected override void OnInitialized()
        {
            base.OnInitialized();

            diagram = adsContext.Diagram;

            DataModelFactory.RegisterAzureModels(diagram);

            diagram.PointerDown += HandlePointerDown;
            diagram.PointerUp += HandlePointerUp;
            diagram.SelectionChanged += HandleSelectionChanged;
            // Attach last so the history gesture commit sees our auto-group record.
            diagram.History.Attach();
            diagram.History.StateChanged += HandleHistoryStateChanged;
            undoAvailable = diagram.History.CanUndo;
            redoAvailable = diagram.History.CanRedo;
            diagram.Snapping.GuideChanged += HandleGuideChanged;
        }

        public void Dispose()
        {
            diagram.History.Detach();
            diagram.History.StateChanged -= HandleHistoryStateChanged;
            diagram.Snapping.GuideChanged -= HandleGuideChanged;
            diagram.PointerDown -= HandlePointerDown;
            diagram.PointerUp -= HandlePointerUp;
            diagram.SelectionChanged -= HandleSelectionChanged;
        }

        #region Drag & Drop
        double clientX, clientY = 0;

        private void HandlePointerDown(Model? node, PointerEventArgs e)
        {
            clientX = e.ClientX;
            clientY = e.ClientY;
            diagram.History.BeginGesture();
        }

        private void HandlePointerUp(Model? node, PointerEventArgs e)
        {
            if ((e.ClientX != clientX || e.ClientY != clientY)
               // && node is not GroupModel
               && node is IAzureNode an
               && node is NodeModel n && n.Group == null)
            {
                var group = GetOverlappedGroup(n);
                var (result, _) = an.IsDrappable(group!);
                if (result)
                {
                    var previousPosition = n.Position;
                    AddNodeToGroup(n, group!);
                    diagram.History.RecordChildMembership(group!, n, added: true, previousPosition);
                }
            }

            diagram.History.EndGesture();
        }
        /// <summary>
        /// Add a node to a group based on the position of the node.
        /// </summary>
        /// <param name="node"> The node to be added.</param>
        /// <returns>True if the node is added successfully.</returns>
        private static bool AddNodeToGroup(NodeModel node, GroupModel? group)
        {
            if (group != null)
            {
                // Set the node's position so the group won't move.
                node.SetPosition(group.Position.X + group.Padding + group.Children.Count, group.Position.Y + group.Padding + group.Children.Count);
                group.AddChild(node);
                return true;
            }

            return false;
        }

        private GroupModel? GetOverlappedGroup(NodeModel node)
        {
            // The node should be added to the group which has the smallest size.
            return diagram.Groups
                .Where(g => g.GetBounds()?.ContainsPoint(node.Position) ?? false)
                .OrderBy(g => g.GetBounds()?.Width * g.GetBounds()?.Height)
                .FirstOrDefault(n => n != node);
        }

        private async Task HandleDrop(DragEventArgs e)
        {
            try
            {
                var position = diagram.GetRelativeMousePoint(e.ClientX, e.ClientY);
                var stencil = adsContext.AllStencils.Single(s => s.Key == adsContext.DraggedStencilKey);
                if (stencil.Category != Models.StencilCategory.Gallery)
                {
                    var node = DataModelFactory.CreateNodeModelFromKey(adsContext.DraggedStencilKey, stencil.Name, stencil.IconPath);
                    if (node is UnknownNodeModel)
                    {
                        messageService.Info($"'{stencil.Name}' has no editor implementation in this version.");
                        return;
                    }
                    node.SetPosition(position.X, position.Y);
                    var overlappedGroup = GetOverlappedGroup(node);

                    var (result, message) = ((IAzureNode)node).IsDrappable(overlappedGroup!);
                    if (!result)
                    {
                        messageService.Error(message);
                        return;
                    }
                    diagram.History.Capture(() => diagram.Batch(() =>
                    {
                        if (node is GroupModel g)
                        {
                            diagram.Groups.Add(g);
                        }
                        else
                        {
                            diagram.Nodes.Add(node);
                        }
                        var previousPosition = node.Position;
                        if (AddNodeToGroup(node, overlappedGroup))
                        {
                            diagram.History.RecordChildMembership(overlappedGroup!, node, added: true, previousPosition);
                        }
                        RefreshGroup(overlappedGroup);
                    }));
                }
                else
                {
                    await LoadReferenceArch(stencil.ReferenceArchPath!);
                }
            }
            catch (NotImplementedException)
            {
                messageService.Info("The component is not implemented yet.");
            }
        }

        private async Task LoadReferenceArch(string refArchPath)
        {
            messageService.Loading("Loading the design ...", 0);
            try
            {
                var httpClient = _clientFactory.CreateClient("AzureDesignStudio.ResourceAccess");
                var diagramGraph = await httpClient.GetFromJsonAsync<DiagramGraph>(refArchPath);
                if (diagramGraph == null)
                {
                    messageService.Error("The reference architecture file could not be read.");
                    return;
                }

                // Programmatic replace: never recorded, and prior history belongs to the old design.
                adsContext.Diagram.History.Suspend(() =>
                {
                    adsContext.Diagram.Nodes.Clear();
                    adsContext.Diagram.Links.Clear();
                    adsContext.Diagram.Groups.Clear();
                    DataModelFactory.LoadDiagramFromDto(adsContext.Diagram, diagramGraph, _mapper);
                });
                adsContext.Diagram.History.Clear();
            }
            catch (Exception ex)
            {
                messageService.Error($"Failed to load the reference architecture: {ex.Message}");
            }
        }
        #endregion

        #region Drawer
        private async Task OpenNodeDrawer()
        {
            var node = diagram.GetSelectedModels().FirstOrDefault();
            if (node is IAzureNode n && n.DataFormType != null)
            {
                RenderFragment drawerContent = builder =>
                {
                    builder.OpenComponent(0, n.DataFormType);
                    builder.CloseComponent();
                };

                RenderFragment child = builder =>
                {
                    builder.OpenComponent<NodeDrawerTemplate>(0);
                    builder.AddAttribute(1, "Node", node);
                    builder.AddAttribute(2, "ChildContent", drawerContent);
                    builder.CloseComponent();
                };

                var options = new DrawerOptions()
                {
                    Title = n.ServiceName,
                    Width = "350",
                    Visible = true,
                    ChildContent = child,
                };
                if (n.DataFormNoPadding)
                    options.BodyStyle = "padding:0px;";

                var drawerRef = await drawerService.CreateAsync(options);
                drawerRef.OnClose = () =>
                {
                    diagram.Batch(() =>
                    {
                        if (node is GroupModel group)
                        {
                            RefreshGroup(group);
                        }
                        node.Refresh();

                    });
                    if (node is NodeModel nm)
                    {
                        nm.Group?.Refresh();
                    }
                    return Task.CompletedTask;
                };
            }
        }

        private void RefreshDiagram()
        {
            diagram.Batch(() =>
            {
                foreach (var group in diagram.Groups)
                    RefreshGroup(group);

                foreach (var node in diagram.Nodes)
                    node.Refresh();
            });
        }

        private void RefreshGroup(GroupModel? group)
        {
            if (group == null)
                return;
            
            foreach (var child in group.Children)
            {
                if (child is GroupModel g)
                {
                    RefreshGroup(g);
                }
                child.Refresh();
            }
            group.Refresh();
        }

        #endregion

        private bool showCtxMenu = false;
        private bool nodeEditable = false;
        private void HandleSelectionChanged(SelectableModel model)
        {
            var stateChanged = false;
            var selectedCount = diagram.GetSelectedModels().Count();
            var isSelected = selectedCount > 0;
            if (showCtxMenu != isSelected)
            {
                showCtxMenu = isSelected;
                stateChanged = true;
            }

            var isSingleNode = selectedCount == 1;
            if (isSingleNode)
            {
                var node = diagram.GetSelectedModels().FirstOrDefault();
                isSingleNode = node is not LinkModel;
            }

            if (nodeEditable != isSingleNode)
            {
                nodeEditable = isSingleNode;
                stateChanged = true;
            }

            if (stateChanged)
                InvokeAsync(StateHasChanged);
        }
    }
}