using System.Collections.Generic;
using System.Linq;
using Molca.Editor.UI;
using Molca.Sequence;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Graph
{
    /// <summary>
    /// GraphView-based sequence authoring window — the primary visual authoring surface (Sprint 8).
    /// Renders one node per <see cref="Step"/> with execution-order edges, and keeps selection in
    /// lockstep with Unity's global selection through the shared <see cref="StepSelectionModel"/>.
    /// </summary>
    /// <remarks>
    /// Composition of the Sprint 7 GUI-free services — this window owns no editing or validation
    /// logic of its own:
    /// <list type="bullet">
    /// <item>Canvas/nodes/edges, pan/zoom/minimap, two-way selection sync (8.1).</item>
    /// <item>Tidy auto-layout + Ref-Id-keyed position persistence via <see cref="StepGraphLayoutStore"/> (8.2).</item>
    /// <item>Structural editing — create/reparent/delete/duplicate — through <see cref="StepEditingService"/> (8.3).</item>
    /// <item>Embedded <c>StepEditor</c> + <c>AuxiliaryBatchPanel</c> inspector panel (8.4).</item>
    /// <item>Play-mode overlay: status tints, current-step outline, follow mode, runtime controls (8.5).</item>
    /// <item>Distinct visuals/edges for <c>ParallelStep</c>/<c>BranchingStep</c>/<c>ConditionalStep</c> (8.6).</item>
    /// <item>Validation badges with click-to-fix from <see cref="SequenceValidator"/> (8.7).</item>
    /// </list>
    /// Refreshes are event-driven via <see cref="SequenceChangeTracker"/>. The visualizer tree window
    /// remains as the lightweight runtime monitor.
    /// </remarks>
    public sealed class SequenceGraphWindow : EditorWindow
    {
        private SequenceController _selectedController;
        private SequenceGraphView _graphView;
        private SequenceChangeTracker _changeTracker;
        private readonly StepSelectionModel _selection = new StepSelectionModel();

        // Positions captured before a rebuild so node placement survives edit-mode hierarchy
        // changes within a session. Sprint 8.2 replaces this with a persisted sidecar store.
        private readonly Dictionary<Step, Vector2> _sessionPositions = new Dictionary<Step, Vector2>();

        private Label _emptyLabel;

        // Node inspector panel (Sprint 8.4): an IMGUI side panel that embeds the same StepEditor and
        // AuxiliaryBatchPanel the visualizer uses, so single/multi-node editing behaves identically.
        private IMGUIContainer _inspectorContainer;
        private UnityEditor.Editor _stepEditor;
        private readonly AuxiliaryBatchPanel _auxiliaryBatchPanel = new AuxiliaryBatchPanel();
        private Vector2 _inspectorScroll;

        // Play-mode overlay (Sprint 8.5): track the controller's current step to outline it, and
        // optionally pan to follow it as the sequence advances.
        private bool _followActiveStep = true;
        private Step _currentStep;
        private SequenceController _stepChangedHookController;

        /// <summary>Opens (or focuses) the sequence graph window.</summary>
        [MenuItem("Molca/Sequence/Sequence Graph", priority = 20)]
        public static void Open()
        {
            var window = GetWindow<SequenceGraphWindow>();
            window.titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Sequence Graph", "sequence");
            window.minSize = new Vector2(480, 320);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Sequence Graph", "sequence");
            _graphView = new SequenceGraphView { name = "sequence-graph" };
            _graphView.StepsSelected += OnGraphStepsSelected;
            _graphView.NodesMoved += OnNodesMoved;
            _graphView.EdgeReparentRequested += OnEdgeReparentRequested;
            _graphView.EdgeDisconnectRequested += OnEdgeDisconnectRequested;
            _graphView.CreateStepRequested += OnCreateStepRequested;
            _graphView.DuplicateStepsRequested += OnDuplicateStepsRequested;
            _graphView.DeleteStepsRequested += OnDeleteStepsRequested;

            // Shared design tokens on the root so the toolbar, hint, and any shared components
            // resolve the editor design language (Sprint 27.4).
            MolcaEditorUi.Apply(rootVisualElement);

            rootVisualElement.Add(BuildToolbar());

            // Horizontal split: graph fills, inspector docks right at a fixed width.
            var row = new VisualElement { name = "graph-row" };
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexGrow = 1;

            _graphView.style.flexGrow = 1;
            row.Add(_graphView);
            row.Add(BuildInspectorPanel());
            rootVisualElement.Add(row);

            _emptyLabel = new Label("Select a SequenceController in the scene to view its graph.")
            {
                name = "empty-hint"
            };
            _emptyLabel.style.position = Position.Absolute;
            _emptyLabel.style.top = 8;
            _emptyLabel.style.left = 8;
            _emptyLabel.style.color = MolcaEditorColors.Muted;
            rootVisualElement.Add(_emptyLabel);

            _selection.SelectionChanged += OnModelSelectionChanged;

            _changeTracker = new SequenceChangeTracker();
            _changeTracker.HierarchyChanged += RebuildGraph;
            _changeTracker.StepListChanged += RebuildGraph;
            _changeTracker.StepStatusChanged += OnStepStatusChanged;

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            TryAdoptControllerFromSelection();
            RebuildGraph();
        }

        private void OnDisable()
        {
            // Flush the latest layout before tearing down so an unsaved drag isn't lost on close.
            CaptureCurrentPositions();
            PersistPositions();

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            UnhookCurrentStepTracking();
            if (_stepEditor != null)
            {
                DestroyImmediate(_stepEditor);
                _stepEditor = null;
            }
            if (_changeTracker != null)
            {
                _changeTracker.HierarchyChanged -= RebuildGraph;
                _changeTracker.StepListChanged -= RebuildGraph;
                _changeTracker.StepStatusChanged -= OnStepStatusChanged;
                _changeTracker.Dispose();
                _changeTracker = null;
            }
            _selection.SelectionChanged -= OnModelSelectionChanged;
            if (_graphView != null)
            {
                _graphView.StepsSelected -= OnGraphStepsSelected;
                _graphView.NodesMoved -= OnNodesMoved;
                _graphView.EdgeReparentRequested -= OnEdgeReparentRequested;
                _graphView.EdgeDisconnectRequested -= OnEdgeDisconnectRequested;
                _graphView.CreateStepRequested -= OnCreateStepRequested;
                _graphView.DuplicateStepsRequested -= OnDuplicateStepsRequested;
                _graphView.DeleteStepsRequested -= OnDeleteStepsRequested;
            }
        }

        /// <summary>Switches the displayed controller and rebuilds the graph.</summary>
        public void SelectController(SequenceController controller)
        {
            if (_selectedController == controller) return;
            _selectedController = controller;
            LoadPersistedPositions();
            _selection.Clear();
            _changeTracker.SetController(controller);
            RebuildGraph();
        }

        private void RebuildGraph()
        {
            if (_graphView == null) return;
            CaptureCurrentPositions();
            _graphView.Rebuild(_selectedController, _sessionPositions);

            var steps = _selectedController != null
                ? _selectedController.GetComponentsInChildren<Step>(true).ToList()
                : new List<Step>();

            _changeTracker.SetKnownEditorSteps(steps);
            // Re-subscribe status events to the current step set (play mode highlight feed).
            _changeTracker.AttachSteps(steps);

            // Structural editing only when a controller is shown and we're not in play mode
            // (the runtime owns step state during play).
            _graphView.EditingEnabled = _selectedController != null && !Application.isPlaying;

            _emptyLabel.style.display = steps.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            // Re-project the surviving selection onto the freshly built nodes.
            _selection.PruneDestroyed();
            _graphView.SyncSelectionFromModel(_selection.Selected);

            // Play-mode overlay: show runtime controls and re-apply the current-step outline on the
            // freshly built nodes (8.5).
            _graphView.SetPlayMode(Application.isPlaying);
            HookCurrentStepTracking();

            RefreshValidation();
        }

        // --- Validation badges (Sprint 8.7) ---

        private void RefreshValidation()
        {
            _graphView.ClearAllValidation();
            if (_selectedController == null) return;

            var findings = SequenceValidator.Validate(_selectedController);
            foreach (var group in findings.Where(f => f.Step != null).GroupBy(f => f.Step))
            {
                if (_graphView.GetNode(group.Key) is not { } node) continue;

                var stepFindings = group.ToList();
                bool hasError = stepFindings.Any(f => f.Severity == SequenceFindingSeverity.Error);
                bool hasWarning = stepFindings.Any(f => f.Severity == SequenceFindingSeverity.Warning);
                string tooltip = string.Join("\n", stepFindings.Select(f => f.Message));

                node.SetValidation(hasError, hasWarning, tooltip, () => ShowValidationMenu(stepFindings));
            }
        }

        private void ShowValidationMenu(List<SequenceFinding> findings)
        {
            var menu = new GenericMenu();
            foreach (var finding in findings)
                menu.AddDisabledItem(new GUIContent(finding.Message));

            var fixable = findings.Where(f => f.HasFix).ToList();
            if (fixable.Count > 0)
            {
                menu.AddSeparator(string.Empty);
                foreach (var finding in fixable)
                {
                    foreach (var auxType in GetAuxiliaryTypes())
                    {
                        var capturedFinding = finding;
                        var capturedType = auxType;
                        menu.AddItem(
                            new GUIContent($"Fix auxiliary {finding.AuxiliaryIndex}/Assign {auxType.Name}"),
                            false, () => ApplyAuxiliaryFix(capturedFinding, capturedType));
                    }
                }
            }
            menu.ShowAsContext();
        }

        private void ApplyAuxiliaryFix(SequenceFinding finding, System.Type newType)
        {
            if (!SequenceValidator.TryFixBrokenAuxiliary(finding, newType)) return;

            // The YAML edit only takes effect after a scene reload (mirrors StepEditor's fix flow).
            string scenePath = finding.Step != null ? finding.Step.gameObject.scene.path : null;
            EditorApplication.delayCall += () =>
            {
                if (!string.IsNullOrEmpty(scenePath))
                    Molca.Editor.Utils.AuxiliaryTypeFixerUtility.PromptSceneReload(scenePath);
            };
        }

        private static IEnumerable<System.Type> GetAuxiliaryTypes() =>
            TypeCache.GetTypesDerivedFrom<Molca.Sequence.Auxiliary.StepAuxiliary>()
                .Where(t => !t.IsAbstract)
                .OrderBy(t => t.Name);

        private void CaptureCurrentPositions()
        {
            if (_graphView == null || _selectedController == null) return;
            foreach (var step in _selectedController.GetComponentsInChildren<Step>(true))
            {
                var node = _graphView.GetNode(step);
                if (node != null) _sessionPositions[step] = node.GetPosition().position;
            }
        }

        // --- Position persistence (Sprint 8.2) ---

        /// <summary>Seeds <see cref="_sessionPositions"/> from the persisted store, matched by Ref Id.</summary>
        private void LoadPersistedPositions()
        {
            _sessionPositions.Clear();
            if (_selectedController == null) return;

            var stored = StepGraphLayoutStore.Load(_selectedController.RefId);
            if (stored.Count == 0) return;

            foreach (var step in _selectedController.GetComponentsInChildren<Step>(true))
            {
                if (step == null || string.IsNullOrEmpty(step.RefId)) continue;
                if (stored.TryGetValue(step.RefId, out var pos)) _sessionPositions[step] = pos;
            }
        }

        /// <summary>Writes the current session positions to the persisted store, keyed by Ref Id.</summary>
        private void PersistPositions()
        {
            if (_selectedController == null || _sessionPositions.Count == 0) return;

            var byRefId = new Dictionary<string, Vector2>();
            foreach (var kvp in _sessionPositions)
            {
                var step = kvp.Key;
                if (step == null || string.IsNullOrEmpty(step.RefId)) continue;
                byRefId[step.RefId] = kvp.Value;
            }
            StepGraphLayoutStore.Save(_selectedController.RefId, byRefId);
        }

        private void OnNodesMoved()
        {
            CaptureCurrentPositions();
            PersistPositions();
        }

        // --- Toolbar + play-mode overlay (Sprint 8.5) ---

        private Toolbar BuildToolbar()
        {
            var toolbar = new Toolbar();
            var followToggle = new ToolbarToggle { text = "Follow active step", value = _followActiveStep };
            followToggle.RegisterValueChangedCallback(evt =>
            {
                _followActiveStep = evt.newValue;
                if (_followActiveStep) _graphView?.HighlightCurrentStep(_currentStep, true);
            });
            toolbar.Add(followToggle);
            return toolbar;
        }

        // Subscribes to the controller's current-step changes during play so the graph can outline
        // and follow the active step. Idempotent — safe to call on every rebuild.
        private void HookCurrentStepTracking()
        {
            UnhookCurrentStepTracking();
            if (!Application.isPlaying || _selectedController == null) return;

            _selectedController.OnStepChanged.AddListener(OnControllerStepChanged);
            _stepChangedHookController = _selectedController;
            OnControllerStepChanged(_selectedController.CurrentStep); // apply current immediately
        }

        private void UnhookCurrentStepTracking()
        {
            if (_stepChangedHookController == null) return;
            _stepChangedHookController.OnStepChanged.RemoveListener(OnControllerStepChanged);
            _stepChangedHookController = null;
        }

        private void OnControllerStepChanged(Step step)
        {
            _currentStep = step;
            _graphView?.HighlightCurrentStep(step, _followActiveStep);
        }

        // --- Node inspector panel (Sprint 8.4) ---

        private VisualElement BuildInspectorPanel()
        {
            var panel = new VisualElement { name = "inspector-panel" };
            panel.style.width = 340;
            panel.style.minWidth = 240;
            panel.style.borderLeftWidth = 1;
            panel.style.borderLeftColor = MolcaEditorColors.Border;
            panel.style.paddingLeft = 4;
            panel.style.paddingRight = 4;

            // IMGUI bridge: reuse the visualizer's exact StepEditor + AuxiliaryBatchPanel so the
            // graph and the tree edit steps identically (no second editor implementation).
            _inspectorContainer = new IMGUIContainer(DrawInspectorIMGUI) { name = "inspector-imgui" };
            _inspectorContainer.style.flexGrow = 1;
            panel.Add(_inspectorContainer);
            return panel;
        }

        private void DrawInspectorIMGUI()
        {
            var primary = _selection.Primary;
            int count = _selection.Count;

            if (primary == null)
            {
                EditorGUILayout.HelpBox("Select a step to edit, or right-click the canvas to add one.",
                    MessageType.Info);
                return;
            }

            // Multi-edit only when every selected step is the same concrete type (mirrors the
            // visualizer); otherwise edit the primary and rely on the batch panel for the rest.
            bool multiEdit = count > 1 && _selection.Selected.All(s => s != null && s.GetType() == primary.GetType());
            var targets = multiEdit
                ? _selection.Selected.Where(s => s != null).Cast<UnityEngine.Object>().ToArray()
                : new UnityEngine.Object[] { primary };

            bool targetsChanged = _stepEditor == null
                || _stepEditor.targets.Length != targets.Length
                || !targets.SequenceEqual(_stepEditor.targets);
            if (targetsChanged)
            {
                if (_stepEditor != null) DestroyImmediate(_stepEditor);
                _stepEditor = UnityEditor.Editor.CreateEditor(targets);
            }

            _inspectorScroll = EditorGUILayout.BeginScrollView(_inspectorScroll);
            if (_stepEditor != null) _stepEditor.OnInspectorGUI();

            // Batch auxiliary editing across a multi-step selection (edit mode only) — works across
            // mixed step types since auxiliaries are independent of the owning step's type.
            if (!Application.isPlaying && count > 1) _auxiliaryBatchPanel.Draw(_selection.Selected);
            EditorGUILayout.EndScrollView();
        }

        // --- Structural editing (Sprint 8.3) — all routed through StepEditingService (Undo-grouped).
        //     Each op mutates the hierarchy, which the change tracker observes and turns into a
        //     RebuildGraph, so these handlers don't redraw the canvas themselves.

        private void OnEdgeReparentRequested(Step newParent, Step child)
        {
            if (newParent == null || child == null) return;
            // ReparentSteps rejects parenting a step under its own subtree (logs + returns 0).
            StepEditingService.ReparentSteps(new[] { child }, newParent.transform);
        }

        private void OnEdgeDisconnectRequested(Step child)
        {
            if (child == null || _selectedController == null) return;
            // Removing a parent→child edge promotes the child back to a top-level (root) step.
            StepEditingService.ReparentSteps(new[] { child }, _selectedController.transform);
        }

        private void OnCreateStepRequested(System.Type stepType, Step parent)
        {
            if (_selectedController == null || stepType == null) return;
            var created = StepEditingService.AddStep(_selectedController, stepType, parent);
            if (created != null) _selection.Select(created);
        }

        private void OnDuplicateStepsRequested(IReadOnlyList<Step> steps)
        {
            if (steps == null || steps.Count == 0) return;
            var clones = StepEditingService.DuplicateSteps(steps);
            if (clones != null && clones.Count > 0) _selection.SelectMany(clones);
        }

        private void OnDeleteStepsRequested(IReadOnlyList<Step> steps)
        {
            if (steps == null || steps.Count == 0) return;
            // Drop the doomed steps (and their descendants) from the model before they're destroyed.
            foreach (var step in steps)
            {
                if (step != null) _selection.RemoveWithDescendants(step);
            }
            StepEditingService.RemoveSteps(steps);
        }

        // --- Selection sync (mirrors SequenceVisualizerWindow's contract) ---

        private void OnGraphStepsSelected(IReadOnlyList<Step> steps)
        {
            // GraphView is the source of this change; adopt it into the model, which then
            // pushes to Unity selection via OnModelSelectionChanged.
            _selection.SelectMany(steps);
        }

        private void OnModelSelectionChanged()
        {
            // Reflect in the canvas (guarded so it doesn't echo back as a graph selection),
            // then push to Unity's global selection.
            _graphView?.SyncSelectionFromModel(_selection.Selected);
            SyncSelectionToUnity();
            _inspectorContainer?.MarkDirtyRepaint();
        }

        private void SyncSelectionToUnity()
        {
            if (_selection.Count == 0)
            {
                _selection.MarkSyncingToUnity();
                Selection.activeGameObject = null;
                return;
            }
            var gos = _selection.Selected.Where(s => s != null).Select(s => s.gameObject).ToArray();
            if (gos.Length == 0) return;
            var primaryGO = _selection.Primary != null ? _selection.Primary.gameObject : gos[gos.Length - 1];
            _selection.MarkSyncingToUnity();
            Selection.objects = gos;
            Selection.activeGameObject = primaryGO;
        }

        private void OnSelectionChange()
        {
            // Selection originating in Unity (hierarchy click, etc.).
            if (_selection.TryConsumeUnitySyncGuard()) return;

            // No controller yet: adopt one if the user clicked a step elsewhere.
            if (_selectedController == null)
            {
                TryAdoptControllerFromSelection();
                if (_selectedController == null) return;
            }

            var steps = new List<Step>();
            Step activeStep = null;
            var activeGO = Selection.activeGameObject;
            foreach (var obj in Selection.objects)
            {
                if (obj is not GameObject go) continue;
                var step = go.GetComponent<Step>();
                if (step == null) continue;
                var controller = step.GetComponentInParent<SequenceController>();
                if (controller == _selectedController)
                {
                    steps.Add(step);
                    if (go == activeGO) activeStep = step;
                }
                else if (go == activeGO)
                {
                    // Active selection moved to a different controller: follow it.
                    SelectController(controller);
                    return;
                }
            }
            _selection.ReconcileFromUnity(steps, activeStep);
        }

        private void TryAdoptControllerFromSelection()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;
            var controller = go.GetComponentInParent<SequenceController>();
            if (controller != null) SelectController(controller);
        }

        // --- Play-mode status feed ---

        private void OnStepStatusChanged(Step step) => _graphView?.RefreshNodeStatus(step);

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Rebuild on entering/exiting play mode so nodes track the rebuilt runtime step set.
            if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.EnteredEditMode)
            {
                RebuildGraph();
            }
        }
    }
}
