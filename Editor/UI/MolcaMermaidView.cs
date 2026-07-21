using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.UI
{
    /// <summary>
    /// A UI Toolkit element that lays out and draws a parsed <see cref="MermaidGraph"/> flowchart: nodes are
    /// placed by a simple layered (rank) layout, their shapes and the connecting edges are drawn with the
    /// vector <see cref="Painter2D"/> API, and labels ride on top as child <see cref="Label"/>s.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/</c>. Colors come from <see cref="MolcaEditorColors"/>
    /// (which follows the active editor skin) since <see cref="Painter2D"/> cannot resolve USS
    /// <c>var(--molca-*)</c> tokens. The view sizes itself to the laid-out graph so it flows in a document.
    /// Not thread-safe; main thread only.
    /// </remarks>
    public sealed class MolcaMermaidView : VisualElement
    {
        private const float LayerGap = 46f;   // spacing between successive ranks (along the flow axis)
        private const float NodeGap = 26f;    // spacing between nodes within a rank (across the flow axis)
        private const float Pad = 10f;        // margin around the whole graph inside the view
        private const float NodeHeight = 30f;
        private const float CharWidth = 7.2f; // approximate advance at the ~12px label font
        private const float NodePadX = 14f;
        private const float MinNodeWidth = 46f;
        private const float ArrowLen = 9f;
        private const float ArrowHalfWidth = 4.5f;

        private readonly MermaidGraph _graph;
        private readonly Dictionary<string, MermaidNode> _nodesById;

        /// <summary>Builds and lays out a view for <paramref name="graph"/>.</summary>
        /// <param name="graph">The parsed flowchart to render; must be non-null with at least one node.</param>
        public MolcaMermaidView(MermaidGraph graph)
        {
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _nodesById = new Dictionary<string, MermaidNode>(StringComparer.Ordinal);
            foreach (var n in _graph.Nodes) _nodesById[n.Id] = n;
            AddToClassList("molca-md-diagram__canvas");

            Layout(out var width, out var height);
            style.width = width;
            style.height = height;

            foreach (var node in _graph.Nodes) Add(CreateNodeLabel(node));
            foreach (var edge in _graph.Edges) AddEdgeLabel(edge);

            generateVisualContent += OnGenerateVisualContent;
        }

        // ---- Layout ---------------------------------------------------------------------------------

        /// <summary>
        /// Assigns each node a layer (longest-path from the roots, bounded so cycles can't loop forever), sizes
        /// nodes from their labels, then places them rank-by-rank and centers each rank across the flow axis.
        /// </summary>
        private void Layout(out float width, out float height)
        {
            var horizontal = _graph.Direction == MermaidDirection.LeftRight || _graph.Direction == MermaidDirection.RightLeft;
            var reverseMain = _graph.Direction == MermaidDirection.BottomUp || _graph.Direction == MermaidDirection.RightLeft;

            AssignLayers();
            foreach (var node in _graph.Nodes) SizeNode(node);

            // Bucket nodes by layer in first-appearance order.
            var byLayer = new Dictionary<int, List<MermaidNode>>();
            var layerCount = 0;
            foreach (var node in _graph.Nodes)
            {
                if (!byLayer.TryGetValue(node.Layer, out var list))
                    byLayer[node.Layer] = list = new List<MermaidNode>();
                list.Add(node);
                layerCount = Mathf.Max(layerCount, node.Layer + 1);
            }

            // Main axis = flow direction; cross axis = within-rank spread. `main`/`cross` sizes swap per orientation.
            float MainSize(MermaidNode n) => horizontal ? n.Width : n.Height;
            float CrossSize(MermaidNode n) => horizontal ? n.Height : n.Width;

            // Per-layer thickness (max main size) and the running main position of each layer band.
            var mainThickness = new float[layerCount];
            for (var l = 0; l < layerCount; l++)
                foreach (var n in byLayer[l])
                    mainThickness[l] = Mathf.Max(mainThickness[l], MainSize(n));

            var mainPos = new float[layerCount];
            var mainTotal = 0f;
            for (var l = 0; l < layerCount; l++)
            {
                mainPos[l] = mainTotal;
                mainTotal += mainThickness[l] + (l < layerCount - 1 ? LayerGap : 0f);
            }

            // Lay each rank across the cross axis, then find the widest rank to center the rest against.
            var crossExtent = new float[layerCount];
            for (var l = 0; l < layerCount; l++)
            {
                var c = 0f;
                foreach (var n in byLayer[l]) c += CrossSize(n) + NodeGap;
                crossExtent[l] = Mathf.Max(0f, c - NodeGap);
            }
            var crossTotal = 0f;
            for (var l = 0; l < layerCount; l++) crossTotal = Mathf.Max(crossTotal, crossExtent[l]);

            for (var l = 0; l < layerCount; l++)
            {
                var c = (crossTotal - crossExtent[l]) * 0.5f;
                var bandMain = reverseMain ? mainTotal - mainPos[l] - mainThickness[l] : mainPos[l];
                foreach (var n in byLayer[l])
                {
                    var mainCoord = bandMain + (mainThickness[l] - MainSize(n)) * 0.5f;
                    if (horizontal) { n.X = Pad + mainCoord; n.Y = Pad + c; }
                    else { n.X = Pad + c; n.Y = Pad + mainCoord; }
                    c += CrossSize(n) + NodeGap;
                }
            }

            width = (horizontal ? mainTotal : crossTotal) + Pad * 2f;
            height = (horizontal ? crossTotal : mainTotal) + Pad * 2f;
        }

        /// <summary>
        /// Longest-path layer assignment: relax <c>layer[v] = max(layer[v], layer[u]+1)</c> over every edge,
        /// bounded by the node count so a cycle terminates instead of looping. Self-edges are ignored.
        /// </summary>
        private void AssignLayers()
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < _graph.Nodes.Count; i++) index[_graph.Nodes[i].Id] = i;

            var iterations = _graph.Nodes.Count;
            for (var pass = 0; pass < iterations; pass++)
            {
                var changed = false;
                foreach (var edge in _graph.Edges)
                {
                    if (edge.From == edge.To) continue;
                    if (!index.TryGetValue(edge.From, out var u) || !index.TryGetValue(edge.To, out var v)) continue;
                    var from = _graph.Nodes[u];
                    var to = _graph.Nodes[v];
                    if (to.Layer < from.Layer + 1) { to.Layer = from.Layer + 1; changed = true; }
                }
                if (!changed) break;
            }
        }

        private static void SizeNode(MermaidNode node)
        {
            var text = node.Label ?? node.Id;
            var width = Mathf.Max(MinNodeWidth, text.Length * CharWidth + NodePadX * 2f);
            var height = NodeHeight;

            switch (node.Shape)
            {
                case MermaidNodeShape.Circle:
                    var d = Mathf.Max(width, height + 14f);
                    width = height = d;
                    break;
                case MermaidNodeShape.Diamond:
                    width *= 1.35f;   // a rhombus needs extra room for the label to clear the slanted sides
                    height += 12f;
                    break;
            }

            node.Width = width;
            node.Height = height;
        }

        // ---- Labels (children, drawn over the vector layer) -----------------------------------------

        private Label CreateNodeLabel(MermaidNode node)
        {
            var label = new Label(node.Label ?? node.Id);
            label.AddToClassList("molca-md-diagram__node-label");
            label.style.position = Position.Absolute;
            label.style.left = node.X;
            label.style.top = node.Y;
            label.style.width = node.Width;
            label.style.height = node.Height;
            return label;
        }

        private void AddEdgeLabel(MermaidEdge edge)
        {
            if (string.IsNullOrEmpty(edge.Label)) return;
            if (!TryNode(edge.From, out var a) || !TryNode(edge.To, out var b)) return;

            var mid = (Center(a) + Center(b)) * 0.5f;
            var approxWidth = edge.Label.Length * CharWidth + 8f;

            var label = new Label(edge.Label);
            label.AddToClassList("molca-md-diagram__edge-label");
            label.style.position = Position.Absolute;
            label.style.left = mid.x - approxWidth * 0.5f;
            label.style.top = mid.y - 9f;
            Add(label);
        }

        // ---- Vector drawing -------------------------------------------------------------------------

        private void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            p.lineJoin = LineJoin.Round;
            p.lineCap = LineCap.Round;

            DrawEdges(p);
            DrawNodes(p);
        }

        private void DrawEdges(Painter2D p)
        {
            var edgeColor = MolcaEditorColors.Label;
            foreach (var edge in _graph.Edges)
            {
                if (!TryNode(edge.From, out var a) || !TryNode(edge.To, out var b)) continue;

                var start = BorderPoint(a, Center(b));
                var end = BorderPoint(b, Center(a));
                var dir = (end - start).normalized;
                if (dir == Vector2.zero) continue;

                // Stop the line short of the tip so the (filled) arrowhead isn't overdrawn by the stroke.
                var lineEnd = edge.HasArrow ? end - dir * (ArrowLen * 0.8f) : end;

                p.strokeColor = edgeColor;
                p.lineWidth = 1.5f;
                if (edge.Dashed) DrawDashedLine(p, start, lineEnd);
                else { p.BeginPath(); p.MoveTo(start); p.LineTo(lineEnd); p.Stroke(); }

                if (edge.HasArrow) DrawArrowHead(p, end, dir, edgeColor);
            }
        }

        private void DrawNodes(Painter2D p)
        {
            var fill = MolcaEditorColors.CardHeader;
            var stroke = MolcaEditorColors.Border;
            var decision = MolcaEditorColors.Primary;

            foreach (var node in _graph.Nodes)
            {
                var rect = new Rect(node.X, node.Y, node.Width, node.Height);
                p.fillColor = fill;
                p.strokeColor = node.Shape == MermaidNodeShape.Diamond ? decision : stroke;
                p.lineWidth = 1.5f;

                p.BeginPath();
                switch (node.Shape)
                {
                    case MermaidNodeShape.Rounded: BuildRoundedRect(p, rect, rect.height * 0.5f); break;
                    case MermaidNodeShape.Circle: BuildEllipse(p, rect.center, rect.width * 0.5f, rect.height * 0.5f); break;
                    case MermaidNodeShape.Diamond: BuildDiamond(p, rect); break;
                    default: BuildRoundedRect(p, rect, 4f); break;
                }
                p.Fill();
                p.Stroke();
            }
        }

        private static void DrawArrowHead(Painter2D p, Vector2 tip, Vector2 dir, Color color)
        {
            var perp = new Vector2(-dir.y, dir.x);
            var baseCenter = tip - dir * ArrowLen;
            p.fillColor = color;
            p.BeginPath();
            p.MoveTo(tip);
            p.LineTo(baseCenter + perp * ArrowHalfWidth);
            p.LineTo(baseCenter - perp * ArrowHalfWidth);
            p.ClosePath();
            p.Fill();
        }

        private static void DrawDashedLine(Painter2D p, Vector2 a, Vector2 b, float dash = 6f, float gap = 4f)
        {
            var total = Vector2.Distance(a, b);
            if (total <= 0f) return;
            var dir = (b - a) / total;
            for (var t = 0f; t < total; t += dash + gap)
            {
                var s = a + dir * t;
                var e = a + dir * Mathf.Min(t + dash, total);
                p.BeginPath();
                p.MoveTo(s);
                p.LineTo(e);
                p.Stroke();
            }
        }

        private static void BuildRoundedRect(Painter2D p, Rect r, float radius)
        {
            radius = Mathf.Min(radius, r.width * 0.5f, r.height * 0.5f);
            float x = r.x, y = r.y, w = r.width, h = r.height;
            p.MoveTo(new Vector2(x + radius, y));
            p.LineTo(new Vector2(x + w - radius, y));
            p.QuadraticCurveTo(new Vector2(x + w, y), new Vector2(x + w, y + radius));
            p.LineTo(new Vector2(x + w, y + h - radius));
            p.QuadraticCurveTo(new Vector2(x + w, y + h), new Vector2(x + w - radius, y + h));
            p.LineTo(new Vector2(x + radius, y + h));
            p.QuadraticCurveTo(new Vector2(x, y + h), new Vector2(x, y + h - radius));
            p.LineTo(new Vector2(x, y + radius));
            p.QuadraticCurveTo(new Vector2(x, y), new Vector2(x + radius, y));
            p.ClosePath();
        }

        private static void BuildEllipse(Painter2D p, Vector2 c, float rx, float ry, int segments = 40)
        {
            for (var i = 0; i <= segments; i++)
            {
                var ang = (float)(2.0 * Math.PI * i / segments);
                var pt = new Vector2(c.x + Mathf.Cos(ang) * rx, c.y + Mathf.Sin(ang) * ry);
                if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
            }
            p.ClosePath();
        }

        private static void BuildDiamond(Painter2D p, Rect r)
        {
            p.MoveTo(new Vector2(r.center.x, r.y));
            p.LineTo(new Vector2(r.xMax, r.center.y));
            p.LineTo(new Vector2(r.center.x, r.yMax));
            p.LineTo(new Vector2(r.x, r.center.y));
            p.ClosePath();
        }

        // ---- Geometry helpers -----------------------------------------------------------------------

        /// <summary>The point on <paramref name="node"/>'s bounding border along the ray toward <paramref name="target"/>.</summary>
        private static Vector2 BorderPoint(MermaidNode node, Vector2 target)
        {
            var c = new Vector2(node.X + node.Width * 0.5f, node.Y + node.Height * 0.5f);
            var d = target - c;
            if (d == Vector2.zero) return c;
            var hw = node.Width * 0.5f;
            var hh = node.Height * 0.5f;
            var scale = Mathf.Min(hw / Mathf.Max(Mathf.Abs(d.x), 1e-4f), hh / Mathf.Max(Mathf.Abs(d.y), 1e-4f));
            return c + d * scale;
        }

        private static Vector2 Center(MermaidNode node)
            => new Vector2(node.X + node.Width * 0.5f, node.Y + node.Height * 0.5f);

        private bool TryNode(string id, out MermaidNode node) => _nodesById.TryGetValue(id, out node);
    }
}
