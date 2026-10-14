using AntDesign;
using System.Text.Json;
using AzureDesignStudio.Core;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Services;
using Blazor.Diagrams.Core.Behaviors;
using Blazor.Diagrams.Core.Extensions;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.PathGenerators;
using Blazor.Diagrams.Core.Routers;
using Blazor.Diagrams.Options;

namespace AzureDesignStudio.Components.MenuDrawer
{
    /// <summary>
    /// Compare two saved designs side by side (v2 plan 9.2/[27]). The answer comes from the pure
    /// <see cref="DesignDiffEngine"/> (identity = durable ArmId first), never from pixel luck: the
    /// change table is authoritative, the mini canvases are read-only views with per-tile diff
    /// highlighting. Load failures degrade honestly - a table without canvases beats an exception.
    /// </summary>
    public partial class CompareDrawerTemplate
    {
        private List<string> _designNames = new();
        private string? _beforeName;
        private string? _afterName;
        private string? _error;
        private bool _busy;
        private bool _offline;
        private bool _pendingFit;
        private DesignDiff? _diff;
        private AdsDiagram? _beforeDiagram;
        private AdsDiagram? _afterDiagram;

        protected override async Task OnInitializedAsync()
        {
            try
            {
                var (status, names) = await _designService.GetAllSavedDesign();
                _offline = _designService.LastCallWasOffline;
                _designNames = names?.ToList() ?? new List<string>();
                if (status != 200)
                    _error = "The saved design list is not available right now.";
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                _error = "The saved design list could not be loaded.";
            }
        }

        private async Task CompareAsync()
        {
            _error = null;
            _diff = null;
            _beforeDiagram = _afterDiagram = null;
            if (string.IsNullOrEmpty(_beforeName) || string.IsNullOrEmpty(_afterName))
            {
                _error = "Pick a saved design for both sides first.";
                return;
            }
            if (string.Equals(_beforeName, _afterName, StringComparison.Ordinal))
            {
                _error = "The two sides must be different designs.";
                return;
            }

            _busy = true;
            try
            {
                var (beforeStatus, beforeData) = await _designService.LoadDesign(_beforeName);
                if (beforeStatus != 200 || beforeData is null)
                {
                    _error = $"\"{_beforeName}\" could not be loaded.";
                    return;
                }
                var (afterStatus, afterData) = await _designService.LoadDesign(_afterName);
                if (afterStatus != 200 || afterData is null)
                {
                    _error = $"\"{_afterName}\" could not be loaded.";
                    return;
                }

                _diff = DesignDiffEngine.DiffGraphJson(beforeData, afterData);

                // The table is the authoritative answer; a canvas that cannot render must not eat it.
                _beforeDiagram = TryRenderSide(beforeData, beforeSide: true, out var beforeError);
                _afterDiagram = TryRenderSide(afterData, beforeSide: false, out var afterError);
                var renderErrors = new[] { beforeError, afterError }.Where(e => e != null).ToList();
                if (renderErrors.Count > 0)
                    _error = string.Join(" ", renderErrors);
                else
                    _pendingFit = _beforeDiagram is not null || _afterDiagram is not null;
            }
            catch (AdsDesignFormatException ex)
            {
                _error = ex.Message;
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                _error = "The design load failed.";
            }
            finally
            {
                _busy = false;
            }
        }

        private AdsDiagram? TryRenderSide(string graphJson, bool beforeSide, out string? error)
        {
            error = null;
            try
            {
                var graph = JsonSerializer.Deserialize<DiagramGraph>(graphJson)
                    ?? throw new AdsDesignFormatException("The design document is empty.");
                var diagram = CreateMiniDiagram();
                // Saved ids are regenerated on load; the diff keys speak the saved language.
                var savedIds = new Dictionary<NodeModel, string>();
                DataModelFactory.LoadDiagramFromDto(diagram, graph, _mapper, savedIds);
                ApplyHighlights(diagram, savedIds, beforeSide);
                return diagram;
            }
            catch (Exception ex)
            {
                error = $"The {(beforeSide ? "before" : "after")} design could not be rendered as a canvas ({ex.Message}); the change table is still complete.";
                return null;
            }
        }

        private static AdsDiagram CreateMiniDiagram()
        {
            var options = new BlazorDiagramOptions
            {
                AllowPanning = false,
                AllowMultiSelection = false,
                LinksLayerOrder = 1,
            };
            options.Zoom.Enabled = false;
            options.Links.DefaultRouter = new OrthogonalRouter();
            options.Links.DefaultPathGenerator = new StraightPathGenerator();
            options.Links.Factory = DataModelFactory.LinkModelFactory;
            var diagram = new AdsDiagram(options);
            DataModelFactory.RegisterAzureModels(diagram);
            // The panes are viewing surfaces, not editors: strip every interaction behavior.
            diagram.UnregisterBehavior<SelectionBehavior>();
            diagram.UnregisterBehavior<ZoomBehavior>();
            diagram.UnregisterBehavior<PanBehavior>();
            diagram.UnregisterBehavior<DragNewLinkBehavior>();
            diagram.UnregisterBehavior<SnappingDragBehavior>();
            return diagram;
        }

        private void ApplyHighlights(AdsDiagram diagram, Dictionary<NodeModel, string> savedIds, bool beforeSide)
        {
            var kinds = new Dictionary<string, DesignChangeKind>(StringComparer.Ordinal);
            foreach (var change in _diff!.Changes)
                if (change.SubjectKind is "node" or "group")
                    kinds[change.Key] = change.Kind;

            foreach (var model in diagram.Nodes.Cast<NodeModel>().Concat(diagram.Groups))
            {
                var armId = model switch
                {
                    AzureNodeBase node => node.ArmId,
                    AzureGroupBase group => group.ArmId,
                    _ => string.Empty,
                };
                var key = DesignDiffEngine.EntityKey(armId, savedIds.TryGetValue(model, out var saved) ? saved : model.Id);
                if (key is null || !kinds.TryGetValue(key, out var kind))
                    continue;

                var css = kind switch
                {
                    DesignChangeKind.Added => beforeSide ? string.Empty : "ads-diff-added",
                    DesignChangeKind.Removed => beforeSide ? "ads-diff-removed" : string.Empty,
                    DesignChangeKind.Changed => "ads-diff-changed",
                    _ => string.Empty,
                };
                switch (model)
                {
                    case AzureNodeBase node: node.HighlightClass = css; break;
                    case AzureGroupBase group: group.HighlightClass = css; break;
                }
            }
        }

        /// <summary>
        /// The canvas reports its container size asynchronously after mount; fit once both sides
        /// have one (bounded retries, then give up - the table never depends on this).
        /// </summary>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!_pendingFit)
                return;

            _pendingFit = false;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (_beforeDiagram is { Container: not null } before) before.ZoomToFit(24);
                if (_afterDiagram is { Container: not null } after) after.ZoomToFit(24);
                var beforeDone = _beforeDiagram is null or { Container: not null };
                var afterDone = _afterDiagram is null or { Container: not null };
                if (beforeDone && afterDone)
                    break;
                await Task.Delay(100);
            }
        }

        private static string ChipStyle(DesignChangeKind kind) => kind switch
        {
            DesignChangeKind.Added => "background:#237804",
            DesignChangeKind.Removed => "background:#a8071a",
            DesignChangeKind.Retyped => "background:#ad6800",
            DesignChangeKind.LinkAdded => "background:#08979c",
            DesignChangeKind.LinkRemoved => "background:#c41d7f",
            _ => "background:#d48806",
        };

        private static TagColor KindColor(DesignChangeKind kind) => kind switch
        {
            DesignChangeKind.Added => TagColor.Success,
            DesignChangeKind.Removed => TagColor.Error,
            DesignChangeKind.Retyped => TagColor.Gold,
            DesignChangeKind.LinkAdded => TagColor.Cyan,
            DesignChangeKind.LinkRemoved => TagColor.Magenta,
            _ => TagColor.Orange,
        };
    }
}
