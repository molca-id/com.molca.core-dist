using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.UI;
using Molca.Sequence;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Graph
{
    /// <summary>
    /// The GraphView canvas for the sequence graph editor: builds one <see cref="StepNode"/> per
    /// step, draws edges following the controller's execution order (sequential siblings, parent
    /// descent, parallel fan-out — see <see cref="StepGraphLayoutUtility.ComputeFlowEdges"/>), and reflects the shared
    /// <see cref="StepSelectionModel"/> selection. Provides pan/zoom, grid background, and a minimap.
    /// </summary>
    /// <remarks>
    /// This class owns no editing logic — node selection is reported up to the owning window via
    /// <see cref="StepsSelected"/>, which routes to <see cref="StepSelectionModel"/>; CRUD/reparent
    /// (Sprint 8.3) routes through <see cref="StepEditingService"/> from the window. Edge creation is
    /// blocked here in 8.1 (read-only topology) and enabled in 8.3.
    /// </remarks>
    public sealed class SequenceGraphView : GraphView
    {
        private readonly Dictionary<Step, StepNode> _nodesByStep = new Dictionary<Step, StepNode>();
        private bool _syncingFromModel;
        private StepNode _currentNode;

        /// <summary>
        /// Raised when the user changes the GraphView node selection. Argument is the selected steps
        /// in selection order. The window feeds this into <see cref="StepSelectionModel"/>.
        /// </summary>
        public event Action<IReadOnlyList<Step>> StepsSelected;

        /// <summary>
        /// Raised when the user drags one or more nodes to a new position. The window persists the
        /// updated layout (Sprint 8.2). Does not fire for programmatic placement during a rebuild.
        /// </summary>
        public event Action NodesMoved;

        /// <summary>Raised when the user connects an edge: <c>(newParent, child)</c> — the child should reparent under newParent (Sprint 8.3).</summary>
        public event Action<Step, Step> EdgeReparentRequested;

        /// <summary>Raised when the user removes a parent→child edge: the child should reparent to the controller root (Sprint 8.3).</summary>
        public event Action<Step> EdgeDisconnectRequested;

        /// <summary>Raised when the user requests creation of a typed step under an optional parent (Sprint 8.3).</summary>
        public event Action<Type, Step> CreateStepRequested;

        /// <summary>Raised when the user requests duplication of the given steps' subtrees (Sprint 8.3).</summary>
        public event Action<IReadOnlyList<Step>> DuplicateStepsRequested;

        /// <summary>Raised when the user requests deletion of the given steps (Sprint 8.3).</summary>
        public event Action<IReadOnlyList<Step>> DeleteStepsRequested;

        /// <summary>
        /// When false, structural editing (create/connect/delete/duplicate) is suppressed — e.g. no
        /// controller is selected, or the app is in play mode. The window sets this on each rebuild.
        /// </summary>
        public bool EditingEnabled { get; set; }

        /// <summary>The minimap overlay, kept so the window can reposition it on resize.</summary>
        public MiniMap MiniMap { get; }

        public SequenceGraphView()
        {
            style.flexGrow = 1;

            // Pan/zoom. ContentZoomer gives scroll-wheel zoom within sensible bounds.
            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());

            var grid = new GridBackground { name = "grid" };
            Insert(0, grid);
            grid.StretchToParentSize();

            MiniMap = new MiniMap { anchored = true };
            MiniMap.SetPosition(new Rect(10, 30, 200, 140));
            Add(MiniMap);

            graphViewChanged = OnGraphViewChanged;
        }

        // Node moves drive layout persistence (8.2). Edge create/remove and node deletion are
        // translated into structural-edit intents (8.3) that the window applies through
        // StepEditingService; we then clear the raw changes and let the resulting HierarchyChanged
        // rebuild redraw the canvas from the actual Steps, so the graph never drifts out of sync.
        private GraphViewChange OnGraphViewChanged(GraphViewChange change)
        {
            if (change.movedElements != null && change.movedElements.Count > 0)
                NodesMoved?.Invoke();

            if (EditingEnabled && change.edgesToCreate != null)
            {
                foreach (var edge in change.edgesToCreate)
                {
                    var parent = (edge.output?.node as StepNode)?.Step;
                    var child = (edge.input?.node as StepNode)?.Step;
                    // Connecting parent.output → child.input reparents child under parent.
                    if (parent != null && child != null) EdgeReparentRequested?.Invoke(parent, child);
                }
            }
            change.edgesToCreate?.Clear();

            if (change.elementsToRemove != null)
            {
                if (EditingEnabled)
                {
                    var stepsToDelete = new List<Step>();
                    foreach (var element in change.elementsToRemove)
                    {
                        switch (element)
                        {
                            case StepNode node when node.Step != null:
                                stepsToDelete.Add(node.Step);
                                break;
                            case Edge edge:
                                var parent = (edge.output?.node as StepNode)?.Step;
                                var child = (edge.input?.node as StepNode)?.Step;
                                // Only a true parent→child (descent) edge maps to a reparent;
                                // sibling-order edges have no reparent meaning, so ignore them
                                // (the rebuild restores them).
                                if (parent != null && child != null && child.Parent == parent)
                                    EdgeDisconnectRequested?.Invoke(child);
                                break;
                        }
                    }
                    if (stepsToDelete.Count > 0) DeleteStepsRequested?.Invoke(stepsToDelete);
                }
                change.elementsToRemove.Clear();
            }

            return change;
        }

        /// <inheritdoc />
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            if (EditingEnabled)
            {
                var targetStep = (evt.target as VisualElement)?.GetFirstAncestorOfType<StepNode>()?.Step;

                // "Add Step ▸ <Type>" — creates under the right-clicked node, or as a root.
                string parentLabel = targetStep != null ? $" (child of {targetStep.name})" : " (root)";
                foreach (var type in GetCreatableStepTypes())
                {
                    var captured = type;
                    evt.menu.AppendAction($"Add Step{parentLabel}/{type.Name}",
                        _ => CreateStepRequested?.Invoke(captured, targetStep));
                }

                var selectedSteps = SelectedSteps();
                if (selectedSteps.Count > 0)
                {
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction($"Duplicate ({selectedSteps.Count})",
                        _ => DuplicateStepsRequested?.Invoke(SelectedSteps()));
                    evt.menu.AppendAction($"Delete ({selectedSteps.Count})",
                        _ => DeleteStepsRequested?.Invoke(SelectedSteps()));
                }
            }

            base.BuildContextualMenu(evt);
        }

        private List<Step> SelectedSteps() =>
            selection.OfType<StepNode>().Select(n => n.Step).Where(s => s != null).ToList();

        // Concrete Step types the user can instantiate, ordered by name. TypeCache is fast and
        // editor-cached; abstract types are excluded.
        private static IEnumerable<Type> GetCreatableStepTypes() =>
            TypeCache.GetTypesDerivedFrom<Step>()
                .Append(typeof(Step))
                .Where(t => !t.IsAbstract)
                .OrderBy(t => t.Name);

        /// <summary>
        /// Rebuilds the entire graph for <paramref name="controller"/>: clears existing nodes/edges,
        /// creates a node per step, lays them out top-down, and connects parent-child edges.
        /// </summary>
        /// <param name="controller">The controller whose steps to display, or <c>null</c> to clear.</param>
        /// <param name="savedPositions">
        /// Optional per-step positions keyed by step; steps present here override auto-layout
        /// (Sprint 8.2 persists these — in 8.1 they only survive within a session).
        /// </param>
        public void Rebuild(SequenceController controller, IReadOnlyDictionary<Step, Vector2> savedPositions = null)
        {
            ClearGraph();
            if (controller == null) return;

            var steps = controller.GetComponentsInChildren<Step>(true).ToList();
            if (steps.Count == 0) return;

            var layout = StepGraphLayoutUtility.ComputeLayeredPositions(steps);

            foreach (var step in steps)
            {
                if (step == null) continue;
                var node = new StepNode(step);
                node.RefreshStatus();
                // Add to the canvas BEFORE positioning: a GraphElement only materializes its
                // position style once parented into contentViewContainer. Positioning an
                // unparented node leaves it at the origin (nodes stack on top of each other).
                AddElement(node);
                Vector2 position = savedPositions != null && savedPositions.TryGetValue(step, out var saved)
                    ? saved
                    : (layout.TryGetValue(step, out var auto) ? auto : Vector2.zero);
                node.SetPosition(new Rect(position, Vector2.zero));
                _nodesByStep[step] = node;
            }

            ConnectEdges(steps);
            FrameContentDeferred();
        }

        // Centres the view on the built content. On first open the window (and this view) can
        // still have zero size while Rebuild runs, so FrameAll would frame an empty region and
        // the graph looks blank until a manual refresh. Frame immediately if the view already
        // has a real layout, otherwise wait for the first valid GeometryChangedEvent.
        private void FrameContentDeferred()
        {
            UnregisterCallback<GeometryChangedEvent>(OnGeometryReadyForFraming);
            if (_nodesByStep.Count == 0) return;

            if (layout.width > 1f && layout.height > 1f)
                schedule.Execute(FrameAllNodes).StartingIn(0); // defer one pass so node bounds resolve
            else
                RegisterCallback<GeometryChangedEvent>(OnGeometryReadyForFraming);
        }

        private void OnGeometryReadyForFraming(GeometryChangedEvent evt)
        {
            if (layout.width <= 1f || layout.height <= 1f) return; // not laid out yet; a later event retries
            UnregisterCallback<GeometryChangedEvent>(OnGeometryReadyForFraming);
            schedule.Execute(FrameAllNodes).StartingIn(0);
        }

        private void FrameAllNodes()
        {
            if (_nodesByStep.Count > 0) FrameAll();
        }

        /// <summary>
        /// Updates the status color of the node for <paramref name="step"/> without a full rebuild
        /// (play-mode status changes route here from the change tracker).
        /// </summary>
        public void RefreshNodeStatus(Step step)
        {
            if (step != null && _nodesByStep.TryGetValue(step, out var node)) node.RefreshStatus();
        }

        /// <summary>
        /// Reflects the model's selection in the GraphView without re-emitting <see cref="StepsSelected"/>
        /// (guards against the selection round-trip). Frames nothing; the window decides when to frame.
        /// </summary>
        /// <param name="selectedSteps">Steps the model considers selected.</param>
        public void SyncSelectionFromModel(IReadOnlyList<Step> selectedSteps)
        {
            _syncingFromModel = true;
            try
            {
                ClearSelection();
                if (selectedSteps != null)
                {
                    foreach (var step in selectedSteps)
                    {
                        if (step != null && _nodesByStep.TryGetValue(step, out var node)) AddToSelection(node);
                    }
                }
            }
            finally
            {
                _syncingFromModel = false;
            }
        }

        /// <summary>Returns the node for <paramref name="step"/>, or <c>null</c> if not present.</summary>
        public StepNode GetNode(Step step) =>
            step != null && _nodesByStep.TryGetValue(step, out var node) ? node : null;

        /// <summary>Toggles the play-mode runtime controls on every node (Sprint 8.5).</summary>
        public void SetPlayMode(bool playing)
        {
            foreach (var node in _nodesByStep.Values) node.SetPlayMode(playing);
        }

        /// <summary>Clears the validation badge on every node (Sprint 8.7).</summary>
        public void ClearAllValidation()
        {
            foreach (var node in _nodesByStep.Values) node.SetValidation(false, false, null, null);
        }

        /// <summary>
        /// Outlines the controller's current step and, when <paramref name="follow"/> is set, pans the
        /// view to centre it (Sprint 8.5). Pass <c>null</c> to clear the highlight.
        /// </summary>
        public void HighlightCurrentStep(Step step, bool follow)
        {
            _currentNode?.SetCurrent(false);
            _currentNode = step != null && _nodesByStep.TryGetValue(step, out var node) ? node : null;
            _currentNode?.SetCurrent(true);

            if (follow && _currentNode != null) FrameNode(_currentNode);
        }

        // Pans (without zooming) so the node sits at the viewport centre. viewTransform is the
        // GraphView's pan/zoom state — its ITransform members are flagged obsolete at the
        // VisualElement level, but they remain the canonical read for GraphView's own transform.
#pragma warning disable 618
        private void FrameNode(StepNode node)
        {
            if (layout.width <= 1f || layout.height <= 1f) return; // not laid out yet
            Vector3 scale = viewTransform.scale;
            Vector2 nodeCenter = node.GetPosition().center;
            Vector2 viewportCenter = new Vector2(layout.width, layout.height) * 0.5f;
            Vector3 newPosition = (Vector3)(viewportCenter - nodeCenter * scale.x);
            newPosition.z = viewTransform.position.z;
            UpdateViewTransform(newPosition, scale);
        }
#pragma warning restore 618

        /// <inheritdoc />
        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            // Parent (output) → child (input) only, and never a node to itself. Cycle prevention
            // beyond self-connection is enforced by StepEditingService.ReparentSteps in 8.3.
            return ports
                .Where(p => p.direction != startPort.direction &&
                            p.node != startPort.node)
                .ToList();
        }

        // Edge tints by relationship kind (Sprint 8.6), drawn from the shared editor design tokens
        // (Sprint 27.4) so edge coloring matches the node category chips and tracks the editor skin.
        private static Color SequentialEdgeColor => MolcaEditorColors.Muted;
        private static Color ParallelEdgeColor => MolcaEditorColors.Primary;
        private static Color BranchEdgeColor => MolcaEditorColors.StatusWarn;

        private void ConnectEdges(IReadOnlyList<Step> steps)
        {
            // Edges mirror execution order (siblings chain; a parent descends to its first child;
            // Parallel fans out concurrently; Branch/Conditional fan out as exclusive branches).
            // Topology + kind come from the GUI-free utility; we only colour them here.
            foreach (var flow in StepGraphLayoutUtility.ComputeFlowEdges(steps))
            {
                if (!_nodesByStep.TryGetValue(flow.From, out var fromNode) ||
                    !_nodesByStep.TryGetValue(flow.To, out var toNode)) continue;

                var edge = fromNode.OutputPort.ConnectTo(toNode.InputPort);
                ApplyEdgeStyle(edge, flow.Kind);
                AddElement(edge);
            }
        }

        private static void ApplyEdgeStyle(Edge edge, StepFlowEdgeKind kind)
        {
            Color color = kind switch
            {
                StepFlowEdgeKind.ParallelFanout => ParallelEdgeColor,
                StepFlowEdgeKind.Branch => BranchEdgeColor,
                _ => SequentialEdgeColor
            };
            edge.edgeControl.inputColor = color;
            edge.edgeControl.outputColor = color;
        }

        private void ClearGraph()
        {
            DeleteElements(graphElements.ToList());
            _nodesByStep.Clear();
            _currentNode = null;
        }

        private void OnSelectionChangedInternal()
        {
            if (_syncingFromModel) return;
            var steps = selection
                .OfType<StepNode>()
                .Select(n => n.Step)
                .Where(s => s != null)
                .ToList();
            StepsSelected?.Invoke(steps);
        }

        // GraphView raises selection changes through AddToSelection/RemoveFromSelection/ClearSelection;
        // override them to funnel into one notification point.
        /// <inheritdoc />
        public override void AddToSelection(ISelectable selectable)
        {
            base.AddToSelection(selectable);
            OnSelectionChangedInternal();
        }

        /// <inheritdoc />
        public override void RemoveFromSelection(ISelectable selectable)
        {
            base.RemoveFromSelection(selectable);
            OnSelectionChangedInternal();
        }

        /// <inheritdoc />
        public override void ClearSelection()
        {
            base.ClearSelection();
            OnSelectionChangedInternal();
        }
    }
}
