using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.UI.Figma
{
    /// <summary>
    /// A normalized, JToken-free view of a Figma node subtree — the structured input the token mapper
    /// consumes (Sprint 58.2). It reads the same node fields the UITK translator does (auto-layout,
    /// spacing, padding, fills, text style, geometry) but presents them as a clean tree, decoupling the
    /// uGUI mapping path from raw Figma JSON.
    /// </summary>
    /// <remarks>
    /// Independent of <c>FigmaToUiToolkitTranslator</c> (which keeps consuming JToken directly) so the
    /// UITK path is unchanged; both simply read Figma node JSON with the same field semantics. Hidden
    /// nodes (<c>visible:false</c>) are dropped during parse.
    /// </remarks>
    public sealed class FigmaFrameNode
    {
        public string Id;
        public string Name;
        public string Type;

        public float Width;
        public float Height;

        public bool IsAutoLayout;
        public bool Horizontal;
        public float ItemSpacing;
        public float PadLeft, PadTop, PadRight, PadBottom;

        /// <summary>The node's effective solid fill (last visible SOLID paint wins, as Figma composites), if any.</summary>
        public Color? SolidFill;
        /// <summary>True if any visible fill is an image paint.</summary>
        public bool HasImageFill;
        /// <summary>The largest corner radius on the node (for rounded-rect / button recognition).</summary>
        public float CornerRadius;

        // TEXT nodes only.
        public string Characters;
        public float FontSize;
        public float FontWeight;

        public readonly List<FigmaFrameNode> Children = new List<FigmaFrameNode>();
    }

    /// <summary>Parses a Figma node <see cref="JToken"/> tree into a <see cref="FigmaFrameNode"/> tree.</summary>
    public static class FigmaFrameModel
    {
        /// <summary>Parses <paramref name="node"/> and its visible descendants. Null/hidden → null.</summary>
        public static FigmaFrameNode Parse(JToken node)
        {
            if (node == null || node.Value<bool?>("visible") == false) return null;

            var model = new FigmaFrameNode
            {
                Id = node.Value<string>("id"),
                Name = node.Value<string>("name"),
                Type = node.Value<string>("type") ?? "UNKNOWN",
            };

            var box = node["absoluteBoundingBox"] as JObject;
            model.Width = box?["width"]?.Value<float>() ?? 0f;
            model.Height = box?["height"]?.Value<float>() ?? 0f;

            string layoutMode = node.Value<string>("layoutMode");
            model.IsAutoLayout = layoutMode == "HORIZONTAL" || layoutMode == "VERTICAL";
            model.Horizontal = layoutMode == "HORIZONTAL";
            model.ItemSpacing = node.Value<float?>("itemSpacing") ?? 0f;
            model.PadLeft = node.Value<float?>("paddingLeft") ?? 0f;
            model.PadTop = node.Value<float?>("paddingTop") ?? 0f;
            model.PadRight = node.Value<float?>("paddingRight") ?? 0f;
            model.PadBottom = node.Value<float?>("paddingBottom") ?? 0f;

            ReadFills(node, model);
            model.CornerRadius = ReadCornerRadius(node);

            if (model.Type == "TEXT")
            {
                model.Characters = node.Value<string>("characters");
                var ts = node["style"] as JObject;
                model.FontSize = ts?.Value<float?>("fontSize") ?? 0f;
                model.FontWeight = ts?.Value<float?>("fontWeight") ?? 400f;
            }

            if (node["children"] is JArray children)
                foreach (var child in children)
                {
                    var parsed = Parse(child);
                    if (parsed != null) model.Children.Add(parsed);
                }

            return model;
        }

        private static void ReadFills(JToken node, FigmaFrameNode model)
        {
            if (!(node["fills"] is JArray fills)) return;
            foreach (var fill in fills)
            {
                if (fill.Value<bool?>("visible") == false) continue;
                string type = fill.Value<string>("type");
                if (type == "SOLID")
                {
                    var col = fill["color"];
                    float opacity = fill.Value<float?>("opacity") ?? 1f;
                    model.SolidFill = new Color(
                        col?.Value<float?>("r") ?? 0f,
                        col?.Value<float?>("g") ?? 0f,
                        col?.Value<float?>("b") ?? 0f,
                        (col?.Value<float?>("a") ?? 1f) * Mathf.Clamp01(opacity));
                    model.HasImageFill = false; // a later solid overrides an earlier image
                }
                else if (type == "IMAGE")
                {
                    model.HasImageFill = true;
                }
            }
        }

        private static float ReadCornerRadius(JToken node)
        {
            float max = node.Value<float?>("cornerRadius") ?? 0f;
            if (node["rectangleCornerRadii"] is JArray arr)
                foreach (var c in arr)
                    max = Mathf.Max(max, c.Value<float>());
            foreach (var key in new[] { "topLeftRadius", "topRightRadius", "bottomRightRadius", "bottomLeftRadius" })
                max = Mathf.Max(max, node.Value<float?>(key) ?? 0f);
            return max;
        }
    }
}
