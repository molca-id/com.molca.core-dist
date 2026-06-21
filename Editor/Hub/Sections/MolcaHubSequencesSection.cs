using System.Linq;
using Molca.Editor.Validation;
using Molca.Sequence;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Read-only Hub view (Sprint 43) of every <see cref="SequenceController"/> in the open scene(s) with
    /// its validation status. Re-runs the Sprint-37 registry on demand; a row click pings/selects the
    /// controller. Side-effect-free — never opens scenes.
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
                int warnings = findings.Count(f => f.Severity == SequenceValidationSeverity.Warning);
                if (errors > 0) invalid++;
                _list.Add(BuildRow(controller, errors, warnings));
            }

            _summary.text = controllers.Length == 0
                ? "No SequenceControllers in the open scene(s)."
                : $"{controllers.Length} controller(s) · {invalid} with errors.";
        }

        private static VisualElement BuildRow(SequenceController controller, int errors, int warnings)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingTop = 3, paddingBottom = 3 } };
            row.AddToClassList("molca-hub-seq-row");

            var dot = new VisualElement
            {
                style =
                {
                    width = 8, height = 8, borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4, marginRight = 8,
                    backgroundColor = errors > 0 ? new Color(0.86f, 0.30f, 0.30f)
                        : warnings > 0 ? new Color(0.90f, 0.74f, 0.25f)
                        : new Color(0.40f, 0.80f, 0.45f)
                }
            };
            row.Add(dot);

            var name = new Label($"{controller.name}  ({controller.RefId})") { style = { flexGrow = 1 } };
            row.Add(name);

            var counts = new Label(errors > 0 || warnings > 0 ? $"{errors} err · {warnings} warn" : "valid");
            counts.AddToClassList("molca-hub-muted");
            row.Add(counts);

            // Capture the controller for ping/select on click.
            var target = controller;
            row.RegisterCallback<MouseDownEvent>(_ =>
            {
                if (target != null)
                {
                    Selection.activeObject = target.gameObject;
                    EditorGUIUtility.PingObject(target.gameObject);
                }
            });
            return row;
        }
    }
}
