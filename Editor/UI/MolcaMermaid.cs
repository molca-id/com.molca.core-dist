using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine.UIElements;

namespace Molca.Editor.UI
{
    /// <summary>The overall flow direction of a parsed Mermaid flowchart.</summary>
    public enum MermaidDirection
    {
        /// <summary>Top-to-bottom (<c>TD</c>/<c>TB</c>); the default.</summary>
        TopDown,
        /// <summary>Bottom-to-top (<c>BT</c>).</summary>
        BottomUp,
        /// <summary>Left-to-right (<c>LR</c>).</summary>
        LeftRight,
        /// <summary>Right-to-left (<c>RL</c>).</summary>
        RightLeft
    }

    /// <summary>The rendered outline of a flowchart node, derived from its Mermaid bracket syntax.</summary>
    public enum MermaidNodeShape
    {
        /// <summary><c>id[text]</c> — a plain rectangle (also the fallback for unsupported wrappers).</summary>
        Rectangle,
        /// <summary><c>id(text)</c> / <c>id([text])</c> — a rounded/stadium box.</summary>
        Rounded,
        /// <summary><c>id((text))</c> — a circle/ellipse.</summary>
        Circle,
        /// <summary><c>id{text}</c> — a decision rhombus.</summary>
        Diamond
    }

    /// <summary>One node in a parsed Mermaid flowchart. Layout fields are populated by <see cref="MolcaMermaidView"/>.</summary>
    public sealed class MermaidNode
    {
        /// <summary>The node's identifier (unique within the graph); used to resolve edge endpoints.</summary>
        public string Id { get; }

        /// <summary>The visible label; defaults to <see cref="Id"/> when no bracketed text was given.</summary>
        public string Label { get; internal set; }

        /// <summary>The node outline shape.</summary>
        public MermaidNodeShape Shape { get; internal set; }

        /// <summary>Layout: the assigned layer (rank) index, 0-based from the graph roots.</summary>
        public int Layer { get; internal set; }

        /// <summary>Layout: the local X position (px) of the node's top-left within the view.</summary>
        public float X { get; internal set; }

        /// <summary>Layout: the local Y position (px) of the node's top-left within the view.</summary>
        public float Y { get; internal set; }

        /// <summary>Layout: the node's rendered width (px).</summary>
        public float Width { get; internal set; }

        /// <summary>Layout: the node's rendered height (px).</summary>
        public float Height { get; internal set; }

        /// <summary>Creates a node with a default (identity) label and rectangle shape.</summary>
        internal MermaidNode(string id)
        {
            Id = id;
            Label = id;
            Shape = MermaidNodeShape.Rectangle;
        }
    }

    /// <summary>One directed link between two nodes in a parsed Mermaid flowchart.</summary>
    public sealed class MermaidEdge
    {
        /// <summary>The source node id.</summary>
        public string From { get; }

        /// <summary>The target node id.</summary>
        public string To { get; }

        /// <summary>The optional edge label (<c>--&gt;|text|</c> or <c>-- text --&gt;</c>); <c>null</c> when none.</summary>
        public string Label { get; }

        /// <summary>Whether the link is dashed (<c>-.-&gt;</c>).</summary>
        public bool Dashed { get; }

        /// <summary>Whether the link carries an arrowhead (<c>--&gt;</c>) versus an open line (<c>---</c>).</summary>
        public bool HasArrow { get; }

        /// <summary>Creates an edge.</summary>
        internal MermaidEdge(string from, string to, string label, bool dashed, bool hasArrow)
        {
            From = from;
            To = to;
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
            Dashed = dashed;
            HasArrow = hasArrow;
        }
    }

    /// <summary>A parsed Mermaid flowchart: its direction plus ordered nodes and edges.</summary>
    public sealed class MermaidGraph
    {
        /// <summary>The overall flow direction.</summary>
        public MermaidDirection Direction { get; }

        /// <summary>The nodes, in first-appearance order.</summary>
        public IReadOnlyList<MermaidNode> Nodes { get; }

        /// <summary>The edges, in source order.</summary>
        public IReadOnlyList<MermaidEdge> Edges { get; }

        /// <summary>Creates a graph from its parsed parts.</summary>
        internal MermaidGraph(MermaidDirection direction, IReadOnlyList<MermaidNode> nodes, IReadOnlyList<MermaidEdge> edges)
        {
            Direction = direction;
            Nodes = nodes;
            Edges = edges;
        }
    }

    /// <summary>
    /// A small, dependency-free renderer for the <c>mermaid</c> fenced blocks that <see cref="MolcaMarkdown"/>
    /// surfaces. It natively draws the common <b>flowchart</b> subset (<c>graph</c>/<c>flowchart</c> with
    /// <c>TD</c>/<c>LR</c>/… directions, bracket node shapes, and labeled/dashed links) into a UI Toolkit
    /// element; any other diagram kind (or a graph too large/malformed to lay out) degrades to a labeled
    /// source block with a copy affordance.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/</c> (shared editor design layer; forks inherit it).
    /// Because Mermaid is a JavaScript library and there is no browser/SVG engine in the editor, this is a
    /// native C# reimplementation of a syntax subset — not a wrapper. Styling requires
    /// <see cref="MolcaEditorUi.Apply"/> on an ancestor root (the <c>molca-md-diagram*</c> rules live in the
    /// shared components stylesheet). Not thread-safe; main thread only (builds UI Toolkit elements).
    /// </remarks>
    public static class MolcaMermaid
    {
        // Guardrails: beyond these a native layout stops being readable, so we fall back to the source block.
        private const int MaxNodes = 120;
        private const int MaxEdges = 300;

        // A statement line that configures styling/behavior rather than declaring nodes/edges; skipped.
        private static readonly HashSet<string> DirectiveKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "subgraph", "end", "direction", "classDef", "class", "style", "linkStyle", "click"
        };

        // Header: `graph`/`flowchart` optionally followed by a direction token.
        private static readonly Regex HeaderRegex = new Regex(
            @"^(?:graph|flowchart)\b\s*(?<dir>TB|TD|BT|LR|RL)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // A node reference: an id and an optional bracketed shape+label. Compound wrappers are listed before
        // their single-bracket prefixes so the longest form wins.
        private static readonly Regex NodeRefRegex = new Regex(
            @"(?<id>[A-Za-z0-9_]+)(?<shape>\[\[.*?\]\]|\[\(.*?\)\]|\(\(.*?\)\)|\(\[.*?\]\)|\{\{.*?\}\}|>.*?\]|\[.*?\]|\(.*?\)|\{.*?\})?",
            RegexOptions.Compiled);

        // A link connector between two node refs. Middle-label forms (`-- text -->`) precede the plain forms
        // so the leading dashes aren't mis-read as an open line; an optional `|label|` follows either.
        private static readonly Regex LinkRegex = new Regex(
            @"(?:" +
            @"--\s*(?<mlabel>.+?)\s*-{2,}>" + "|" +   // -- label -->
            @"==\s*(?<mlabel>.+?)\s*={2,}>" + "|" +   // == label ==>
            @"-\.\s*(?<mlabel>.+?)\s*\.-+>" + "|" +   // -. label .->
            @"-\.-+>" + "|" +                          // -.->
            @"-\.-+"  + "|" +                          // -.-
            @"={2,}>" + "|" +                          // ==>
            @"={2,}"  + "|" +                          // ===
            @"-{2,}>" + "|" +                          // -->
            @"-{2,}"  +                                // ---
            @")(?:\s*\|(?<plabel>[^|]*)\|)?",
            RegexOptions.Compiled);

        /// <summary>True when a fence info string names a Mermaid diagram block.</summary>
        public static bool IsMermaidLanguage(string infoString)
            => string.Equals(infoString?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Builds a <see cref="VisualElement"/> for a Mermaid <paramref name="source"/>. Returns a native
        /// flowchart view when the source parses as a supported flowchart, otherwise a labeled fallback that
        /// shows the source with a Copy button.
        /// </summary>
        /// <param name="source">The raw Mermaid diagram text (the fenced block body).</param>
        /// <returns>A container element ready to add to a UI Toolkit hierarchy.</returns>
        public static VisualElement Create(string source)
        {
            var graph = ParseFlowchart(source);
            if (graph == null || graph.Nodes.Count == 0)
                return CreateFallback(source);

            var frame = new VisualElement();
            frame.AddToClassList("molca-md-diagram");
            frame.Add(new MolcaMermaidView(graph));
            return frame;
        }

        /// <summary>
        /// Parses <paramref name="source"/> as a Mermaid flowchart into a <see cref="MermaidGraph"/>. Returns
        /// <c>null</c> when the source is not a <c>graph</c>/<c>flowchart</c>, declares no nodes, or exceeds the
        /// size guardrails — the caller then renders the source-block fallback.
        /// </summary>
        /// <param name="source">The raw Mermaid diagram text.</param>
        /// <returns>The parsed graph, or <c>null</c> when it is not a renderable flowchart.</returns>
        public static MermaidGraph ParseFlowchart(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;

            // Statements are newline- or semicolon-separated; drop blanks and `%%` comments.
            var statements = source
                .Replace("\r\n", "\n").Replace('\r', '\n')
                .Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0 && !s.StartsWith("%%", StringComparison.Ordinal))
                .ToList();
            if (statements.Count == 0) return null;

            var header = HeaderRegex.Match(statements[0]);
            if (!header.Success) return null; // not a flowchart (e.g. sequenceDiagram) → fallback

            var direction = ParseDirection(header.Groups["dir"].Value);

            var nodes = new Dictionary<string, MermaidNode>(StringComparer.Ordinal);
            var order = new List<MermaidNode>();
            var edges = new List<MermaidEdge>();

            for (var i = 1; i < statements.Count; i++)
                ParseStatement(statements[i], nodes, order, edges);

            if (order.Count == 0 || order.Count > MaxNodes || edges.Count > MaxEdges) return null;
            return new MermaidGraph(direction, order, edges);
        }

        private static MermaidDirection ParseDirection(string token) => token?.ToUpperInvariant() switch
        {
            "BT" => MermaidDirection.BottomUp,
            "LR" => MermaidDirection.LeftRight,
            "RL" => MermaidDirection.RightLeft,
            _ => MermaidDirection.TopDown
        };

        /// <summary>
        /// Parses one flowchart statement into node/edge declarations. Reads an alternating chain of node refs
        /// and link connectors (<c>A --&gt; B --&gt; C</c>); a bare node ref declares/updates a node. Unparseable
        /// remainder is ignored so a single odd line never fails the whole diagram.
        /// </summary>
        private static void ParseStatement(string line, IDictionary<string, MermaidNode> nodes,
            ICollection<MermaidNode> order, ICollection<MermaidEdge> edges)
        {
            var firstToken = FirstWord(line);
            if (DirectiveKeywords.Contains(firstToken)) return;

            var pos = 0;
            var prev = ReadNode(line, ref pos, nodes, order);
            if (prev == null) return;

            while (true)
            {
                SkipWhitespace(line, ref pos);
                if (pos >= line.Length) break;

                var link = LinkRegex.Match(line, pos);
                if (!link.Success || link.Index != pos) break;
                pos = link.Index + link.Length;

                var next = ReadNode(line, ref pos, nodes, order);
                if (next == null) break;

                ClassifyLink(link, out var dashed, out var hasArrow, out var label);
                edges.Add(new MermaidEdge(prev.Id, next.Id, label, dashed, hasArrow));
                prev = next;
            }
        }

        /// <summary>Reads a node ref at <paramref name="pos"/>, registering/updating it; returns <c>null</c> on no match.</summary>
        private static MermaidNode ReadNode(string line, ref int pos, IDictionary<string, MermaidNode> nodes,
            ICollection<MermaidNode> order)
        {
            SkipWhitespace(line, ref pos);
            var match = NodeRefRegex.Match(line, pos);
            if (!match.Success || match.Index != pos) return null;
            pos = match.Index + match.Length;

            var id = match.Groups["id"].Value;
            if (!nodes.TryGetValue(id, out var node))
            {
                node = new MermaidNode(id);
                nodes[id] = node;
                order.Add(node);
            }

            var shape = match.Groups["shape"];
            if (shape.Success && shape.Value.Length > 0)
            {
                node.Shape = ClassifyShape(shape.Value, out var label);
                if (!string.IsNullOrWhiteSpace(label)) node.Label = label;
            }
            return node;
        }

        /// <summary>Maps a bracket wrapper to a shape and extracts its inner (unquoted, single-line) label.</summary>
        private static MermaidNodeShape ClassifyShape(string wrapper, out string label)
        {
            MermaidNodeShape shape;
            string inner;
            if (wrapper.StartsWith("((", StringComparison.Ordinal)) { shape = MermaidNodeShape.Circle; inner = Trim(wrapper, 2); }
            else if (wrapper.StartsWith("([", StringComparison.Ordinal)) { shape = MermaidNodeShape.Rounded; inner = Trim(wrapper, 2); }
            else if (wrapper.StartsWith("[(", StringComparison.Ordinal)) { shape = MermaidNodeShape.Rectangle; inner = Trim(wrapper, 2); }
            else if (wrapper.StartsWith("[[", StringComparison.Ordinal)) { shape = MermaidNodeShape.Rectangle; inner = Trim(wrapper, 2); }
            else if (wrapper.StartsWith("{{", StringComparison.Ordinal)) { shape = MermaidNodeShape.Rectangle; inner = Trim(wrapper, 2); }
            else if (wrapper.StartsWith("{", StringComparison.Ordinal)) { shape = MermaidNodeShape.Diamond; inner = Trim(wrapper, 1); }
            else if (wrapper.StartsWith("(", StringComparison.Ordinal)) { shape = MermaidNodeShape.Rounded; inner = Trim(wrapper, 1); }
            else if (wrapper.StartsWith(">", StringComparison.Ordinal)) { shape = MermaidNodeShape.Rectangle; inner = wrapper.Substring(1, wrapper.Length - 2); }
            else { shape = MermaidNodeShape.Rectangle; inner = Trim(wrapper, 1); }

            label = CleanLabel(inner);
            return shape;
        }

        /// <summary>Strips <paramref name="pad"/> wrapper chars from each end of a bracketed body.</summary>
        private static string Trim(string wrapper, int pad)
            => wrapper.Length >= pad * 2 ? wrapper.Substring(pad, wrapper.Length - pad * 2) : string.Empty;

        /// <summary>Normalizes a node/edge label: unwrap quotes, collapse <c>&lt;br&gt;</c> to spaces, trim.</summary>
        private static string CleanLabel(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var s = Regex.Replace(text, @"<br\s*/?>", " ", RegexOptions.IgnoreCase).Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
            return s.Trim();
        }

        /// <summary>Derives dashed/arrow flags and any label from a matched link connector.</summary>
        private static void ClassifyLink(Match link, out bool dashed, out bool hasArrow, out string label)
        {
            var value = link.Value;
            var pipe = value.IndexOf('|');
            var core = (pipe >= 0 ? value.Substring(0, pipe) : value).Trim();

            dashed = core.IndexOf('.') >= 0;
            hasArrow = core.EndsWith(">", StringComparison.Ordinal);

            var mlabel = link.Groups["mlabel"];
            var plabel = link.Groups["plabel"];
            label = mlabel.Success ? mlabel.Value : (plabel.Success ? plabel.Value : null);
        }

        private static string FirstWord(string line)
        {
            var end = 0;
            while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
            return line.Substring(0, end);
        }

        private static void SkipWhitespace(string line, ref int pos)
        {
            while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
        }

        // ---- Fallback ------------------------------------------------------------------------------

        /// <summary>
        /// Builds the labeled source-block fallback used when a Mermaid source is not a renderable flowchart.
        /// It shows the diagram kind, the raw source, and a Copy button (paste into a full Mermaid renderer).
        /// </summary>
        private static VisualElement CreateFallback(string source)
        {
            var container = new VisualElement();
            container.AddToClassList("molca-md-diagram-fallback");

            var header = new VisualElement();
            header.AddToClassList("molca-md-diagram-fallback__header");

            var caption = new Label("◈ mermaid diagram");
            caption.AddToClassList("molca-md-diagram-fallback__caption");
            header.Add(caption);

            var copy = new Button(() => EditorGUIUtility.systemCopyBuffer = source ?? string.Empty) { text = "Copy" };
            copy.AddToClassList("molca-md-diagram-fallback__copy");
            header.Add(copy);
            container.Add(header);

            var body = new Label(source ?? string.Empty)
            {
                selection = { isSelectable = true }
            };
            body.AddToClassList("molca-md-code-block");
            body.style.whiteSpace = WhiteSpace.Pre;
            container.Add(body);

            return container;
        }
    }
}
