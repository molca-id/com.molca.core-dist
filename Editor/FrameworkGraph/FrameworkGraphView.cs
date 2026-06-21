using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.UI;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.FrameworkGraph
{
    /// <summary>
    /// Read-only GraphView canvas for the Molca Framework Graph: one node per
    /// <see cref="FrameworkGraphNode"/>, edges following <see cref="FrameworkGraphEdge"/>s, grouped into
    /// category columns. Pan/zoom/minimap are enabled; node and edge <em>editing</em> is deliberately
    /// not — this surface only visualizes the snapshot. Any mutation belongs to the guarded action tools.
    /// </summary>
    public sealed class FrameworkGraphView : GraphView
    {
        private readonly Dictionary<string, Node> _nodesById = new();

        /// <summary>Raised when the user clicks a node; argument is the backing snapshot node.</summary>
        public event Action<FrameworkGraphNode> NodeClicked;

        /// <summary>Raised when the user drags nodes; the window persists the updated layout (22.9).</summary>
        public event Action NodesMoved;

        /// <summary>The minimap overlay.</summary>
        public MiniMap MiniMap { get; }

        public FrameworkGraphView()
        {
            style.flexGrow = 1;
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

            // Read-only: drop any edge the framework would otherwise let the user create/delete.
            // Node moves are allowed (layout only) and reported so the window persists positions.
            graphViewChanged = change =>
            {
                change.edgesToCreate?.Clear();
                change.elementsToRemove?.Clear();
                if (change.movedElements != null && change.movedElements.Count > 0)
                    NodesMoved?.Invoke();
                return change;
            };
        }

        /// <summary>
        /// Rebuilds the canvas from a snapshot, showing only nodes whose category is in
        /// <paramref name="visibleCategories"/> and (when <paramref name="search"/> is non-empty) whose
        /// label/subtitle/properties contain the term. Edges are drawn only between two visible nodes.
        /// </summary>
        public void Rebuild(FrameworkGraphSnapshot snapshot, ISet<FrameworkNodeCategory> visibleCategories,
            string search, IReadOnlyDictionary<string, Vector2> savedPositions = null)
        {
            DeleteElements(graphElements.ToList());
            _nodesById.Clear();
            if (snapshot == null) return;

            bool Matches(FrameworkGraphNode n)
            {
                if (!visibleCategories.Contains(n.Category)) return false;
                if (string.IsNullOrWhiteSpace(search)) return true;
                var term = search.Trim();
                if (Contains(n.Label, term) || Contains(n.Subtitle, term)) return true;
                return n.Properties.Any(p => Contains(p.Key, term) || Contains(p.Value, term));
            }

            // Lay out visible nodes in per-category columns, stacked top-down.
            var visible = snapshot.Nodes.Where(Matches).ToList();
            var columnOrder = new[]
            {
                FrameworkNodeCategory.Subsystem, FrameworkNodeCategory.Service,
                FrameworkNodeCategory.Sequence, FrameworkNodeCategory.Step,
                FrameworkNodeCategory.Reference, FrameworkNodeCategory.Config,
                FrameworkNodeCategory.Fork,
            };
            const float colWidth = 260f, rowHeight = 130f;

            foreach (var category in columnOrder)
            {
                int col = Array.IndexOf(columnOrder, category);
                int row = 0;
                foreach (var n in visible.Where(n => n.Category == category))
                {
                    var node = CreateNode(n);
                    // Saved layout wins; otherwise auto-place in the category column.
                    var pos = savedPositions != null && savedPositions.TryGetValue(n.Id, out var saved)
                        ? saved
                        : new Vector2(col * colWidth, row * rowHeight);
                    node.SetPosition(new Rect(pos.x, pos.y, 220, 100));
                    AddElement(node);
                    _nodesById[n.Id] = node;
                    row++;
                }
            }

            foreach (var e in snapshot.Edges)
            {
                if (!_nodesById.TryGetValue(e.SourceId, out var source) ||
                    !_nodesById.TryGetValue(e.TargetId, out var target))
                    continue;
                ConnectPorts(source, target, e);
            }
        }

        /// <summary>Current node positions keyed by node id, for layout persistence (22.9).</summary>
        public Dictionary<string, Vector2> GetPositions()
        {
            var result = new Dictionary<string, Vector2>();
            foreach (var kv in _nodesById)
                if (kv.Value != null) result[kv.Key] = kv.Value.GetPosition().position;
            return result;
        }

        private static bool Contains(string haystack, string term)
            => !string.IsNullOrEmpty(haystack) &&
               haystack.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        private Node CreateNode(FrameworkGraphNode model)
        {
            var node = new Node { title = model.Label, userData = model.Id };

            // Two Multi ports so edges have something to attach to; not user-connectable (read-only).
            var input = node.InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
            input.portName = string.Empty;
            input.capabilities &= ~Capabilities.Selectable;
            node.inputContainer.Add(input);

            var output = node.InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
            output.portName = string.Empty;
            output.capabilities &= ~Capabilities.Selectable;
            node.outputContainer.Add(output);

            node.capabilities &= ~Capabilities.Deletable;

            // Title-bar tint by category; left accent by severity.
            node.titleContainer.style.backgroundColor = CategoryColor(model.Category);
            if (model.Severity != FrameworkGraphSeverity.None)
            {
                node.style.borderLeftWidth = 4;
                node.style.borderLeftColor = SeverityColor(model.Severity);
            }

            if (!string.IsNullOrEmpty(model.Subtitle))
            {
                var subtitle = new Label(model.Subtitle);
                subtitle.style.unityFontStyleAndWeight = FontStyle.Italic;
                subtitle.style.opacity = 0.7f;
                subtitle.style.marginLeft = 6;
                subtitle.style.marginRight = 6;
                node.extensionContainer.Add(subtitle);
            }

            // A couple of the most useful properties inline; the full set shows in the details panel.
            foreach (var kv in model.Properties.Take(3))
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                var line = new Label($"{kv.Key}: {kv.Value}");
                line.style.fontSize = 10;
                line.style.marginLeft = 6;
                line.style.marginRight = 6;
                node.extensionContainer.Add(line);
            }
            node.RefreshExpandedState();
            node.RefreshPorts();

            node.RegisterCallback<MouseDownEvent>(_ => NodeClicked?.Invoke(model));
            return node;
        }

        private void ConnectPorts(Node source, Node target, FrameworkGraphEdge model)
        {
            var output = source.outputContainer.Q<Port>();
            var input = target.inputContainer.Q<Port>();
            if (output == null || input == null) return;

            var edge = output.ConnectTo(input);
            edge.capabilities &= ~(Capabilities.Selectable | Capabilities.Deletable);
            if (!string.IsNullOrEmpty(model.Label))
                edge.tooltip = model.Label;
            AddElement(edge);
        }

        // Category column tints are a fixed semantic palette (one hue per node kind), not UI chrome, so
        // they intentionally stay literal rather than mapping to the shared `--molca-*` tokens — the same
        // rationale that exempts the ColorID theming colors (see Sprint 27.3 audit, Tier-4 exceptions).
        private static Color CategoryColor(FrameworkNodeCategory category) => category switch
        {
            FrameworkNodeCategory.Subsystem => new Color(0.20f, 0.35f, 0.55f), // doctor:ignore — fixed semantic palette, see comment above
            FrameworkNodeCategory.Service => new Color(0.20f, 0.45f, 0.40f), // doctor:ignore — fixed semantic palette
            FrameworkNodeCategory.Reference => new Color(0.45f, 0.35f, 0.20f), // doctor:ignore — fixed semantic palette
            FrameworkNodeCategory.Sequence => new Color(0.40f, 0.25f, 0.45f), // doctor:ignore — fixed semantic palette
            FrameworkNodeCategory.Step => new Color(0.32f, 0.30f, 0.48f), // doctor:ignore — fixed semantic palette
            FrameworkNodeCategory.Config => new Color(0.35f, 0.35f, 0.35f), // doctor:ignore — fixed semantic palette
            FrameworkNodeCategory.Fork => new Color(0.45f, 0.20f, 0.30f), // doctor:ignore — fixed semantic palette
            _ => new Color(0.3f, 0.3f, 0.3f), // doctor:ignore — fixed semantic palette
        };

        // Severity left-accent colors from the shared, skin-aware editor palette (Sprint 27.5). The
        // palette reads EditorGUIUtility.isProSkin at call time, so applying these at draw time gives
        // correct dark/light coloring without a CustomStyleResolvedEvent round-trip.
        private static Color SeverityColor(FrameworkGraphSeverity severity) => severity switch
        {
            FrameworkGraphSeverity.Error => MolcaEditorColors.StatusError,
            FrameworkGraphSeverity.Warning => MolcaEditorColors.StatusWarn,
            FrameworkGraphSeverity.Info => MolcaEditorColors.Link,
            _ => Color.clear,
        };
    }
}
