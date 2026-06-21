using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Molca.Editor.Utils;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Validation surface for the tree: per-step badges from <see cref="SequenceValidator"/>,
    /// click-to-fix for broken auxiliaries, and the "Select All Problems" action.
    /// </summary>
    public partial class SequenceVisualizerView
    {
        /// <summary>Recomputes the findings cache if it was invalidated since the last build.</summary>
        private void EnsureValidation()
        {
            if (!_validationDirty) return;

            _findingsByStep.Clear();
            _findingCount = 0;

            if (_selectedController != null)
            {
                var findings = SequenceValidator.Validate(_selectedController);
                _findingCount = findings.Count;
                foreach (var finding in findings)
                {
                    if (finding.Step == null) continue;
                    if (!_findingsByStep.TryGetValue(finding.Step, out var list))
                    {
                        list = new List<SequenceFinding>();
                        _findingsByStep[finding.Step] = list;
                    }
                    list.Add(finding);
                }
            }

            _validationDirty = false;
        }

        /// <summary>Findings on a single step, or <c>null</c> when it has none.</summary>
        private List<SequenceFinding> GetFindingsForStep(Step step)
        {
            EnsureValidation();
            return step != null && _findingsByStep.TryGetValue(step, out var list) ? list : null;
        }

        private void ShowFindingMenu(Step step, List<SequenceFinding> findings)
        {
            var menu = new GenericMenu();
            foreach (var finding in findings)
            {
                // Each problem is a disabled header line, optionally followed by fix entries.
                menu.AddDisabledItem(new GUIContent(finding.Message));
                if (finding.HasFix)
                {
                    AddAuxiliaryFixItems(menu, finding);
                }
            }
            menu.ShowAsContext();
        }

        /// <summary>
        /// Adds "Fix → assign type" entries for a broken-auxiliary finding, one per concrete
        /// <see cref="StepAuxiliary"/> type, routed through <see cref="SequenceValidator.TryFixBrokenAuxiliary"/>.
        /// </summary>
        private void AddAuxiliaryFixItems(GenericMenu menu, SequenceFinding finding)
        {
            var step = finding.Step;
            string scenePath = step.gameObject.scene.path;

            foreach (var type in TypeCache.GetTypesDerivedFrom<StepAuxiliary>()
                         .Where(t => !t.IsAbstract)
                         .OrderBy(t => t.Name))
            {
                var attr = type.GetCustomAttribute<AuxiliaryMenuAttribute>();
                string path = attr != null && !string.IsNullOrEmpty(attr.Path) ? attr.Path : type.Name;
                var capturedType = type;
                menu.AddItem(new GUIContent($"Fix auxiliary {finding.AuxiliaryIndex} as.../{path}"), false, () =>
                {
                    if (SequenceValidator.TryFixBrokenAuxiliary(finding, capturedType))
                    {
                        _validationDirty = true;
                        // The YAML changed on disk; reload so the new type deserializes.
                        EditorApplication.delayCall += () => AuxiliaryTypeFixerUtility.PromptSceneReload(scenePath);
                    }
                });
            }
        }

        /// <summary>
        /// Selects every step with at least one validation finding (edit mode), expands
        /// parents so they are visible, and pushes the selection to Unity.
        /// </summary>
        private void SelectAllProblems()
        {
            EnsureValidation();
            if (_findingsByStep.Count == 0)
            {
                Notify("No validation problems found.");
                return;
            }

            var problems = _findingsByStep.Keys.Where(s => s != null).ToList();
            _selection.SelectMany(problems, problems[problems.Count - 1]);
            foreach (var step in problems) ExpandParents(step);
            SyncSelectionToUnity();
            Repaint();
        }
    }
}
