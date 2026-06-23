using System;
using System.Collections.Generic;
using UnityEngine;
using Molca.ColorID;
using Molca.UI.Tokens;

namespace Molca.Editor.UI.Figma
{
    /// <summary>
    /// The deterministic token pre-pass (Sprint 58.3): walks a <see cref="FigmaFrameNode"/> tree and emits
    /// a <i>draft</i> <see cref="UiIntentSpec"/> — snapping each fill to the nearest catalog color
    /// (CIEDE2000), each text run to the nearest style preset (by size), and recognizing button-shaped
    /// nodes and repeated-sibling lists. No model call and no raw appearance: an unmappable color becomes
    /// the <c>color/_unmapped</c> sentinel for human/model review, never a hex literal. Fully testable.
    /// </summary>
    public static class FigmaTokenMapper
    {
        /// <summary>CIEDE2000 ΔE above which a fill is considered to have no catalog match (→ <c>_unmapped</c>).</summary>
        public const double ColorUnmappedThreshold = 25.0;

        /// <summary>Minimum similar siblings for a container to be recognized as a list.</summary>
        public const int ListMinChildren = 3;

        /// <summary>A per-decision record for the mapping report (confidences + unmapped flags).</summary>
        public sealed class ReportEntry
        {
            public string Path;
            public string Kind;       // "color" | "text" | "control"
            public string TokenId;
            public double Confidence; // 0..1 (1 = exact)
            public bool Unmapped;
        }

        /// <summary>The outcome of a draft mapping: the spec plus the per-node decisions.</summary>
        public sealed class Draft
        {
            public UiIntentSpec Spec;
            public readonly List<ReportEntry> Report = new List<ReportEntry>();
        }

        /// <summary>
        /// Builds a draft spec for <paramref name="root"/> against <paramref name="catalog"/>, resolving
        /// catalog color tokens to actual colors through <paramref name="colors"/> (the active palette).
        /// </summary>
        public static Draft BuildDraft(FigmaFrameNode root, MolcaUiTokenRegistry catalog, IColorProvider colors,
            string sourceFrame, float worldScale, float minHitCm)
        {
            var draft = new Draft();
            if (root == null || catalog == null) { draft.Spec = new UiIntentSpec(); return draft; }

            var palette = BuildPalette(catalog, colors);
            var presets = BuildTextPresets(catalog);
            bool hasButton = catalog.TryResolve("control/button", out _);
            bool hasListItem = catalog.TryResolve("control/list-item", out _);

            draft.Spec = new UiIntentSpec
            {
                sourceFrame = sourceFrame ?? root.Name,
                worldScale = worldScale,
                minHitCm = minHitCm,
                catalogId = catalog.name,
                root = MapNode(root, "root", palette, presets, hasButton, hasListItem, draft.Report),
            };
            return draft;
        }

        private static UiIntentNode MapNode(FigmaFrameNode node, string path,
            List<FigmaColorSnap.PaletteEntry> palette, List<(string id, float size)> presets,
            bool hasButton, bool hasListItem, List<ReportEntry> report)
        {
            bool isText = node.Type == "TEXT";
            bool hasChildren = node.Children.Count > 0;
            bool isButton = !isText && (NameLooksLikeButton(node.Name)
                || (node.CornerRadius > 0f && node.SolidFill.HasValue && HasTextChild(node)));
            bool isList = hasChildren && node.Children.Count >= ListMinChildren && ChildrenSimilar(node.Children);

            var ui = new UiIntentNode
            {
                layout = node.IsAutoLayout ? (node.Horizontal ? "horizontal" : "vertical") : "none",
                gap = node.ItemSpacing,
            };
            if (node.PadLeft != 0f || node.PadTop != 0f || node.PadRight != 0f || node.PadBottom != 0f)
                ui.padding = new[] { node.PadLeft, node.PadTop, node.PadRight, node.PadBottom };

            if (isText)
            {
                ui.type = "text";
                ui.text = SnapText(node.FontSize, presets, path, report);
                ui.color = SnapColor(node.SolidFill, palette, path, report); // text color
            }
            else if (isButton)
            {
                ui.type = "button";
                ui.token = hasButton ? "control/button" : null;
                if (!hasButton) AddReport(report, path, "control", null, 0, true);
                ui.color = SnapColor(node.SolidFill, palette, path, report);
            }
            else if (isList)
            {
                ui.type = "list";
                ui.token = hasListItem ? "control/list-item" : null;
                ui.bind = "items";
                if (!hasListItem) AddReport(report, path, "control", null, 0, true);
                // Keep a single representative child as the row template.
                ui.children = new List<UiIntentNode>
                {
                    MapNode(node.Children[0], $"{path}.template", palette, presets, hasButton, hasListItem, report)
                };
                return ui;
            }
            else if (node.HasImageFill && !hasChildren)
            {
                ui.type = "image";
            }
            else if (node.SolidFill.HasValue && hasChildren)
            {
                ui.type = "panel";
                ui.color = SnapColor(node.SolidFill, palette, path, report);
            }
            else
            {
                ui.type = hasChildren ? "group" : "image";
            }

            if (hasChildren)
            {
                ui.children = new List<UiIntentNode>(node.Children.Count);
                for (int i = 0; i < node.Children.Count; i++)
                    ui.children.Add(MapNode(node.Children[i], $"{path}.children[{i}]",
                        palette, presets, hasButton, hasListItem, report));
            }
            return ui;
        }

        private static string SnapColor(Color? fill, List<FigmaColorSnap.PaletteEntry> palette,
            string path, List<ReportEntry> report)
        {
            if (!fill.HasValue || palette.Count == 0) return null;
            if (!FigmaColorSnap.TryNearest(palette, fill.Value, out var tokenId, out var dist)) return null;

            if (dist > ColorUnmappedThreshold)
            {
                AddReport(report, path, "color", "color/_unmapped", 0, true);
                return "color/_unmapped";
            }
            double confidence = Math.Max(0.0, 1.0 - dist / ColorUnmappedThreshold);
            AddReport(report, path, "color", tokenId, confidence, false);
            return tokenId;
        }

        private static string SnapText(float fontSize, List<(string id, float size)> presets,
            string path, List<ReportEntry> report)
        {
            if (presets.Count == 0) return null;
            string best = null;
            float bestDiff = float.MaxValue;
            foreach (var (id, size) in presets)
            {
                float diff = Mathf.Abs(size - fontSize);
                if (diff < bestDiff) { bestDiff = diff; best = id; }
            }
            // Confidence falls off over a 12pt window; presets are coarse, so this is advisory.
            double confidence = Math.Max(0.0, 1.0 - bestDiff / 12.0);
            AddReport(report, path, "text", best, confidence, false);
            return best;
        }

        private static List<FigmaColorSnap.PaletteEntry> BuildPalette(MolcaUiTokenRegistry catalog, IColorProvider colors)
        {
            var palette = new List<FigmaColorSnap.PaletteEntry>();
            if (colors == null) return palette;
            foreach (var token in catalog.AllTokens)
            {
                if (token == null || token.Category != MolcaUiTokenCategory.Color) continue;
                palette.Add(new FigmaColorSnap.PaletteEntry(token.Id, colors.GetColor(token.SwatchName, token.ColorId)));
            }
            return palette;
        }

        private static List<(string id, float size)> BuildTextPresets(MolcaUiTokenRegistry catalog)
        {
            // Snap by preferred font size only (avoids a direct TMPro dependency on FontStyles).
            var presets = new List<(string, float)>();
            foreach (var token in catalog.AllTokens)
            {
                if (token == null || token.Category != MolcaUiTokenCategory.Text || token.StyleInfo == null) continue;
                presets.Add((token.Id, token.StyleInfo.PreferredSize));
            }
            return presets;
        }

        private static bool NameLooksLikeButton(string name) =>
            !string.IsNullOrEmpty(name) && name.ToLowerInvariant().Contains("button");

        private static bool HasTextChild(FigmaFrameNode node)
        {
            foreach (var c in node.Children)
                if (c.Type == "TEXT") return true;
            return false;
        }

        // Siblings are "similar" when they share a type and have child counts within 1 of the first.
        private static bool ChildrenSimilar(List<FigmaFrameNode> children)
        {
            var first = children[0];
            foreach (var c in children)
            {
                if (c.Type != first.Type) return false;
                if (Math.Abs(c.Children.Count - first.Children.Count) > 1) return false;
            }
            return true;
        }

        private static void AddReport(List<ReportEntry> report, string path, string kind, string tokenId,
            double confidence, bool unmapped) =>
            report.Add(new ReportEntry { Path = path, Kind = kind, TokenId = tokenId, Confidence = confidence, Unmapped = unmapped });
    }
}
