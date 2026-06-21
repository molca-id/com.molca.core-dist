using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Molca.Settings.Integration.Figma
{
    /// <summary>
    /// Translates a fetched Figma node subtree into a UI Toolkit UXML tree + a USS stylesheet.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/Figma/</c>.
    /// Registration: pure utility; not an asset. <b>GUI-agnostic and network-free</b> — it operates entirely on
    /// a deserialized <see cref="JToken"/> node, so it is unit-testable on a captured fixture with no editor or
    /// HTTP dependency.
    /// <para>
    /// This is a <b>scaffold</b>, not a pixel-perfect export. Supported node set: <c>FRAME</c>/<c>GROUP</c>/
    /// <c>COMPONENT</c>/<c>INSTANCE</c> containers (auto-layout → flexbox; otherwise absolute positioning),
    /// <c>TEXT</c> → <c>Label</c>, and <c>RECTANGLE</c> solid fills/corner-radius/strokes →
    /// <c>background-color</c>/<c>border-radius</c>/<c>border-*</c>. Every node type it cannot represent
    /// (vectors, boolean ops, masks, blend modes) is recorded in <see cref="Result.Unsupported"/> and never
    /// silently dropped — the fidelity ceiling is always visible. Image fills are recorded in
    /// <see cref="Result.ImageFills"/> for the asset pipeline to export; the translator itself touches no files.
    /// </para>
    /// </remarks>
    public sealed class FigmaToUiToolkitTranslator
    {
        /// <summary>A node the translator could not represent in UI Toolkit, with the reason.</summary>
        public sealed class UnsupportedNode
        {
            /// <summary>The Figma node id.</summary>
            public string Id;
            /// <summary>The Figma node name.</summary>
            public string Name;
            /// <summary>The Figma node type (e.g. <c>VECTOR</c>).</summary>
            public string Type;
            /// <summary>Why it could not be represented.</summary>
            public string Reason;
        }

        /// <summary>A node whose fill is an image and must be exported by the asset pipeline.</summary>
        public sealed class ImageFill
        {
            /// <summary>The Figma node id to render via the images endpoint.</summary>
            public string NodeId;
            /// <summary>The USS class assigned to the element, so the pipeline can wire its background-image.</summary>
            public string UssClass;
        }

        /// <summary>The output of a translation: the UXML/USS text plus the fidelity report.</summary>
        public sealed class Result
        {
            /// <summary>The generated UXML document text.</summary>
            public string Uxml;
            /// <summary>The generated USS stylesheet text.</summary>
            public string Uss;
            /// <summary>The frame's name (sanitized for use as a file name by the caller).</summary>
            public string FrameName;
            /// <summary>Nodes that could not be represented, with reasons.</summary>
            public readonly List<UnsupportedNode> Unsupported = new List<UnsupportedNode>();
            /// <summary>Image-fill nodes the asset pipeline must export and wire up.</summary>
            public readonly List<ImageFill> ImageFills = new List<ImageFill>();
            /// <summary>
            /// Vector/geometry nodes (icons, shapes) with no UI Toolkit equivalent, rasterized to PNG by the
            /// asset pipeline and wired as a <c>background-image</c> instead of being dropped.
            /// </summary>
            public readonly List<ImageFill> RasterizedNodes = new List<ImageFill>();
            /// <summary>Warnings for fonts that had no Unity-font mapping (default font used instead).</summary>
            public readonly List<string> FontWarnings = new List<string>();
        }

        /// <summary>
        /// Optional Figma-font-family → Unity-font-name lookup. Empty by default; populate to map specific
        /// designer fonts onto project fonts. An unmapped font yields a warning and the default font.
        /// </summary>
        private static readonly Dictionary<string, string> FontMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ContainerTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "FRAME", "GROUP", "COMPONENT", "COMPONENT_SET", "INSTANCE", "SECTION", "CANVAS"
        };

        private Result _result;
        private StringBuilder _uss;
        private int _counter;

        /// <summary>
        /// Translates a frame node subtree into UXML + USS.
        /// </summary>
        /// <param name="frameNode">The root node to translate (typically a <c>FRAME</c> document node).</param>
        /// <returns>The translation result, including the unsupported-node report. Never <c>null</c>.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="frameNode"/> is null.</exception>
        public Result Translate(JToken frameNode)
        {
            if (frameNode == null) throw new ArgumentNullException(nameof(frameNode));

            _result = new Result { FrameName = frameNode.Value<string>("name") ?? "Frame" };
            _uss = new StringBuilder();
            _counter = 0;

            var uxmlBody = new StringBuilder();
            EmitNode(frameNode, uxmlBody, indent: 1, parentBox: null, ctx: default, isRoot: true);

            var uxml = new StringBuilder();
            uxml.AppendLine("<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\">");
            uxml.Append(uxmlBody);
            uxml.AppendLine("</ui:UXML>");

            _result.Uxml = uxml.ToString();
            _result.Uss = _uss.ToString();
            return _result;
        }

        /// <summary>Parent auto-layout context threaded to a child so it can place itself like Figma.</summary>
        private readonly struct FlowContext
        {
            public FlowContext(bool parentAutoLayout, bool parentHorizontal, float itemSpacing, bool isLastFlowChild,
                float padLeft, float padTop, float padRight, float padBottom)
            {
                ParentAutoLayout = parentAutoLayout;
                ParentHorizontal = parentHorizontal;
                ItemSpacing = itemSpacing;
                IsLastFlowChild = isLastFlowChild;
                ParentPadLeft = padLeft;
                ParentPadTop = padTop;
                ParentPadRight = padRight;
                ParentPadBottom = padBottom;
            }

            /// <summary>Whether the parent is an auto-layout (flex) frame.</summary>
            public bool ParentAutoLayout { get; }
            /// <summary>Whether the parent's main axis is horizontal (row).</summary>
            public bool ParentHorizontal { get; }
            /// <summary>The parent's itemSpacing, or 0 when not applicable (e.g. space-between distributes gaps).</summary>
            public float ItemSpacing { get; }
            /// <summary>Whether this child is the last visible flow child (gets no trailing item-spacing margin).</summary>
            public bool IsLastFlowChild { get; }
            /// <summary>Parent padding (UI Toolkit absolute children resolve against the padding box).</summary>
            public float ParentPadLeft { get; }
            public float ParentPadTop { get; }
            public float ParentPadRight { get; }
            public float ParentPadBottom { get; }
        }

        private void EmitNode(JToken node, StringBuilder uxml, int indent, JObject parentBox, FlowContext ctx, bool isRoot)
        {
            // Honor visibility: an explicitly-hidden node is skipped entirely (not an "unsupported" case).
            if (node.Value<bool?>("visible") == false)
                return;

            string type = node.Value<string>("type") ?? "UNKNOWN";
            string name = node.Value<string>("name") ?? type;
            string id = node.Value<string>("id");

            // Masks and boolean/vector geometry have no UI Toolkit equivalent — report and skip the subtree.
            if (node.Value<bool?>("isMask") == true)
            {
                Report(id, name, type, "Mask layers are not supported by UI Toolkit.");
                return;
            }
            // Vector/geometry leaves (icons, shapes) have no UI Toolkit equivalent, but rather than drop them we
            // rasterize them to PNG and wire the image as a background — preserving the design's icons/graphics.
            if (!ContainerTypes.Contains(type) && type != "TEXT" && type != "RECTANGLE")
            {
                EmitRasterizedLeaf(node, uxml, indent, parentBox, ctx, type, name, id);
                return;
            }

            string ussClass = NextClass(name);
            var box = node["absoluteBoundingBox"] as JObject;
            var style = new List<string>();

            // Non-normal blend modes are recorded but the node is still rendered without the blend.
            string blend = node.Value<string>("blendMode");
            if (!string.IsNullOrEmpty(blend) && blend != "NORMAL" && blend != "PASS_THROUGH")
                Report(id, name, type, $"Blend mode '{blend}' is ignored (UI Toolkit has no blend modes).");

            ApplyLayout(node, style, box, parentBox, ctx, isRoot, type);
            ApplyVisuals(node, style, ussClass, type, id);

            WriteUssRule(ussClass, style);

            string tag = type == "TEXT" ? "ui:Label" : "ui:VisualElement";
            string pad = new string(' ', indent * 4);

            if (type == "TEXT")
            {
                string text = Escape(node.Value<string>("characters") ?? string.Empty);
                uxml.AppendLine($"{pad}<{tag} text=\"{text}\" class=\"{ussClass}\" />");
                return;
            }

            var children = node["children"] as JArray;
            if (children == null || children.Count == 0)
            {
                uxml.AppendLine($"{pad}<{tag} class=\"{ussClass}\" />");
                return;
            }

            uxml.AppendLine($"{pad}<{tag} class=\"{ussClass}\">");
            EmitChildren(node, children, box, uxml, indent + 1);
            uxml.AppendLine($"{pad}</{tag}>");
        }

        /// <summary>Emits a node's children, computing each child's flow context (item-spacing, last-child).</summary>
        private void EmitChildren(JToken node, JArray children, JObject box, StringBuilder uxml, int indent)
        {
            bool thisAutoLayout = IsAutoLayout(node);
            bool horizontal = node.Value<string>("layoutMode") == "HORIZONTAL";

            // UI Toolkit has no gap; itemSpacing becomes a trailing margin on each child except the last. When the
            // primary axis is space-distributed, justify-content owns the gaps, so no item-spacing margins.
            string primaryAlign = node.Value<string>("primaryAxisAlignItems");
            bool spaceDistributed = primaryAlign == "SPACE_BETWEEN" || primaryAlign == "SPACE_AROUND";
            float itemSpacing = (thisAutoLayout && !spaceDistributed) ? (node.Value<float?>("itemSpacing") ?? 0f) : 0f;

            float padLeft = node.Value<float?>("paddingLeft") ?? 0f;
            float padTop = node.Value<float?>("paddingTop") ?? 0f;
            float padRight = node.Value<float?>("paddingRight") ?? 0f;
            float padBottom = node.Value<float?>("paddingBottom") ?? 0f;

            int lastVisible = -1;
            for (int i = 0; i < children.Count; i++)
                if (children[i].Value<bool?>("visible") != false) lastVisible = i;

            for (int i = 0; i < children.Count; i++)
            {
                var ctx = new FlowContext(thisAutoLayout, horizontal, itemSpacing, isLastFlowChild: i == lastVisible,
                    padLeft, padTop, padRight, padBottom);
                EmitNode(children[i], uxml, indent, box, ctx, isRoot: false);
            }
        }

        /// <summary>
        /// Emits a leaf <c>VisualElement</c> for a vector/geometry node and records it for PNG rasterization.
        /// The node's appearance is baked into the image, so only layout (size/position) is applied here.
        /// </summary>
        private void EmitRasterizedLeaf(JToken node, StringBuilder uxml, int indent, JObject parentBox,
            FlowContext ctx, string type, string name, string id)
        {
            string ussClass = NextClass(name);
            var box = node["absoluteBoundingBox"] as JObject;
            var style = new List<string>();

            ApplyLayout(node, style, box, parentBox, ctx, isRoot: false, type);
            WriteUssRule(ussClass, style);

            if (!string.IsNullOrEmpty(id))
                _result.RasterizedNodes.Add(new ImageFill { NodeId = id, UssClass = ussClass });

            string pad = new string(' ', indent * 4);
            uxml.AppendLine($"{pad}<ui:VisualElement class=\"{ussClass}\" />");
        }

        private void ApplyLayout(JToken node, List<string> style, JObject box, JObject parentBox,
            FlowContext ctx, bool isRoot, string type)
        {
            float? width = box?["width"]?.Value<float>();
            float? height = box?["height"]?.Value<float>();

            // A child may opt out of flow even inside an auto-layout parent (Figma "absolute position" toggle).
            bool isAbsolute = !isRoot && (node.Value<string>("layoutPositioning") == "ABSOLUTE" || !ctx.ParentAutoLayout);

            if (isRoot)
            {
                if (width.HasValue) style.Add($"width: {Px(width.Value)};");
                if (height.HasValue) style.Add($"height: {Px(height.Value)};");
                // Establish a positioning context so descendant absolute children resolve against this frame.
                style.Add("position: relative;");
            }
            else if (isAbsolute)
            {
                if (box == null || parentBox == null)
                {
                    // No geometry to anchor against — falling back to flow beats collapsing every such child to (0,0).
                    Report(node.Value<string>("id"), node.Value<string>("name"), type,
                        "Absolute child has no bounding box; left in normal flow.");
                    if (type == "TEXT") ApplyTextSizing(node, style, box, isFlow: false);
                    else
                    {
                        if (width.HasValue) style.Add($"width: {Px(width.Value)};");
                        if (height.HasValue) style.Add($"height: {Px(height.Value)};");
                    }
                }
                else
                {
                    style.Add("position: absolute;");
                    EmitConstraints(node, style, box, parentBox, ctx, type);
                }
            }
            else
            {
                // Flow item in an auto-layout parent.
                if (ctx.ItemSpacing > 0 && !ctx.IsLastFlowChild)
                    style.Add(ctx.ParentHorizontal
                        ? $"margin-right: {Px(ctx.ItemSpacing)};"
                        : $"margin-bottom: {Px(ctx.ItemSpacing)};");

                if (type == "TEXT")
                {
                    ApplyTextSizing(node, style, box, isFlow: true);
                }
                else
                {
                    ApplyFlowSizing(node, style, width, height, ctx.ParentHorizontal);
                }
            }

            if (IsAutoLayout(node))
            {
                bool horizontal = node.Value<string>("layoutMode") == "HORIZONTAL";
                style.Add($"flex-direction: {(horizontal ? "row" : "column")};");

                if (node.Value<string>("layoutWrap") == "WRAP")
                    style.Add("flex-wrap: wrap;");

                string justify = MapPrimaryAlign(node.Value<string>("primaryAxisAlignItems"));
                if (justify != null) style.Add($"justify-content: {justify};");

                // UI Toolkit defaults align-items to 'stretch'; Figma's counter-axis default is MIN, so emit an
                // explicit value (default flex-start) to stop children stretching unexpectedly on the cross axis.
                string align = MapCounterAlign(node.Value<string>("counterAxisAlignItems")) ?? "flex-start";
                style.Add($"align-items: {align};");

                AddPadding(node, style, box);
            }
        }

        /// <summary>
        /// Emits width/height for a non-text flow child, honoring its own HUG sizing modes and the parent's
        /// fill directives (<c>layoutGrow</c> → flex-grow, <c>layoutAlign: STRETCH</c> → align-self) — each of
        /// which omits the corresponding fixed dimension so flex can do its job.
        /// </summary>
        private static void ApplyFlowSizing(JToken node, List<string> style, float? width, float? height, bool parentHorizontal)
        {
            bool grow = (node.Value<int?>("layoutGrow") ?? 0) >= 1 || (node.Value<float?>("layoutGrow") ?? 0f) >= 1f;
            string layoutAlign = node.Value<string>("layoutAlign");
            bool stretch = layoutAlign == "STRETCH";

            if (grow) style.Add("flex-grow: 1;");
            string alignSelf = layoutAlign switch
            {
                "MIN" => "flex-start",
                "CENTER" => "center",
                "MAX" => "flex-end",
                "STRETCH" => "stretch",
                _ => null
            };
            if (alignSelf != null) style.Add($"align-self: {alignSelf};");

            // A node that is itself an auto-layout frame hugs content on an axis when its sizing mode is AUTO.
            bool hugWidth = false, hugHeight = false;
            if (IsAutoLayout(node))
            {
                bool nodeHorizontal = node.Value<string>("layoutMode") == "HORIZONTAL";
                bool primaryAuto = node.Value<string>("primaryAxisSizingMode") == "AUTO";
                bool counterAuto = node.Value<string>("counterAxisSizingMode") == "AUTO";
                if (nodeHorizontal) { hugWidth = primaryAuto; hugHeight = counterAuto; }
                else { hugHeight = primaryAuto; hugWidth = counterAuto; }
            }

            // flex-grow fills the parent's main axis; align-self: stretch fills its cross axis — both omit the
            // matching fixed dimension or it would win over the flex behavior.
            bool omitWidth = hugWidth || (grow && parentHorizontal) || (stretch && !parentHorizontal);
            bool omitHeight = hugHeight || (grow && !parentHorizontal) || (stretch && parentHorizontal);

            if (width.HasValue && !omitWidth) style.Add($"width: {Px(width.Value)};");
            if (height.HasValue && !omitHeight) style.Add($"height: {Px(height.Value)};");
        }

        /// <summary>
        /// Emits sizing + wrapping for a TEXT node based on Figma's <c>textAutoResize</c>, so the label occupies
        /// the same box as the design instead of wrapping at the flex-container width and pushing siblings.
        /// </summary>
        private static void ApplyTextSizing(JToken node, List<string> style, JObject box, bool isFlow)
        {
            string resize = (node["style"] as JObject)?.Value<string>("textAutoResize");
            float? width = box?["width"]?.Value<float>();
            float? height = box?["height"]?.Value<float>();

            switch (resize)
            {
                case "WIDTH_AND_HEIGHT": // hug both axes
                    style.Add("white-space: nowrap;");
                    if (isFlow) style.Add("align-self: flex-start;");
                    break;
                case "NONE": // fixed box, clip overflow
                    if (width.HasValue) style.Add($"width: {Px(width.Value)};");
                    if (height.HasValue) style.Add($"height: {Px(height.Value)};");
                    style.Add("white-space: normal;");
                    style.Add("overflow: hidden;");
                    break;
                case "TRUNCATE": // single line with ellipsis
                    if (width.HasValue) style.Add($"width: {Px(width.Value)};");
                    if (height.HasValue) style.Add($"height: {Px(height.Value)};");
                    style.Add("white-space: nowrap;");
                    style.Add("overflow: hidden;");
                    style.Add("text-overflow: ellipsis;");
                    style.Add("-unity-text-overflow-position: end;");
                    break;
                default: // HEIGHT (auto-height) or absent: fixed width, wrap, grow vertically
                    if (width.HasValue) style.Add($"width: {Px(width.Value)};");
                    style.Add("white-space: normal;");
                    break;
            }

            // A flow label must not be shrunk by flex, or it wraps differently than the design.
            if (isFlow) style.Add("flex-shrink: 0;");
        }

        /// <summary>
        /// Places an absolutely-positioned child using its Figma layout constraints: MIN→left/top, MAX→right/
        /// bottom, STRETCH→both edges (dropping the fixed size), CENTER→50% + translate. Offsets are relative to
        /// the parent's padding box. SCALE is approximated as MIN.
        /// </summary>
        private void EmitConstraints(JToken node, List<string> style, JObject box, JObject parentBox, FlowContext ctx, string type)
        {
            float bx = box["x"].Value<float>(), by = box["y"].Value<float>();
            float bw = box.Value<float?>("width") ?? 0f, bh = box.Value<float?>("height") ?? 0f;
            float px = parentBox["x"].Value<float>(), py = parentBox["y"].Value<float>();
            float pw = parentBox.Value<float?>("width") ?? 0f, ph = parentBox.Value<float?>("height") ?? 0f;

            float left = bx - px - ctx.ParentPadLeft;
            float top = by - py - ctx.ParentPadTop;
            float right = (px + pw) - (bx + bw) - ctx.ParentPadRight;
            float bottom = (py + ph) - (by + bh) - ctx.ParentPadBottom;

            var cons = node["constraints"] as JObject;
            string h = cons?.Value<string>("horizontal");
            string v = cons?.Value<string>("vertical");
            bool translateX = false, translateY = false;

            switch (h)
            {
                case "MAX": style.Add($"right: {Px(right)};"); break;
                case "STRETCH": style.Add($"left: {Px(left)};"); style.Add($"right: {Px(right)};"); break;
                case "CENTER": style.Add("left: 50%;"); translateX = true; break;
                default: style.Add($"left: {Px(left)};"); break; // MIN / SCALE / absent
            }
            switch (v)
            {
                case "MAX": style.Add($"bottom: {Px(bottom)};"); break;
                case "STRETCH": style.Add($"top: {Px(top)};"); style.Add($"bottom: {Px(bottom)};"); break;
                case "CENTER": style.Add("top: 50%;"); translateY = true; break;
                default: style.Add($"top: {Px(top)};"); break;
            }
            if (translateX || translateY)
                style.Add($"translate: {(translateX ? "-50%" : "0")} {(translateY ? "-50%" : "0")};");

            if (h == "SCALE" || v == "SCALE")
                Report(node.Value<string>("id"), node.Value<string>("name"), type,
                    "SCALE constraint approximated as fixed offset (UI Toolkit has no proportional anchoring).");

            // STRETCH owns the axis size via the two edges; otherwise pin from the box. TEXT manages its own size.
            if (type == "TEXT")
            {
                ApplyTextSizing(node, style, box, isFlow: false);
            }
            else
            {
                if (h != "STRETCH" && bw > 0f) style.Add($"width: {Px(bw)};");
                if (v != "STRETCH" && bh > 0f) style.Add($"height: {Px(bh)};");
            }
        }

        private void ApplyVisuals(JToken node, List<string> style, string ussClass, string type, string id)
        {
            // Fills → background-color, or an image fill recorded for the asset pipeline. Figma paints are
            // bottom-first, so the LAST visible paint composites on top and wins.
            if (node["fills"] is JArray fills)
            {
                string solid = null;
                bool image = false;
                bool gradient = false;
                foreach (var fill in fills)
                {
                    if (fill.Value<bool?>("visible") == false) continue;
                    string fillType = fill.Value<string>("type");
                    if (fillType == "SOLID" && type != "TEXT")
                    {
                        solid = Rgba(fill["color"], fill.Value<float?>("opacity") ?? 1f);
                        image = false;
                        gradient = false;
                    }
                    else if (fillType == "IMAGE")
                    {
                        image = true;
                        gradient = false;
                    }
                    else if (fillType != null && fillType.StartsWith("GRADIENT") && type != "TEXT")
                    {
                        // UI Toolkit has no gradient fills; rasterize the node to a PNG instead (leaf nodes only,
                        // or the render would bake in children that are also emitted as elements).
                        gradient = true;
                        image = false;
                    }
                }

                bool isLeaf = !(node["children"] is JArray kids && kids.Count > 0);
                if (image || (gradient && isLeaf))
                {
                    _result.ImageFills.Add(new ImageFill { NodeId = id, UssClass = ussClass });
                    if (gradient)
                        Report(id, node.Value<string>("name"), type, "Gradient rasterized to a background image (UI Toolkit has no gradient fills).");
                }
                else if (gradient)
                {
                    Report(id, node.Value<string>("name"), type, "Gradient fill on a container is not representable (UI Toolkit has no gradient fills).");
                }
                else if (solid != null)
                {
                    style.Add($"background-color: {solid};");
                }
            }

            // Group/element opacity composites the whole subtree, like CSS opacity.
            float? opacity = node.Value<float?>("opacity");
            if (opacity.HasValue && opacity.Value < 1f)
                style.Add($"opacity: {opacity.Value.ToString("0.###", CultureInfo.InvariantCulture)};");

            // clipsContent → clip children (and round-clip when combined with border-radius).
            if (node.Value<bool?>("clipsContent") == true && type != "TEXT")
                style.Add("overflow: hidden;");

            EmitCornerRadius(node, style);
            EmitStrokes(node, style, type, id);

            // Effects (shadows/blurs) have no USS equivalent — record the fidelity loss rather than dropping silently.
            if (node["effects"] is JArray effects)
            {
                foreach (var effect in effects)
                {
                    if (effect.Value<bool?>("visible") == false) continue;
                    string kind = effect.Value<string>("type");
                    if (kind == "DROP_SHADOW" || kind == "INNER_SHADOW" || kind == "LAYER_BLUR" || kind == "BACKGROUND_BLUR")
                        Report(id, node.Value<string>("name"), type, $"Effect '{kind}' has no UI Toolkit equivalent and was dropped.");
                }
            }

            if (type == "TEXT")
                ApplyTextStyle(node, style);
        }

        /// <summary>Emits border-radius: per-corner longhands when the corners differ, else the shorthand.</summary>
        private static void EmitCornerRadius(JToken node, List<string> style)
        {
            float[] corners = null;
            if (node["rectangleCornerRadii"] is JArray arr && arr.Count == 4)
                corners = new[] { arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>(), arr[3].Value<float>() };
            else if (node["topLeftRadius"] != null || node["topRightRadius"] != null
                     || node["bottomRightRadius"] != null || node["bottomLeftRadius"] != null)
                corners = new[]
                {
                    node.Value<float?>("topLeftRadius") ?? 0f, node.Value<float?>("topRightRadius") ?? 0f,
                    node.Value<float?>("bottomRightRadius") ?? 0f, node.Value<float?>("bottomLeftRadius") ?? 0f
                };

            if (corners != null)
            {
                if (corners[0] == corners[1] && corners[1] == corners[2] && corners[2] == corners[3])
                {
                    if (corners[0] > 0f) style.Add($"border-radius: {Px(corners[0])};");
                }
                else
                {
                    style.Add($"border-top-left-radius: {Px(corners[0])};");
                    style.Add($"border-top-right-radius: {Px(corners[1])};");
                    style.Add($"border-bottom-right-radius: {Px(corners[2])};");
                    style.Add($"border-bottom-left-radius: {Px(corners[3])};");
                }
                return;
            }

            float? radius = node.Value<float?>("cornerRadius");
            if (radius.HasValue && radius.Value > 0f)
                style.Add($"border-radius: {Px(radius.Value)};");
        }

        /// <summary>
        /// Emits borders from the first visible solid stroke: per-side widths from individualStrokeWeights when
        /// present, else the uniform strokeWeight. UI Toolkit borders draw inside, so non-INSIDE strokeAlign is reported.
        /// </summary>
        private void EmitStrokes(JToken node, List<string> style, string type, string id)
        {
            if (!(node["strokes"] is JArray strokes) || strokes.Count == 0) return;

            string color = null;
            foreach (var stroke in strokes)
            {
                if (stroke.Value<bool?>("visible") == false || stroke.Value<string>("type") != "SOLID") continue;
                color = Rgba(stroke["color"], stroke.Value<float?>("opacity") ?? 1f);
                break;
            }
            if (color == null) return;

            var individual = node["individualStrokeWeights"] as JObject;
            foreach (var side in new[] { "top", "right", "bottom", "left" })
            {
                float weight = individual != null
                    ? individual.Value<float?>(side) ?? 0f
                    : node.Value<float?>("strokeWeight") ?? 1f;
                style.Add($"border-{side}-width: {Px(weight)};");
                style.Add($"border-{side}-color: {color};");
            }

            string align = node.Value<string>("strokeAlign");
            if (!string.IsNullOrEmpty(align) && align != "INSIDE")
                Report(id, node.Value<string>("name"), type, $"strokeAlign '{align}' not reproducible (UI Toolkit borders draw inside).");
        }

        private void ApplyTextStyle(JToken node, List<string> style)
        {
            var ts = node["style"] as JObject;
            if (ts != null)
            {
                float? fontSize = ts.Value<float?>("fontSize");
                if (fontSize.HasValue) style.Add($"font-size: {Px(fontSize.Value)};");

                float? weight = ts.Value<float?>("fontWeight");
                bool italic = ts.Value<bool?>("italic") == true;
                string fontStyle = (weight.HasValue && weight.Value >= 700f)
                    ? (italic ? "bold-and-italic" : "bold")
                    : (italic ? "italic" : "normal");
                style.Add($"-unity-font-style: {fontStyle};");

                style.Add($"-unity-text-align: {CombinedTextAlign(ts)};");

                // letterSpacing is px (percent unit is converted against the font size).
                float? letter = ts.Value<float?>("letterSpacing");
                if (letter.HasValue && Math.Abs(letter.Value) > 0.01f)
                {
                    float px = ts.Value<string>("letterSpacingUnit") == "PERCENT" && fontSize.HasValue
                        ? fontSize.Value * letter.Value / 100f
                        : letter.Value;
                    style.Add($"letter-spacing: {Px(px)};");
                }

                float? paragraph = ts.Value<float?>("paragraphSpacing");
                if (paragraph.HasValue && paragraph.Value > 0f)
                    style.Add($"-unity-paragraph-spacing: {Px(paragraph.Value)};");

                // USS has no line-height; record the loss when the design set an explicit one.
                string lineHeightUnit = ts.Value<string>("lineHeightUnit");
                if (!string.IsNullOrEmpty(lineHeightUnit) && lineHeightUnit != "AUTO")
                    _result.FontWarnings.Add("Line height is not representable in USS (per-FontAsset only); design line-height ignored.");

                string family = ts.Value<string>("fontFamily");
                if (!string.IsNullOrEmpty(family) && !FontMap.ContainsKey(family))
                    _result.FontWarnings.Add($"Font '{family}' has no Unity-font mapping; using the default font.");
            }

            // Text color comes from the node's first solid fill.
            if (node["fills"] is JArray fills)
            {
                foreach (var fill in fills)
                {
                    if (fill.Value<bool?>("visible") == false) continue;
                    if (fill.Value<string>("type") == "SOLID")
                    {
                        style.Add($"color: {Rgba(fill["color"], fill.Value<float?>("opacity") ?? 1f)};");
                        break;
                    }
                }
            }
        }

        // ---- helpers ----------------------------------------------------------------------------------

        private static bool IsAutoLayout(JToken node)
        {
            string mode = node.Value<string>("layoutMode");
            return mode == "HORIZONTAL" || mode == "VERTICAL";
        }

        private static void AddPadding(JToken node, List<string> style, JObject box)
        {
            float l = node.Value<float?>("paddingLeft") ?? 0f;
            float r = node.Value<float?>("paddingRight") ?? 0f;
            float t = node.Value<float?>("paddingTop") ?? 0f;
            float b = node.Value<float?>("paddingBottom") ?? 0f;

            // Figma cannot render padding larger than the frame, but Yoga grows the box to fit it — which would
            // shove siblings and clip content. Clamp each axis's padding to the bounding-box size to match Figma.
            float? w = box?["width"]?.Value<float>();
            float? h = box?["height"]?.Value<float>();
            if (w.HasValue && l + r > w.Value && l + r > 0f) { float k = w.Value / (l + r); l *= k; r *= k; }
            if (h.HasValue && t + b > h.Value && t + b > 0f) { float k = h.Value / (t + b); t *= k; b *= k; }

            if (l > 0) style.Add($"padding-left: {Px(l)};");
            if (r > 0) style.Add($"padding-right: {Px(r)};");
            if (t > 0) style.Add($"padding-top: {Px(t)};");
            if (b > 0) style.Add($"padding-bottom: {Px(b)};");
        }

        private static string MapPrimaryAlign(string v) => v switch
        {
            "MIN" => "flex-start",
            "CENTER" => "center",
            "MAX" => "flex-end",
            "SPACE_BETWEEN" => "space-between",
            _ => null
        };

        private static string MapCounterAlign(string v) => v switch
        {
            "MIN" => "flex-start",
            "CENTER" => "center",
            "MAX" => "flex-end",
            _ => null
        };

        /// <summary>
        /// Maps Figma's horizontal + vertical text alignment to UI Toolkit's single <c>-unity-text-align</c>
        /// enum (<c>{upper|middle|lower}-{left|center|right}</c>). JUSTIFIED has no equivalent → left.
        /// </summary>
        private static string CombinedTextAlign(JObject ts)
        {
            string horizontal = ts.Value<string>("textAlignHorizontal") switch
            {
                "LEFT" => "left",
                "CENTER" => "center",
                "RIGHT" => "right",
                "JUSTIFIED" => "left",
                _ => "left"
            };
            string vertical = ts.Value<string>("textAlignVertical") switch
            {
                "TOP" => "upper",
                "BOTTOM" => "lower",
                _ => "middle"
            };
            return $"{vertical}-{horizontal}";
        }

        private void Report(string id, string name, string type, string reason)
            => _result.Unsupported.Add(new UnsupportedNode { Id = id, Name = name, Type = type, Reason = reason });

        private string NextClass(string name)
        {
            var sb = new StringBuilder("fg-");
            foreach (char c in (name ?? "node").ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            sb.Append('-').Append(_counter++);
            return sb.ToString();
        }

        private void WriteUssRule(string ussClass, List<string> style)
        {
            if (style.Count == 0) return;
            _uss.Append('.').Append(ussClass).AppendLine(" {");
            foreach (var line in style)
                _uss.Append("    ").AppendLine(line);
            _uss.AppendLine("}");
        }

        private static string Px(float value)
            => Math.Round(value).ToString(CultureInfo.InvariantCulture) + "px";

        private static string Rgba(JToken color, float opacity)
        {
            if (color == null) return "rgba(0, 0, 0, 0)";
            int r = (int)Math.Round((color.Value<float?>("r") ?? 0f) * 255f);
            int g = (int)Math.Round((color.Value<float?>("g") ?? 0f) * 255f);
            int b = (int)Math.Round((color.Value<float?>("b") ?? 0f) * 255f);
            float a = (color.Value<float?>("a") ?? 1f) * Math.Clamp(opacity, 0f, 1f);
            return string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3:0.###})", r, g, b, a);
        }

        private static string Escape(string s) => s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("\n", " ");
    }
}
