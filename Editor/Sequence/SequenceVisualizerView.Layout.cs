using System.Linq;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using Molca.Sequence;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor
{
    /// <summary>
    /// UI Toolkit body shell (Phase 2): a <see cref="TwoPaneSplitView"/> whose left pane hosts the
    /// chrome cards (controller info, add-step, CSV, filters) above the still-IMGUI steps tree, and
    /// whose right pane hosts the still-IMGUI details panel. The tree and details migrate in
    /// Phases 3 and 4; this partial owns the layout and the chrome cards only.
    /// </summary>
    public partial class SequenceVisualizerView
    {
        private const float DefaultLeftPaneWidth = 320f;

        private VisualElement _body;
        private VisualElement _noControllerBox;
        private TwoPaneSplitView _split;
        private VisualElement _leftPane;
        private VisualElement _leftChrome;
        private VisualElement _rightPane;

        /// <summary>Builds the body shell once; content is (re)populated by <see cref="RefreshBody"/>.</summary>
        private void BuildBody()
        {
            _body = new VisualElement();
            _body.style.flexGrow = 1;
            Add(_body);

            // Empty-state shown when no controller is selected.
            _noControllerBox = new VisualElement();
            _noControllerBox.style.flexGrow = 1;
            _noControllerBox.style.paddingTop = 10;
            _noControllerBox.style.paddingLeft = 10;
            _noControllerBox.style.paddingRight = 10;
            var help = new HelpBox(
                "No SequenceController is assigned. Select one in the scene or via the toolbar field.",
                HelpBoxMessageType.Info);
            _noControllerBox.Add(help);
            _noControllerBox.Add(MolcaButtons.Toolbar("Find First Controller in Scene", AutoSelectController));
            _body.Add(_noControllerBox);

            // The split: left = chrome cards + tree, right = details.
            _split = new TwoPaneSplitView(0, DefaultLeftPaneWidth, TwoPaneSplitViewOrientation.Horizontal);
            _split.style.flexGrow = 1;

            _leftPane = new VisualElement();
            _leftPane.style.flexGrow = 1;
            _leftPane.style.minWidth = MIN_PANEL_WIDTH;

            _leftChrome = new VisualElement();
            _leftChrome.style.paddingTop = 6;
            _leftChrome.style.paddingLeft = 8;
            _leftChrome.style.paddingRight = 8;
            _leftPane.Add(_leftChrome);

            // Phase 3 (Sprint 44): the steps tree is a native UI Toolkit TreeView (see the .Tree partial).
            _leftPane.Add(BuildTreePane());

            _rightPane = new VisualElement();
            _rightPane.style.flexGrow = 1;
            _rightPane.style.minWidth = 220;

            // Phase 4 (Sprint 44): details panel is a UI Toolkit tree. The step inspector is hosted via
            // InspectorElement (which falls back to StepEditor's IMGUI OnInspectorGUI), while the batch
            // auxiliary panel and change-type controls remain IMGUI inside a nested container.
            _rightPane.Add(BuildDetailsPane());

            _split.Add(_leftPane);
            _split.Add(_rightPane);
            _body.Add(_split);
        }

        /// <summary>
        /// Syncs the UITK body to the current controller/mode: toggles the empty-state vs the split,
        /// shows/hides the details pane, and rebuilds the chrome cards. Cheap enough to call per
        /// <see cref="Repaint"/>; the tree and details rebuild on demand.
        /// </summary>
        private void RefreshBody()
        {
            if (_body == null) return;

            bool hasController = _selectedController != null;
            _noControllerBox.style.display = hasController ? DisplayStyle.None : DisplayStyle.Flex;
            _split.style.display = hasController ? DisplayStyle.Flex : DisplayStyle.None;

            // Details pane visibility tracks the toolbar toggle.
            _rightPane.style.display = _showDetailedInfo ? DisplayStyle.Flex : DisplayStyle.None;

            if (hasController)
                RebuildChrome();
        }

        /// <summary>Rebuilds the left-pane chrome cards (controller info, add-step, CSV, filters).</summary>
        private void RebuildChrome()
        {
            _leftChrome.Clear();
            _leftChrome.Add(BuildControllerCard());

            if (!Application.isPlaying)
                _leftChrome.Add(BuildAddStepCard());

            if (Application.isPlaying)
                _leftChrome.Add(BuildFiltersCard());
        }

        private VisualElement BuildControllerCard()
        {
            var card = new MolcaSectionCard(_selectedController.DisplayName);

            var stepsList = Application.isPlaying ? _selectedController.Steps : _editorModeSteps;
            RecomputeStepCounts(stepsList);

            card.Body.Add(FieldRow("Total Steps", _cachedActiveStepCount.ToString()));

            if (Application.isPlaying)
            {
                string current = _selectedController.CurrentStep != null
                    ? _selectedController.CurrentStep.DisplayName : "None";
                var currentRow = FieldRow("Current Step", current);
                var jump = MolcaButtons.Mini("Jump", () =>
                {
                    if (_selectedController.CurrentStep == null) return;
                    _selection.Select(_selectedController.CurrentStep);
                    SyncSelectionToUnity();
                    EditorGUIUtility.PingObject(_selectedController.CurrentStep.gameObject);
                    ExpandParents(_selectedController.CurrentStep);
                    Repaint();
                });
                jump.SetEnabled(_selectedController.CurrentStep != null);
                jump.style.marginLeft = 6;
                currentRow.Add(jump);
                card.Body.Add(currentRow);

                var actions = new VisualElement();
                actions.style.flexDirection = FlexDirection.Row;
                actions.style.marginTop = 4;
                var complete = MolcaButtons.Toolbar("Complete Current Step",
                    () => _selectedController.CompleteCurrentStep());
                complete.style.flexGrow = 1;
                actions.Add(complete);

                bool running = _selectedController.IsRunning;
                var startStop = MolcaButtons.Toolbar(running ? "Stop Sequence" : "Start Sequence", () =>
                {
                    if (_selectedController.IsRunning) _selectedController.StopSequence();
                    else _selectedController.StartSequence();
                    Repaint();
                });
                startStop.style.flexGrow = 1;
                startStop.style.marginLeft = 4;
                actions.Add(startStop);
                card.Body.Add(actions);
            }

            if (_cachedActiveStepCount > 0)
            {
                float fraction = (float)_cachedCompletedStepCount / _cachedActiveStepCount;
                var bar = new ProgressBar
                {
                    value = fraction * 100f,
                    title = Application.isPlaying
                        ? $"{_cachedCompletedStepCount}/{_cachedActiveStepCount} Completed"
                        : $"{_cachedCompletedStepCount}/{_cachedActiveStepCount} Steps Ready",
                };
                bar.style.marginTop = 6;
                card.Body.Add(bar);
            }

            return card;
        }

        private VisualElement BuildAddStepCard()
        {
            // Collapsible (and collapsed by default) — step CRUD is used far less often than browsing.
            // State persists per project so it stays where the user left it.
            var foldout = new Foldout
            {
                text = "Add Step",
                value = MolcaEditorPrefs.GetBool(Key("AddStepExpanded"), false),
            };
            foldout.AddToClassList("molca-card");
            foldout.RegisterValueChangedCallback(evt =>
                MolcaEditorPrefs.SetBool(Key("AddStepExpanded"), evt.newValue));

            if (_stepTypes == null || _stepTypes.Count == 0)
            {
                foldout.Add(new HelpBox(
                    "No custom Step types found. Create a class inheriting from 'Step' to add new steps.",
                    HelpBoxMessageType.Info));
                return foldout;
            }

            if (_stepTypeNames == null || _stepTypeNames.Length != _stepTypes.Count)
                _stepTypeNames = _stepTypes.Select(t => ObjectNames.NicifyVariableName(t.Name)).ToArray();

            if (_selectedStepTypeIndex >= _stepTypeNames.Length) _selectedStepTypeIndex = 0;

            var dropdown = new PopupField<string>(_stepTypeNames.ToList(), _selectedStepTypeIndex);
            dropdown.RegisterValueChangedCallback(evt =>
                _selectedStepTypeIndex = _stepTypeNames.ToList().IndexOf(evt.newValue));
            foldout.Add(dropdown);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 6;

            var addChild = MolcaButtons.Toolbar("Add as Child",
                () => AddNewStep(_stepTypes[_selectedStepTypeIndex], GetPrimarySelectedStep()));
            addChild.SetEnabled(GetPrimarySelectedStep() != null);
            addChild.style.flexGrow = 1;
            row.Add(addChild);

            var addRoot = MolcaButtons.Toolbar("Add as Root",
                () => AddNewStep(_stepTypes[_selectedStepTypeIndex], null));
            addRoot.style.flexGrow = 1;
            addRoot.style.marginLeft = 4;
            row.Add(addRoot);
            foldout.Add(row);

            int sel = _selection.Count;
            var duplicate = MolcaButtons.Toolbar(
                sel > 1 ? $"Duplicate Selected ({sel})" : "Duplicate Selected",
                DuplicateSelectedSteps);
            duplicate.SetEnabled(sel > 0);
            duplicate.style.marginTop = 4;
            foldout.Add(duplicate);

            var divider = new VisualElement();
            divider.AddToClassList("molca-divider");
            foldout.Add(divider);

            foldout.Add(MolcaButtons.Toolbar("Open CSV Step Importer…",
                () => CsvStepImporterWindow.ShowWindow(_selectedController)));

            return foldout;
        }

        private VisualElement BuildFiltersCard()
        {
            var card = new MolcaSectionCard("Filters");

            var completed = new Toggle("Show Completed") { value = _showCompletedSteps };
            completed.RegisterValueChangedCallback(evt =>
            {
                _showCompletedSteps = evt.newValue;
                MolcaEditorPrefs.SetBool(Key("ShowCompleted"), _showCompletedSteps);
                Repaint();
            });
            card.Body.Add(completed);

            var inactive = new Toggle("Show Inactive") { value = _showInactiveSteps };
            inactive.RegisterValueChangedCallback(evt =>
            {
                _showInactiveSteps = evt.newValue;
                MolcaEditorPrefs.SetBool(Key("ShowInactive"), _showInactiveSteps);
                Repaint();
            });
            card.Body.Add(inactive);

            return card;
        }

        // --- Small helpers ---

        private static VisualElement FieldRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-field-row");

            var labelEl = new Label(label);
            labelEl.AddToClassList("molca-field-label");
            row.Add(labelEl);

            var valueEl = new Label(value);
            valueEl.AddToClassList("molca-field-control");
            valueEl.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(valueEl);

            return row;
        }

        private void RecomputeStepCounts(System.Collections.Generic.IReadOnlyList<Step> stepsList)
        {
            if (_stepCountsValid) return;

            _cachedActiveStepCount = 0;
            _cachedCompletedStepCount = 0;
            if (stepsList != null)
            {
                foreach (var s in stepsList)
                {
                    if (s == null || !s.gameObject.activeInHierarchy || !s.enabled) continue;
                    _cachedActiveStepCount++;
                    if (s.IsCompleted) _cachedCompletedStepCount++;
                }
            }
            _stepCountsValid = true;
        }
    }
}
