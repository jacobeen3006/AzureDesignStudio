using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Events;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

namespace AzureDesignStudio.Core.Common
{
    /// <summary>
    /// Undo/redo command stack over 3.x diagram layer events (the library ships none).
    /// Layer/child mutations are recorded unless suspended; pointer gestures and
    /// <see cref="Capture"/> calls coalesce records into one composite command per user action.
    /// Programmatic design loads must call <see cref="Clear"/> or run inside <see cref="Suspend"/>.
    /// </summary>
    public class DiagramHistory
    {
        private const int MaxDepth = 100;

        private readonly Diagram _diagram;
        private readonly LinkedList<Command> _undo = new();
        private readonly Stack<Command> _redo = new();
        private readonly Dictionary<MovableModel, Point> _gesturePositions = new();
        private List<Command>? _gesture;
        // The Groups layer clears a group's children before the Removed event fires,
        // so delete paths pre-snapshot membership for an undoable re-add.
        private readonly Dictionary<GroupModel, NodeModel[]> _pendingChildren = new();
        private bool _suppress;
        private bool _suspended;
        private bool _attached;

        public event Action? StateChanged;

        public DiagramHistory(Diagram diagram)
        {
            _diagram = diagram;
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Subscribes to diagram events. Call after any app handlers so gesture
        /// commits run last on pointer up.</summary>
        public void Attach()
        {
            if (_attached)
                return;
            _attached = true;

            _diagram.Nodes.Added += OnNodeAdded;
            _diagram.Nodes.Removed += OnNodeRemoved;
            _diagram.Groups.Added += OnGroupAdded;
            _diagram.Groups.Removed += OnGroupRemoved;
            _diagram.Links.Added += OnLinkAdded;
            _diagram.Links.Removed += OnLinkRemoved;
            _diagram.PointerDown += OnPointerDown;
            _diagram.PointerUp += OnPointerUp;
        }

        public void Detach()
        {
            if (!_attached)
                return;
            _attached = false;

            _diagram.Nodes.Added -= OnNodeAdded;
            _diagram.Nodes.Removed -= OnNodeRemoved;
            _diagram.Groups.Added -= OnGroupAdded;
            _diagram.Groups.Removed -= OnGroupRemoved;
            _diagram.Links.Added -= OnLinkAdded;
            _diagram.Links.Removed -= OnLinkRemoved;
            _diagram.PointerDown -= OnPointerDown;
            _diagram.PointerUp -= OnPointerUp;
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            _gesture = null;
            StateChanged?.Invoke();
        }

        /// <summary>Runs a user action as one undoable unit.</summary>
        public void Capture(Action action)
        {
            BeginGesture();
            try
            {
                action();
            }
            finally
            {
                EndGesture();
            }
        }

        /// <summary>Runs programmatic mutations (design load, print) without recording.</summary>
        public void Suspend(Action action)
        {
            var was = _suspended;
            _suspended = true;
            try
            {
                action();
            }
            finally
            {
                _suspended = was;
            }
        }

        public void Undo()
        {
            if (_undo.Last is null)
                return;

            var command = _undo.Last.Value;
            _undo.RemoveLast();
            Apply(command.Undo);
            _redo.Push(command);
            StateChanged?.Invoke();
        }

        public void Redo()
        {
            if (_redo.Count == 0)
                return;

            var command = _redo.Pop();
            Apply(command.Redo);
            AddToUndo(command);
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Records a group-membership change, which the library exposes no event for.
        /// <paramref name="previousPosition"/> restores the child's placement when un-grouping
        /// (auto-group snaps the child next to the group origin).
        /// </summary>
        public void RecordChildMembership(GroupModel group, NodeModel child, bool added, Point? previousPosition = null)
        {
            ArgumentNullException.ThrowIfNull(group);
            if (!CanRecord)
                return;

            Push(new ChildCommand(group, child, added, previousPosition));
            _redo.Clear();
            StateChanged?.Invoke();
        }

        /// <summary>Opens a gesture window; all records until <see cref="EndGesture"/> form one command.</summary>
        public void BeginGesture()
        {
            _gesture ??= new List<Command>();
            if (!_suspended && !_suppress)
            {
                _gesturePositions.Clear();
                foreach (var movable in _diagram.Nodes.OfType<MovableModel>().Concat(_diagram.Groups.OfType<MovableModel>()))
                    _gesturePositions[movable] = movable.Position;
            }
        }

        public void EndGesture()
        {
            if (_gesture is null)
                return;

            var commands = _gesture;
            _gesture = null;
            _gesturePositions.Clear();

            if (commands.Count == 1)
                AddToUndo(commands[0]);
            else if (commands.Count > 1)
                AddToUndo(new CompositeCommand(commands));

            if (commands.Count > 0)
                StateChanged?.Invoke();
        }

        private bool CanRecord => _attached && !_suppress && !_suspended;

        private void Apply(Action action)
        {
            _suppress = true;
            try
            {
                _diagram.Batch(action);
            }
            finally
            {
                _suppress = false;
            }
        }

        private void Push(Command command)
        {
            // Library behaviors (e.g. DragNewLinkBehavior) can fire Adds after the
            // pointer-up that closed our gesture window; such events are their own
            // atomic commands and must reach the undo stack directly.
            if (_gesture is { } gesture)
                gesture.Add(command);
            else
                AddToUndo(command);
        }

        private void AddToUndo(Command command)
        {
            _undo.AddLast(command);
            while (_undo.Count > MaxDepth)
                _undo.RemoveFirst();
        }

        #region Recording

        private void OnNodeAdded(NodeModel node)
        {
            node.Moved += OnMoved;
            if (!CanRecord)
                return;

            Push(new LayerModelCommand(_diagram, node, isGroup: false, added: true, Array.Empty<NodeModel>()));
            _redo.Clear();
            StateChanged?.Invoke();
        }

        private void OnNodeRemoved(NodeModel node)
        {
            node.Moved -= OnMoved;
            if (!CanRecord)
                return;

            Push(new LayerModelCommand(_diagram, node, isGroup: false, added: false, Array.Empty<NodeModel>()));
            _redo.Clear();
            StateChanged?.Invoke();
        }

        private void OnGroupAdded(GroupModel group)
        {
            group.Moved += OnMoved;
            if (!CanRecord)
                return;

            Push(new LayerModelCommand(_diagram, group, isGroup: true, added: true, group.Children.ToArray()));
            _redo.Clear();
            StateChanged?.Invoke();
        }

        private void OnGroupRemoved(GroupModel group)
        {
            group.Moved -= OnMoved;
            if (!CanRecord)
                return;

            Push(new LayerModelCommand(_diagram, group, isGroup: true, added: false, _pendingChildren.TryGetValue(group, out var children) ? children : Array.Empty<NodeModel>()));
            _pendingChildren.Remove(group);
            _redo.Clear();
            StateChanged?.Invoke();
        }

        private void OnLinkAdded(BaseLinkModel link)
        {
            if (!CanRecord)
                return;

            Push(new LinkCommand(_diagram, link, added: true));
            _redo.Clear();
            StateChanged?.Invoke();
        }

        private void OnLinkRemoved(BaseLinkModel link)
        {
            if (!CanRecord)
                return;

            Push(new LinkCommand(_diagram, link, added: false));
            _redo.Clear();
            StateChanged?.Invoke();
        }

        private void OnMoved(MovableModel model)
        {
            if (!CanRecord)
                return;

            var to = model.Position;
            var from = _gesturePositions.TryGetValue(model, out var p) ? p : to;
            if (from.Equals(to))
                return;

            Push(new MoveCommand(model, from, to));
            _redo.Clear();
            StateChanged?.Invoke();
        }


        public void SnapshotChildren(GroupModel group)
        {
            if (!CanRecord)
                return;

            _pendingChildren[group] = group.Children.ToArray();
            foreach (var child in group.Children.OfType<GroupModel>())
                SnapshotChildren(child);
        }

        private void OnPointerDown(Model? model, PointerEventArgs e)
        {
            BeginGesture();
        }

        private void OnPointerUp(Model? model, PointerEventArgs e)
        {
            EndGesture();
        }

        #endregion

        #region Commands

        private abstract class Command
        {
            public abstract void Undo();
            public abstract void Redo();
        }

        private sealed class CompositeCommand : Command
        {
            private readonly List<Command> _commands;

            public CompositeCommand(List<Command> commands) => _commands = commands;

            public override void Undo()
            {
                for (var i = _commands.Count - 1; i >= 0; i--)
                    _commands[i].Undo();
            }

            public override void Redo()
            {
                foreach (var command in _commands)
                    command.Redo();
            }
        }

        /// <summary>Adds or removes a node/group from its layer; groups re-attach children on add.</summary>
        private sealed class LayerModelCommand : Command
        {
            private readonly Diagram _diagram;
            private readonly Model _model;
            private readonly bool _isGroup;
            private readonly bool _added;
            private readonly NodeModel[] _children;

            public LayerModelCommand(Diagram diagram, Model model, bool isGroup, bool added, NodeModel[] children)
            {
                _diagram = diagram;
                _model = model;
                _isGroup = isGroup;
                _added = added;
                _children = children;
            }

            public override void Undo()
            {
                if (_added)
                    Remove();
                else
                    Add();
            }

            public override void Redo()
            {
                if (_added)
                    Add();
                else
                    Remove();
            }

            private void Add()
            {
                if (_isGroup)
                {
                    var group = (GroupModel)_model;
                    _diagram.Groups.Add(group);
                    foreach (var child in _children)
                    {
                        if (!group.Children.Contains(child))
                            group.AddChild(child);
                    }
                }
                else
                {
                    _diagram.Nodes.Add((NodeModel)_model);
                }
            }

            private void Remove()
            {
                if (_isGroup)
                    _diagram.Groups.Remove((GroupModel)_model);
                else
                    _diagram.Nodes.Remove((NodeModel)_model);
            }
        }

        private sealed class LinkCommand : Command
        {
            private readonly Diagram _diagram;
            private readonly BaseLinkModel _link;
            private readonly bool _added;

            public LinkCommand(Diagram diagram, BaseLinkModel link, bool added)
            {
                _diagram = diagram;
                _link = link;
                _added = added;
            }

            public override void Undo()
            {
                if (_added)
                    _diagram.Links.Remove(_link);
                else
                    _diagram.Links.Add(_link);
            }

            public override void Redo()
            {
                if (_added)
                    _diagram.Links.Add(_link);
                else
                    _diagram.Links.Remove(_link);
            }
        }

        private sealed class MoveCommand : Command
        {
            private readonly MovableModel _model;
            private readonly Point _from;
            private readonly Point _to;

            public MoveCommand(MovableModel model, Point from, Point to)
            {
                _model = model;
                _from = from;
                _to = to;
            }

            public override void Undo() => _model.SetPosition(_from.X, _from.Y);

            public override void Redo() => _model.SetPosition(_to.X, _to.Y);
        }

        private sealed class ChildCommand : Command
        {
            private readonly GroupModel _group;
            private readonly NodeModel _child;
            private readonly bool _added;
            private readonly Point? _previousPosition;

            public ChildCommand(GroupModel group, NodeModel child, bool added, Point? previousPosition)
            {
                _group = group;
                _child = child;
                _added = added;
                _previousPosition = previousPosition;
            }

            public override void Undo()
            {
                if (_added)
                    Detach();
                else
                    Attach();
            }

            public override void Redo()
            {
                if (_added)
                    Attach();
                else
                    Detach();
            }

            private void Attach()
            {
                if (!_group.Children.Contains(_child))
                    _group.AddChild(_child);
            }

            private void Detach()
            {
                _group.RemoveChild(_child);
                if (_previousPosition is not null)
                    _child.SetPosition(_previousPosition.X, _previousPosition.Y);
            }
        }

        #endregion
    }

    /// <summary>History accessor so Core components (forms) reach the client-created instance.</summary>
    public static class DiagramHistoryExtensions
    {
        private static readonly ConditionalWeakTable<Diagram, DiagramHistory> Histories = new();

        public static DiagramHistory EnsureHistory(this Diagram diagram)
            => Histories.GetValue(diagram, static d => new DiagramHistory(d));

        public static DiagramHistory? GetHistory(this Diagram diagram)
            => Histories.TryGetValue(diagram, out var history) ? history : null;
    }
}
