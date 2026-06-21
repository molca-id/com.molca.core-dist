using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor
{
    /// <summary>
    /// Right-hand details panel (UI Toolkit, Phase 4): the per-step inspector is hosted via
    /// <see cref="InspectorElement"/> — which transparently falls back to <see cref="StepEditor"/>'s
    /// IMGUI <c>OnInspectorGUI</c> (StepEditor itself is unchanged). The batch auxiliary panel and the
    /// change-type controls remain IMGUI inside a nested <see cref="IMGUIContainer"/>.
    /// </summary>
    public partial class SequenceVisualizerView
    {
        private Label _detailsHeader;
        private HelpBox _detailsEmpty;
        private VisualElement _detailsInspectorHost;
        private IMGUIContainer _detailsAuxImgui;

        // The exact target set currently hosted in the InspectorElement; used to avoid recreating the
        // editor (and losing inspector state) on every refresh — only rebuild when the targets change.
        private Object[] _detailsTargets;

        /// <summary>Builds the scrollable details pane shell; content is populated by <see cref="RebuildDetails"/>.</summary>
        private VisualElement BuildDetailsPane()
        {
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            scroll.style.paddingTop = 6;
            scroll.style.paddingLeft = 8;
            scroll.style.paddingRight = 8;

            _detailsHeader = new Label("Step Details");
            _detailsHeader.AddToClassList("molca-card__title");
            _detailsHeader.style.marginBottom = 4;
            scroll.Add(_detailsHeader);

            _detailsEmpty = new HelpBox(
                "Select a step from the hierarchy to view its details.", HelpBoxMessageType.Info);
            scroll.Add(_detailsEmpty);

            _detailsInspectorHost = new VisualElement();
            scroll.Add(_detailsInspectorHost);

            _detailsAuxImgui = new IMGUIContainer(OnDetailsAuxGUI);
            scroll.Add(_detailsAuxImgui);

            return scroll;
        }

        /// <summary>
        /// Syncs the details pane to the current selection: header text, empty-state, and the hosted
        /// inspector (recreated only when the target set changes). Batch/change-type IMGUI repaints itself.
        /// </summary>
        private void RebuildDetails()
        {
            if (_detailsHeader == null) return;

            var primary = GetPrimarySelectedStep();
            int count = _selection.Count;
            _detailsHeader.text = count > 1 ? $"Step Details ({count} selected)" : "Step Details";

            if (primary == null)
            {
                _detailsEmpty.style.display = DisplayStyle.Flex;
                _detailsInspectorHost.Clear();
                _detailsAuxImgui.style.display = DisplayStyle.None;
                _detailsTargets = null;
                if (_stepEditor != null)
                {
                    Object.DestroyImmediate(_stepEditor);
                    _stepEditor = null;
                }
                return;
            }

            _detailsEmpty.style.display = DisplayStyle.None;
            _detailsAuxImgui.style.display = DisplayStyle.Flex;

            // Multi-edit only when every selected step shares the primary's concrete type.
            bool multiEdit = count > 1 && _selection.Selected.All(s => s.GetType() == primary.GetType());
            Object[] targets = multiEdit
                ? _selection.Selected.Cast<Object>().ToArray()
                : new Object[] { primary };

            bool targetsChanged = _detailsTargets == null || _detailsTargets.Length != targets.Length ||
                !targets.SequenceEqual(_detailsTargets);
            if (targetsChanged)
            {
                if (_stepEditor != null) Object.DestroyImmediate(_stepEditor);
                _stepEditor = UnityEditor.Editor.CreateEditor(targets);
                _detailsTargets = targets;

                _detailsInspectorHost.Clear();
                if (_stepEditor != null)
                    _detailsInspectorHost.Add(new InspectorElement(_stepEditor));
            }

            _detailsAuxImgui.MarkDirtyRepaint();
        }

        // Batch auxiliary editing + change-type controls — still IMGUI, hosted under the inspector.
        private void OnDetailsAuxGUI()
        {
            var primary = GetPrimarySelectedStep();
            if (primary == null) return;

            int count = _selection.Count;

            // Batch auxiliary editing — works across mixed step types, since auxiliaries
            // are independent of the owning step's concrete type.
            if (!Application.isPlaying && count > 1)
            {
                _auxiliaryBatchPanel.Draw(_selection.Selected);
            }

            if (!Application.isPlaying)
            {
                DrawChangeTypeControls();
            }
        }

        private void DrawChangeTypeControls()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Change Step Type", EditorStyles.boldLabel);

            if (_stepTypes == null || _stepTypeNames == null)
            {
                EditorGUILayout.HelpBox("Step types not loaded.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            // Ensure the dropdown selection is valid
            if (_changeStepTypeIndex >= _stepTypeNames.Length) _changeStepTypeIndex = 0;

            _changeStepTypeIndex = EditorGUILayout.Popup("New Type", _changeStepTypeIndex, _stepTypeNames);

            var targetType = _stepTypes[_changeStepTypeIndex];
            int count = _selection.Count;

            // Enabled when at least one selected step is not already the target type.
            int convertible = _selection.Selected.Count(s => s != null && s.GetType() != targetType);
            using (new EditorGUI.DisabledScope(convertible == 0))
            {
                string buttonLabel = count > 1 ? $"Apply Type Change ({convertible})" : "Apply Type Change";
                if (GUILayout.Button(buttonLabel))
                {
                    string message = count > 1
                        ? $"Change the type of {convertible} selected step(s) to '{_stepTypeNames[_changeStepTypeIndex]}'?\n\nOnly each step's Step ID and Auxiliaries will be preserved. Other data will be lost."
                        : $"Are you sure you want to change the type of '{GetPrimarySelectedStep().name}' to '{_stepTypeNames[_changeStepTypeIndex]}'?\n\nOnly the Step ID and Auxiliaries will be preserved. Other data will be lost.";
                    if (EditorUtility.DisplayDialog("Change Step Type?", message, "Change", "Cancel"))
                    {
                        if (count > 1)
                        {
                            ChangeStepTypes(targetType);
                        }
                        else
                        {
                            ChangeStepType(GetPrimarySelectedStep(), targetType);
                        }
                        GUIUtility.ExitGUI(); // Prevent layout errors after component destruction
                    }
                }
            }
            EditorGUILayout.EndVertical();
        }
    }
}
