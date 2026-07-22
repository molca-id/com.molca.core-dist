using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Utilities
{
    /// <summary>
    /// UI Toolkit renderer for the BudgetMonitor overlay.
    /// Added programmatically by <see cref="BudgetMonitor"/> — do not place manually.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    internal class BudgetMonitorView : MonoBehaviour
    {
        private static readonly Color BackgroundColor    = new Color(0.10f, 0.10f, 0.10f, 0.90f);
        private static readonly Color SectionHeaderColor = new Color(0.70f, 0.80f, 1.00f, 1.00f);
        private static readonly Color TextColor          = Color.white;
        private static readonly Color WarningColor       = new Color(1.00f, 0.80f, 0.00f, 1.00f);
        private static readonly Color CriticalColor      = new Color(1.00f, 0.30f, 0.30f, 1.00f);
        private static readonly Color GoodColor          = new Color(0.30f, 1.00f, 0.30f, 1.00f);
        private static readonly Color BarBgColor         = new Color(0.15f, 0.15f, 0.15f, 0.80f);

        private VisualElement _panelRoot;
        private VisualElement _contentRoot;
        private bool _isVisible = true;

        // Cached row references — updated in-place on RefreshValues to avoid DOM churn every 0.5 s.
        private readonly Dictionary<string, MetricRowUI> _rows = new();
        private int _lastMetricCount;

        private static readonly string[][] SectionDefinitions = BuildSectionDefinitions();

        public bool IsVisible => _isVisible;

        /// <param name="panelSettings">Required by UIDocument. Null logs a warning and the overlay may not render.</param>
        /// <param name="uiFont">
        /// Font applied to every label. May be <c>null</c>, in which case the bundled Poppins font (else a
        /// dynamic OS font) is resolved. The shipped PanelSettings has no text settings, so <b>without a
        /// resolved font the labels rasterize no glyphs and only the bars render</b>.
        /// </param>
        /// <param name="toggleKeyLabel">Display name of the toggle key, shown in the header (Sprint 54: a plain string, so the view needs no input-package dependency).</param>
        internal void Initialize(PanelSettings panelSettings, Font uiFont, BudgetMonitor.UIAnchor anchor, Vector2 position, string toggleKeyLabel)
        {
            var doc = GetComponent<UIDocument>();
            if (panelSettings != null)
                doc.panelSettings = panelSettings;
            else
                Debug.LogWarning("[BudgetMonitor] No PanelSettings assigned — UI Toolkit overlay may not render.");

            BuildPanel(doc.rootVisualElement, uiFont, anchor, position, toggleKeyLabel);
        }

        public void SetVisible(bool visible)
        {
            _isVisible = visible;
            if (_contentRoot != null)
                _contentRoot.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void ToggleVisibility() => SetVisible(!_isVisible);

        /// <summary>
        /// Refreshes the overlay. Rebuilds the metric rows when the set of available metrics changes;
        /// otherwise performs a lightweight in-place value update.
        /// </summary>
        internal void UpdateMetrics(IReadOnlyDictionary<string, BudgetMonitor.MetricData> metrics)
        {
            if (_panelRoot == null) return;

            bool needsRebuild = metrics.Count != _lastMetricCount || HasNewKeys(metrics);
            _lastMetricCount = metrics.Count;

            if (needsRebuild)
                RebuildContent(metrics);
            else
                RefreshValues(metrics);
        }

        // ── Build ────────────────────────────────────────────────────────────────

        private void BuildPanel(VisualElement docRoot, Font uiFont, BudgetMonitor.UIAnchor anchor, Vector2 position, string toggleKeyLabel)
        {
            docRoot.pickingMode = PickingMode.Ignore;

            // The shipped PanelSettings has no text settings/font assigned, so at runtime Labels rasterize no
            // glyphs and only the colored bar VisualElements are visible. -unity-font-definition is inherited,
            // so applying a resolved font once on the panel root covers every Label added beneath it.
            var font = ResolveFont(uiFont);
            if (font != null)
                docRoot.style.unityFontDefinition = FontDefinition.FromFont(font);
            else
                Debug.LogWarning("[BudgetMonitor] No overlay font could be resolved; metric labels may not render (bars only).");

            var container = new VisualElement();
            container.style.backgroundColor = new StyleColor(BackgroundColor);
            container.style.paddingTop    = 5;
            container.style.paddingBottom = 5;
            container.style.paddingLeft   = 8;
            container.style.paddingRight  = 8;
            container.style.minWidth      = 220;
            // Position the container absolutely within the full-screen panel.
            ApplyAnchor(container, anchor, position);
            docRoot.Add(container);

            var header = new Label($"Budget Monitor  Ctrl+{toggleKeyLabel}");
            header.style.color = new StyleColor(new Color(0.9f, 0.9f, 0.9f, 1f));
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.fontSize    = 13;
            header.style.marginBottom = 3;
            container.Add(header);

            _contentRoot = new VisualElement();
            _contentRoot.style.display = _isVisible ? DisplayStyle.Flex : DisplayStyle.None;
            container.Add(_contentRoot);

            _panelRoot = docRoot;
        }

        private void RebuildContent(IReadOnlyDictionary<string, BudgetMonitor.MetricData> metrics)
        {
            _contentRoot.Clear();
            _rows.Clear();

            foreach (var sectionDef in SectionDefinitions)
            {
                string sectionName = sectionDef[0];
                var present = new List<string>();
                for (int i = 1; i < sectionDef.Length; i++)
                    if (metrics.ContainsKey(sectionDef[i]))
                        present.Add(sectionDef[i]);

                if (present.Count == 0) continue;

                var sectionHeader = new Label(sectionName);
                sectionHeader.style.color = new StyleColor(SectionHeaderColor);
                sectionHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
                sectionHeader.style.fontSize    = 12;
                sectionHeader.style.marginTop   = 4;
                sectionHeader.style.marginBottom = 1;
                _contentRoot.Add(sectionHeader);

                foreach (var key in present)
                {
                    var row = CreateMetricRow(key, metrics[key]);
                    _contentRoot.Add(row.Root);
                    _rows[key] = row;
                }
            }
        }

        private static MetricRowUI CreateMetricRow(string key, BudgetMonitor.MetricData metric)
        {
            var row = new MetricRowUI { Root = new VisualElement() };
            row.Root.style.marginBottom = 1;

            var labelRow = new VisualElement();
            labelRow.style.flexDirection    = FlexDirection.Row;
            labelRow.style.justifyContent   = Justify.SpaceBetween;

            row.NameLabel  = new Label($"{key}:") { pickingMode = PickingMode.Ignore };
            row.ValueLabel = new Label(metric.value) { pickingMode = PickingMode.Ignore };

            foreach (var lbl in new[] { row.NameLabel, row.ValueLabel })
                lbl.style.fontSize = 11;

            labelRow.Add(row.NameLabel);
            labelRow.Add(row.ValueLabel);
            row.Root.Add(labelRow);

            if (metric.maxValue > 0)
            {
                var barBg = new VisualElement();
                barBg.style.height          = 6;
                barBg.style.marginTop       = 1;
                barBg.style.backgroundColor = new StyleColor(BarBgColor);

                row.BarFill = new VisualElement();
                row.BarFill.style.height = Length.Percent(100);
                // Seed width/color from the current value so the bar isn't blank until the first RefreshValues tick.
                float fill = Mathf.Clamp01(metric.currentValue / metric.maxValue);
                row.BarFill.style.width           = Length.Percent(fill * 100f);
                row.BarFill.style.backgroundColor = new StyleColor(GetBarColor(metric, fill));

                barBg.Add(row.BarFill);
                row.Root.Add(barBg);
            }

            ApplyColors(row, metric);
            return row;
        }

        // ── Refresh ──────────────────────────────────────────────────────────────

        private void RefreshValues(IReadOnlyDictionary<string, BudgetMonitor.MetricData> metrics)
        {
            foreach (var kvp in metrics)
            {
                if (!_rows.TryGetValue(kvp.Key, out var row)) continue;
                var m = kvp.Value;

                row.ValueLabel.text = m.value;
                ApplyColors(row, m);

                if (row.BarFill != null && m.maxValue > 0)
                {
                    float fill = Mathf.Clamp01(m.currentValue / m.maxValue);
                    row.BarFill.style.width           = Length.Percent(fill * 100f);
                    row.BarFill.style.backgroundColor = new StyleColor(GetBarColor(m, fill));
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Resources path (namespaced, extension-less) of the Poppins font bundled with Core, loaded when no
        /// explicit HUD font is assigned. Lives under a package <c>Resources</c> folder so it resolves in
        /// player builds, not just the editor.
        /// </summary>
        private const string BundledFontResourcePath = "Molca/Poppins-Medium";

        /// <summary>
        /// Resolves the overlay label font: the explicitly-assigned HUD font first, else the bundled Poppins
        /// font, else a dynamic OS font as a last resort so metric text never silently disappears.
        /// </summary>
        /// <param name="explicitFont">Font assigned on the <see cref="BudgetMonitor"/>, or <c>null</c>.</param>
        /// <returns>A usable <see cref="Font"/>, or <c>null</c> if none could be resolved.</returns>
        private static Font ResolveFont(Font explicitFont)
        {
            if (explicitFont != null) return explicitFont;

            var bundled = Resources.Load<Font>(BundledFontResourcePath);
            if (bundled != null) return bundled;

            // OS fallback: the labels are plain ASCII, so any sans-serif face renders them correctly.
            try { return Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Helvetica", "sans-serif" }, 12); }
            catch { return null; }
        }

        private static void ApplyColors(MetricRowUI row, BudgetMonitor.MetricData metric)
        {
            Color c = metric.isCritical ? CriticalColor :
                      metric.isWarning  ? WarningColor  :
                      metric.type == BudgetMonitor.MetricType.FPS && metric.currentValue >= metric.maxValue ? GoodColor :
                      TextColor;

            row.NameLabel.style.color  = new StyleColor(c);
            row.ValueLabel.style.color = new StyleColor(c);
        }

        private static Color GetBarColor(BudgetMonitor.MetricData m, float fill)
        {
            if (m.isCritical) return CriticalColor;
            if (m.isWarning)  return WarningColor;
            if (m.type == BudgetMonitor.MetricType.FPS)
                return fill >= 1f ? GoodColor : Color.Lerp(WarningColor, GoodColor, fill);
            return Color.Lerp(GoodColor, WarningColor, fill * 0.8f);
        }

        private bool HasNewKeys(IReadOnlyDictionary<string, BudgetMonitor.MetricData> metrics)
        {
            foreach (var key in metrics.Keys)
                if (!_rows.ContainsKey(key)) return true;
            return false;
        }

        private static void ApplyAnchor(VisualElement root, BudgetMonitor.UIAnchor anchor, Vector2 pos)
        {
            root.style.position = Position.Absolute;
            switch (anchor)
            {
                case BudgetMonitor.UIAnchor.TopLeft:
                    root.style.left = pos.x; root.style.top = pos.y;
                    break;
                case BudgetMonitor.UIAnchor.TopRight:
                    root.style.right = pos.x; root.style.top = pos.y;
                    break;
                case BudgetMonitor.UIAnchor.BottomLeft:
                    root.style.left = pos.x; root.style.bottom = pos.y;
                    break;
                case BudgetMonitor.UIAnchor.BottomRight:
                    root.style.right = pos.x; root.style.bottom = pos.y;
                    break;
                case BudgetMonitor.UIAnchor.Center:
                    root.style.left      = Length.Percent(50);
                    root.style.top       = Length.Percent(50);
                    root.style.translate = new StyleTranslate(new Translate(Length.Percent(-50), Length.Percent(-50)));
                    break;
            }
        }

        private static string[][] BuildSectionDefinitions()
        {
            var defs = new System.Collections.Generic.List<string[]>
            {
                new[] { "Performance", "FPS" },
                new[] { "Memory", "Total Memory", "Texture Memory" },
                new[] { "Scene", "GameObjects", "Material Count", "Mesh Count" },
                // Rendering metrics are ProfilerRecorder-backed (Sprint 54), so they show in dev builds too.
                new[] { "Rendering", "Draw Calls", "Batches", "SetPass Calls", "Triangles" },
            };
            return defs.ToArray();
        }

        private class MetricRowUI
        {
            public VisualElement Root;
            public Label NameLabel;
            public Label ValueLabel;
            public VisualElement BarFill; // null when metric has no maxValue
        }
    }
}
