using System.Collections.Generic;
using System.Linq;
using Molca.Editor.UI;
using Molca.Editor.Validation;
using Molca.Sequence;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Hub view (Sprint 43; expandable detail + inline fixes Sprint 47) of every
    /// <see cref="SequenceController"/> in the open scene(s) with its validation status. Re-runs the
    /// Sprint-37 registry on demand; each controller row expands to list its findings, a finding click
    /// pings the offending step, and findings with a safe deterministic fix show an inline "Fix" button.
    /// Validation itself is side-effect-free and never opens scenes; only an explicit Fix click mutates.
    /// </summary>
    internal sealed class MolcaHubSequencesSection : VisualElement
    {
        private readonly VisualElement _list;
        private readonly Label _summary;

        internal MolcaHubSequencesSection()
        {
            AddToClassList("molca-hub-sequences-section");
            style.flexGrow = 1;

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };
            var title = new Label("Sequence Validation") { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 14, flexGrow = 1 } };
            var refresh = new Button(Refresh) { text = "Refresh" };
            refresh.AddToClassList("molca-hub-mini-button");
            header.Add(title);
            header.Add(refresh);
            Add(header);

            _summary = new Label { style = { marginBottom = 6 } };
            _summary.AddToClassList("molca-hub-muted");
            Add(_summary);

            _list = new VisualElement();
            Add(_list);

            Refresh();
        }

        private void Refresh()
        {
            _list.Clear();

            var controllers = Object.FindObjectsByType<SequenceController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int invalid = 0;

            foreach (var controller in controllers.OrderBy(c => c.name, System.StringComparer.Ordinal))
            {
                var findings = SequenceValidatorRegistry.Run(controller);
                int errors = findings.Count(f => f.Severity == SequenceValidationSeverity.Error);
                if (errors > 0) invalid++;
                _list.Add(BuildRow(controller, findings));
            }

            _summary.text = controllers.Length == 0
                ? "No SequenceControllers in the open scene(s)."
                : $"{controllers.Length} controller(s) · {invalid} with errors.";
        }

        private VisualElement BuildRow(SequenceController controller, IReadOnlyList<SequenceValidationFinding> findings)
        {
            int errors = findings.Count(f => f.Severity == SequenceValidationSeverity.Error);
            int warnings = findings.Count(f => f.Severity == SequenceValidationSeverity.Warning);
            int infos = findings.Count(f => f.Severity == SequenceValidationSeverity.Info);

            var container = new VisualElement();
            container.AddToClassList("molca-hub-seq-row");

            // Header: caret + status dot + name + counts. Click toggles the detail panel.
            var headerRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingTop = 3, paddingBottom = 3 } };

            bool expandable = findings.Count > 0;
            var caret = new Label(expandable ? "▸" : " ") { style = { width = 12, unityTextAlign = TextAnchor.MiddleCenter } };
            caret.AddToClassList("molca-hub-muted");
            headerRow.Add(caret);

            headerRow.Add(StatusDot(errors, warnings));

            var name = new Label($"{controller.name}  ({controller.RefId})") { style = { flexGrow = 1 } };
            headerRow.Add(name);

            var counts = new Label(SummarizeCounts(errors, warnings, infos));
            counts.AddToClassList("molca-hub-muted");
            headerRow.Add(counts);
            container.Add(headerRow);

            // Detail panel: one entry per finding, hidden until the header is clicked.
            var details = new VisualElement { style = { marginLeft = 20, marginBottom = 4, display = DisplayStyle.None } };
            foreach (var finding in findings)
                details.Add(BuildFinding(controller, finding));
            container.Add(details);

            if (expandable)
            {
                headerRow.RegisterCallback<MouseDownEvent>(_ =>
                {
                    bool show = details.style.display == DisplayStyle.None;
                    details.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                    caret.text = show ? "▾" : "▸";
                });
            }
            else
            {
                // Valid controller: clicking the row just pings/selects it.
                var target = controller;
                headerRow.RegisterCallback<MouseDownEvent>(_ => Ping(target != null ? target.gameObject : null));
            }

            return container;
        }

        private VisualElement BuildFinding(SequenceController controller, SequenceValidationFinding finding)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart, paddingTop = 2, paddingBottom = 2 } };

            var sev = new Label(SeverityGlyph(finding.Severity)) { style = { width = 14, color = SeverityColor(finding.Severity), unityFontStyleAndWeight = FontStyle.Bold } };
            row.Add(sev);

            var text = new Label(finding.Message) { style = { flexGrow = 1, whiteSpace = WhiteSpace.Normal } };
            row.Add(text);

            // Click anywhere on the message to ping the offending step (when the finding targets one).
            var step = finding.Step;
            if (step != null)
            {
                text.AddToClassList("molca-hub-link");
                text.RegisterCallback<MouseDownEvent>(_ => Ping(step != null ? step.gameObject : null));
            }

            // Inline "Fix" button when a deterministic, non-destructive Unity-Undo fix handles this category.
            if (HasSafeFix(finding.Category))
            {
                var target = controller;
                var captured = finding;
                var fix = new Button(() => ApplyFix(target, captured)) { text = "Fix" };
                fix.AddToClassList("molca-hub-mini-button");
                fix.tooltip = SafeFixDescription(finding.Category);
                row.Add(fix);
            }

            return row;
        }

        private void ApplyFix(SequenceController controller, SequenceValidationFinding finding)
        {
            if (controller == null) return;
            var result = SequenceFixRegistry.ApplyFixes(
                controller, new[] { finding }, RemediationPolicy.SafeOnly);

            if (result.TotalApplied == 0)
                Debug.LogWarning($"[Molca Hub] No safe fix applied for '{finding.Category}' on '{controller.name}'.");
            else if (result.RequiresSceneReload)
                Debug.Log($"[Molca Hub] Applied fix for '{finding.Category}'; reload the scene to see the change.");

            Refresh();
        }

        private static bool HasSafeFix(string category)
            => SequenceFixRegistry.FixesFor(category)
                .Any(f => f.IsDeterministic && SequenceFixRegistry.PolicyAllows(RemediationPolicy.SafeOnly, f));

        private static string SafeFixDescription(string category)
            => SequenceFixRegistry.FixesFor(category)
                .FirstOrDefault(f => f.IsDeterministic && SequenceFixRegistry.PolicyAllows(RemediationPolicy.SafeOnly, f))
                ?.Description ?? "Apply the registered fix for this finding.";

        private static void Ping(GameObject go)
        {
            if (go == null) return;
            Selection.activeObject = go;
            EditorGUIUtility.PingObject(go);
        }

        private static VisualElement StatusDot(int errors, int warnings)
        {
            return new VisualElement
            {
                style =
                {
                    width = 8, height = 8, borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4, marginRight = 8,
                    backgroundColor = errors > 0 ? MolcaEditorColors.StatusError
                        : warnings > 0 ? MolcaEditorColors.StatusWarn
                        : MolcaEditorColors.StatusOk
                }
            };
        }

        private static string SummarizeCounts(int errors, int warnings, int infos)
        {
            if (errors == 0 && warnings == 0 && infos == 0) return "valid";
            var parts = new List<string>();
            if (errors > 0) parts.Add($"{errors} err");
            if (warnings > 0) parts.Add($"{warnings} warn");
            if (infos > 0) parts.Add($"{infos} info");
            return string.Join(" · ", parts);
        }

        private static string SeverityGlyph(SequenceValidationSeverity severity) => severity switch
        {
            SequenceValidationSeverity.Error => "✕",   // ✕
            SequenceValidationSeverity.Warning => "⚠", // ⚠
            _ => "ℹ",                                   // ℹ
        };

        private static Color SeverityColor(SequenceValidationSeverity severity) => severity switch
        {
            SequenceValidationSeverity.Error => MolcaEditorColors.StatusError,
            SequenceValidationSeverity.Warning => MolcaEditorColors.StatusWarn,
            _ => MolcaEditorColors.Link,
        };
    }
}
