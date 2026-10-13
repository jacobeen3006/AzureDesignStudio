using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Behaviors;
using Blazor.Diagrams.Core.Events;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

namespace AzureDesignStudio.Services
{
    /// <summary>
    /// DragMovablesBehavior (MIT, Z.Blazor.Diagrams) extended with alignment snapping:
    /// while dragging a single node, its edges snap to the nearest edge/center of any
    /// other node or group within a small threshold, and <see cref="GuideChanged"/>
    /// publishes the snapped model-space coordinates so the UI can draw guide lines.
    /// </summary>
    public sealed class SnappingDragBehavior : DragMovablesBehavior
    {
        private const double ThresholdPx = 5;

        private readonly Diagram diagram;
        private NodeModel? dragged;
        private double? guideX, guideY;

        /// <summary>Snapped guide coordinates in model space; null clears the guide.</summary>
        public event Action<double?, double?>? GuideChanged;

        public SnappingDragBehavior(Diagram diagram) : base(diagram)
        {
            this.diagram = diagram;
            // Base subscribed its private handlers first in its ctor, so the snap runs
            // on the already grid-moved position.
            diagram.PointerDown += OnPointerDownSnap;
            diagram.PointerMove += OnPointerMoveSnap;
            diagram.PointerUp += OnPointerUpSnap;
        }

        private void OnPointerDownSnap(Model? model, PointerEventArgs e)
        {
            // Same selection set the base behavior drags: single movable node/group only.
            var models = diagram.GetSelectedModels().ToList();
            dragged = null;
            if (models.Count == 1)
            {
                dragged = models[0] switch
                {
                    NodeModel n when n.Group is null => n,
                    GroupModel g => g,
                    _ => null,
                };
            }
        }

        private void OnPointerMoveSnap(Model? model, PointerEventArgs e)
        {
            if (dragged is null)
                return;

            var bounds = dragged.GetBounds(includePorts: false);
            if (bounds is null)
                return;

            // Grid landing points are up to half a grid step from the pointer, so the
            // snap window must clear the grid quantization or alignment never engages.
            var threshold = ThresholdPx / Math.Max(diagram.Zoom, 0.01) + diagram.Options.GridSize.GetValueOrDefault();
            var targets = diagram.Nodes.OfType<NodeModel>()
                .Concat(diagram.Groups)
                .Where(m => !ReferenceEquals(m, dragged))
                .Select(m => m.GetBounds(includePorts: false))
                .Where(b => b is not null)
                .Select(b => b!)
                .ToList();
            if (targets.Count == 0)
                return;

            var centerX = bounds.Left + bounds.Width / 2;
            var centerY = bounds.Top + bounds.Height / 2;
            var (dx, gx) = BestOffset(
                new[] { bounds.Left, centerX, bounds.Right },
                targets.SelectMany(b => new[] { b.Left, b.Left + b.Width / 2, b.Right }),
                threshold);
            var (dy, gy) = BestOffset(
                new[] { bounds.Top, centerY, bounds.Bottom },
                targets.SelectMany(b => new[] { b.Top, b.Top + b.Height / 2, b.Bottom }),
                threshold);

            if (dx != 0 || dy != 0)
                dragged.SetPosition(dragged.Position.X + dx, dragged.Position.Y + dy);

            guideX = gx;
            guideY = gy;
            GuideChanged?.Invoke(guideX, guideY);
        }

        private static (double offset, double? line) BestOffset(double[] edges, IEnumerable<double> targets, double threshold)
        {
            var best = double.MaxValue;
            double? bestTarget = null;
            foreach (var t in targets)
            {
                foreach (var e in edges)
                {
                    var diff = t - e;
                    if (Math.Abs(diff) <= threshold && Math.Abs(diff) < Math.Abs(best))
                    {
                        best = diff;
                        bestTarget = t;
                    }
                }
            }

            return bestTarget is null ? (0, null) : (best, bestTarget);
        }

        private void OnPointerUpSnap(Model? model, PointerEventArgs e)
        {
            dragged = null;
            if (guideX is not null || guideY is not null)
            {
                guideX = guideY = null;
                GuideChanged?.Invoke(null, null);
            }
        }

        public override void Dispose()
        {
            diagram.PointerDown -= OnPointerDownSnap;
            diagram.PointerMove -= OnPointerMoveSnap;
            diagram.PointerUp -= OnPointerUpSnap;
            GuideChanged = null;
            base.Dispose();
        }
    }
}
