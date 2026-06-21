using System.Collections.Generic;

namespace Molca.Editor.FrameworkGraph
{
    /// <summary>
    /// Broad classification of a <see cref="FrameworkGraphNode"/>, driving its visual grouping in the
    /// editor window and its section in the MCP snapshot. Categories mirror the framework's own
    /// subsystems so the map reads the same way the architecture docs describe the layer model.
    /// </summary>
    public enum FrameworkNodeCategory
    {
        /// <summary>A <c>RuntimeSubsystem</c> discovered on the RuntimeManager (Play mode).</summary>
        Subsystem,
        /// <summary>A DI-container service/factory/binding registration (Play mode).</summary>
        Service,
        /// <summary>A scene <c>ReferenceableComponent</c> Ref Id, or an unresolved reference target.</summary>
        Reference,
        /// <summary>A <c>SequenceController</c> root in a loaded scene.</summary>
        Sequence,
        /// <summary>A <c>Step</c> inside a sequence.</summary>
        Step,
        /// <summary>Authored ScriptableObject configuration (read-only, never a runtime resolve target).</summary>
        Config,
        /// <summary>A node contributed by an SDK fork through the read-only graph provider contract.</summary>
        Fork,
    }

    /// <summary>
    /// The relationship a <see cref="FrameworkGraphEdge"/> represents. Edge direction is
    /// source → target (dependant → dependency for <see cref="DependsOn"/>, predecessor → successor
    /// for <see cref="InitOrder"/> and <see cref="StepFlow"/>).
    /// </summary>
    public enum FrameworkEdgeKind
    {
        /// <summary><c>[DependsOn]</c> declaration between two subsystems.</summary>
        DependsOn,
        /// <summary>Consecutive entries in the resolved subsystem initialization order.</summary>
        InitOrder,
        /// <summary>A service registration whose implementation is itself a graph node (e.g. a subsystem).</summary>
        ServiceBinding,
        /// <summary>A <c>SceneObjectReference</c> from one referenceable to another Ref Id.</summary>
        SceneReference,
        /// <summary>Parent → child execution flow between steps (matches the sequence graph editor).</summary>
        StepFlow,
        /// <summary>Structural containment (controller → step, step → auxiliary), not execution flow.</summary>
        Contains,
    }

    /// <summary>
    /// Severity surfaced as a node badge. Aligned with Doctor/validator severities so the framework
    /// graph can colour nodes from existing findings without inventing a parallel scale.
    /// </summary>
    public enum FrameworkGraphSeverity
    {
        /// <summary>No finding attached.</summary>
        None = 0,
        /// <summary>Informational.</summary>
        Info = 1,
        /// <summary>Likely problem (heuristic).</summary>
        Warning = 2,
        /// <summary>Definite problem (e.g. duplicate/empty/unresolved Ref Id).</summary>
        Error = 3,
    }

    /// <summary>
    /// A single node in the framework graph. Plain, GUI-free data so the same instance serves both the
    /// <c>GraphView</c> window and the read-only <c>molca_framework_graph</c> MCP export.
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/> is stable across rebuilds (category-prefixed, derived from a durable key such as
    /// a Ref Id or full type name) so editor-only layout positions can be persisted against it
    /// (see Sprint 22.9). Never carries runtime Unity object references — only describes them.
    /// </remarks>
    public sealed class FrameworkGraphNode
    {
        /// <summary>Stable, category-prefixed identifier (e.g. <c>subsystem:Molca.Foo</c>, <c>ref:main-valve</c>).</summary>
        public string Id;
        /// <summary>Short display label (typically a type name or Ref Id).</summary>
        public string Label;
        /// <summary>Optional secondary line (e.g. full type name, GameObject name).</summary>
        public string Subtitle;
        /// <summary>Node classification.</summary>
        public FrameworkNodeCategory Category;
        /// <summary>Highest-severity finding attached to this node, if any.</summary>
        public FrameworkGraphSeverity Severity;
        /// <summary>True when the node only exists in Play mode (live runtime data).</summary>
        public bool RuntimeOnly;
        /// <summary>Free-form, display-only key/value detail shown in the selection panel and JSON export.</summary>
        public Dictionary<string, string> Properties = new();

        public FrameworkGraphNode() { }

        public FrameworkGraphNode(string id, string label, FrameworkNodeCategory category)
        {
            Id = id;
            Label = label;
            Category = category;
        }

        /// <summary>Sets a display property and returns this node for fluent construction.</summary>
        public FrameworkGraphNode With(string key, string value)
        {
            if (!string.IsNullOrEmpty(key)) Properties[key] = value;
            return this;
        }
    }

    /// <summary>A directed relationship between two <see cref="FrameworkGraphNode"/>s.</summary>
    public sealed class FrameworkGraphEdge
    {
        /// <summary>Stable identifier, derived from kind + endpoints.</summary>
        public string Id;
        /// <summary>Source node id (dependant / predecessor / owner).</summary>
        public string SourceId;
        /// <summary>Target node id (dependency / successor / referenced).</summary>
        public string TargetId;
        /// <summary>The relationship this edge represents.</summary>
        public FrameworkEdgeKind Kind;
        /// <summary>Optional edge label.</summary>
        public string Label;

        public FrameworkGraphEdge() { }

        public FrameworkGraphEdge(string sourceId, string targetId, FrameworkEdgeKind kind, string label = null)
        {
            SourceId = sourceId;
            TargetId = targetId;
            Kind = kind;
            Label = label;
            Id = $"{kind}:{sourceId}->{targetId}";
        }
    }

    /// <summary>
    /// An immutable-by-convention snapshot of the loaded project's framework topology. Produced by
    /// <see cref="FrameworkGraphBuilder"/>; consumed read-only by the editor window and MCP tool.
    /// </summary>
    /// <remarks>
    /// Layers that require Play mode (subsystems, services) record a human-readable entry in
    /// <see cref="UnavailableReasons"/> when built in Edit mode, instead of silently emitting nothing —
    /// so both front-ends can show "requires Play mode" rather than an empty canvas.
    /// </remarks>
    public sealed class FrameworkGraphSnapshot
    {
        /// <summary>Whether the snapshot was taken in Play mode (runtime layers populated).</summary>
        public bool IsPlayMode;
        /// <summary>All nodes, keyed for lookup by <see cref="Id"/>.</summary>
        public List<FrameworkGraphNode> Nodes = new();
        /// <summary>All edges (only added when both endpoints exist as nodes).</summary>
        public List<FrameworkGraphEdge> Edges = new();
        /// <summary>Per-layer reasons a section is unavailable (e.g. "Subsystems: requires Play mode").</summary>
        public List<string> UnavailableReasons = new();

        private readonly Dictionary<string, FrameworkGraphNode> _byId = new();

        /// <summary>Adds a node, de-duplicating by id (later additions raise severity if higher).</summary>
        public FrameworkGraphNode AddNode(FrameworkGraphNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.Id)) return node;
            if (_byId.TryGetValue(node.Id, out var existing))
            {
                if (node.Severity > existing.Severity) existing.Severity = node.Severity;
                return existing;
            }
            _byId[node.Id] = node;
            Nodes.Add(node);
            return node;
        }

        /// <summary>Returns the node with the given id, or <c>null</c>.</summary>
        public FrameworkGraphNode FindNode(string id)
            => id != null && _byId.TryGetValue(id, out var n) ? n : null;

        /// <summary>True if a node with the given id exists.</summary>
        public bool HasNode(string id) => id != null && _byId.ContainsKey(id);

        /// <summary>
        /// Adds an edge only when both endpoints already exist as nodes — keeps the graph free of
        /// dangling edges regardless of which layers are enabled.
        /// </summary>
        public FrameworkGraphEdge AddEdge(FrameworkGraphEdge edge)
        {
            if (edge == null || !HasNode(edge.SourceId) || !HasNode(edge.TargetId)) return null;
            if (edge.SourceId == edge.TargetId) return null;
            Edges.Add(edge);
            return edge;
        }

        /// <summary>Records that a layer could not be built, with a caller-supplied reason.</summary>
        public void AddUnavailable(string reason)
        {
            if (!string.IsNullOrEmpty(reason)) UnavailableReasons.Add(reason);
        }
    }
}
