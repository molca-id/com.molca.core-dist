using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using Molca.Editor.UI;

namespace Molca.Editor
{
    /// <summary>
    /// Tree-based sequence authoring and runtime monitoring view. Selection lives in
    /// <see cref="StepSelectionModel"/>, CRUD goes through <see cref="StepEditingService"/>,
    /// refresh is driven by <see cref="SequenceChangeTracker"/>. Split into partials:
    /// <c>.Toolbar</c> (toolbar, filters, shortcuts), <c>.Details</c> (right panel),
    /// <c>.StepManagement</c> (add/remove/change-type controls).
    /// </summary>
    /// <remarks>
    /// A reusable <see cref="VisualElement"/> hosted by both the standalone
    /// <see cref="SequenceVisualizerWindow"/> and the Molca Hub Sequence workspace (Sprint 26.10).
    /// The IMGUI body runs in an <see cref="IMGUIContainer"/>; <c>Repaint()</c> marks it dirty and
    /// lifecycle (change tracker, play-mode hook, window-state save) is keyed on attach/detach.
    /// </remarks>
    public partial class SequenceVisualizerView : VisualElement
    {
        private Action<string> _notify;

        /// <summary>Creates the view.</summary>
        /// <param name="notify">Optional transient-notification sink (e.g. the host window's
        /// <c>ShowNotification</c>); ignored when null.</param>
        public SequenceVisualizerView(Action<string> notify = null)
        {
            _notify = notify;
            style.flexGrow = 1;

            // Phase 1 (Sprint 44): the toolbar is native UI Toolkit; the tree/details body still runs
            // in the IMGUIContainer below and is migrated in later phases. Apply the shared design
            // tokens so the UITK chrome matches the Hub.
            MolcaEditorUi.Apply(this);
            Add(BuildToolbar());
            BuildBody();

            Initialize();
            Repaint();
            RegisterCallback<DetachFromPanelEvent>(_ => Teardown());
        }

        // Structural refresh: rebuilds the UITK tree from the model/hierarchy and resyncs the
        // event-driven toolbar, chrome cards, and the (still-IMGUI) details panel. Every edit,
        // selection, and play-mode transition funnels through here.
        private void Repaint()
        {
            RebuildTree();
            RefreshToolbarState();
            RefreshBody();
            RebuildDetails();
        }

        // Routes ShowNotification through the host (or no-ops when hosted without a sink).
        private void Notify(string message) => _notify?.Invoke(message);
        // --- Private Fields ---
        private SequenceController _selectedController;
        private bool _autoRefresh = true;
        private bool _showDetailedInfo = true;
        private SequenceChangeTracker _changeTracker;

        // Filters
        private bool _showCompletedSteps = true;
        private bool _showInactiveSteps = true;
        private string _searchFilter = "";
        // Parsed form of _searchFilter (aux:/ref:/status:/type: operators + free text),
        // rebuilt only when the raw string changes. Shared with the graph editor.
        private StepQueryFilter _queryFilter = new StepQueryFilter("");

        // Tree view state. Expand state is keyed on Ref Id (stable across domain reloads
        // and play-mode transitions, unlike instance ids) and serialized with the window.
        [SerializeField] private List<string> _expandedStepKeys = new List<string>();
        private HashSet<string> _expandedSet = new HashSet<string>();
        private readonly StepSelectionModel _selection = new StepSelectionModel();
        private UnityEditor.Editor _stepEditor;
        private readonly AuxiliaryBatchPanel _auxiliaryBatchPanel = new AuxiliaryBatchPanel();

        // Add/Remove Step state
        private List<System.Type> _stepTypes;
        private string[] _stepTypeNames;
        private int _selectedStepTypeIndex = 0;
        private int _changeStepTypeIndex = 0;

        // Editor mode data - This will now be rebuilt on demand.
        private List<Step> _editorModeSteps = new List<Step>();

        // --- Caching for performance ---
        private readonly Dictionary<int, string> _stepNameCache = new Dictionary<int, string>();
        private readonly Dictionary<int, Color> _stepColorCache = new Dictionary<int, Color>();
        private readonly Dictionary<int, string> _stepTypeCache = new Dictionary<int, string>();
        private List<Step> _cachedRootSteps = new List<Step>();

        // Validation findings, recomputed lazily when the hierarchy/list/status changes.
        private readonly Dictionary<Step, List<SequenceFinding>> _findingsByStep = new Dictionary<Step, List<SequenceFinding>>();
        private int _findingCount;
        private bool _validationDirty = true;

        private int _cachedActiveStepCount;
        private int _cachedCompletedStepCount;
        private bool _stepCountsValid;
        private bool _hierarchyDirty = true;
        private bool _hierarchyBuilt = false;

        // --- Constants ---
        private const float MIN_PANEL_WIDTH = 250f;

        // Tree row height (UITK TreeView.fixedItemHeight).
        private const float ROW_HEIGHT = 22f;

        // --- Colors ---
        // Drawn from the shared editor design tokens (MolcaEditorColors, Sprint 27.4) so status
        // coloring matches the graph editor and the rest of the editor, and tracks the active skin.
        private static Color ActiveColor => MolcaEditorColors.StatusWarn;    // Active step (amber)
        private static Color CompletedColor => MolcaEditorColors.StatusOk;   // Completed step (green)
        private static Color InactiveColor => MolcaEditorColors.Muted;       // Inactive / disabled
        private static Color SelectionColor                                  // Selection row tint
        {
            get { var c = MolcaEditorColors.RowSelected; c.a = 0.3f; return c; }
        }

        private void Initialize()
        {
            _expandedSet = new HashSet<string>(_expandedStepKeys);
            _selection.SelectionChanged += OnSelectionModelChanged;
            _changeTracker = new SequenceChangeTracker();
            _changeTracker.StepStatusChanged += OnTrackedStepStatusChanged;
            _changeTracker.StepListChanged += OnTrackedStepListChanged;
            _changeTracker.HierarchyChanged += OnTrackedHierarchyChanged;
            LoadWindowState();
            AutoSelectController();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            FindAllStepTypes();
        }

        private void Teardown()
        {
            SaveWindowState();
            _selection.SelectionChanged -= OnSelectionModelChanged;
            _changeTracker?.Dispose();
            _changeTracker = null;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            if (_stepEditor != null)
            {
                UnityEngine.Object.DestroyImmediate(_stepEditor);
            }
        }

        #region Window State Persistence

        // Project-scoped via MolcaEditorPrefs (Sprint 27.7). Previously used a local productGUID-based
        // key directly against unscoped EditorPrefs; MolcaEditorPrefs scopes by project, so the prefix
        // only needs to namespace this window. (One-time reset of persisted view state on upgrade.)
        private static string Key(string name) => "SequenceVisualizer." + name;

        private void LoadWindowState()
        {
            _autoRefresh = MolcaEditorPrefs.GetBool(Key("AutoRefresh"), true);
            _showDetailedInfo = MolcaEditorPrefs.GetBool(Key("ShowDetails"), true);
            _showCompletedSteps = MolcaEditorPrefs.GetBool(Key("ShowCompleted"), true);
            _showInactiveSteps = MolcaEditorPrefs.GetBool(Key("ShowInactive"), true);

            // Expand state previously rode on a [SerializeField]; this view persists it explicitly
            // (Ref Ids never contain a newline, so a newline-joined string round-trips safely).
            var expanded = MolcaEditorPrefs.GetString(Key("Expanded"), "");
            _expandedStepKeys = string.IsNullOrEmpty(expanded)
                ? new List<string>()
                : expanded.Split('\n').ToList();
            _expandedSet = new HashSet<string>(_expandedStepKeys);

            // Restore the selected controller across editor restarts via GlobalObjectId.
            if (_selectedController == null)
            {
                string saved = MolcaEditorPrefs.GetString(Key("Controller"), "");
                if (!string.IsNullOrEmpty(saved) && GlobalObjectId.TryParse(saved, out var id))
                {
                    var controller = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as SequenceController;
                    if (controller != null) SelectController(controller);
                }
            }
        }

        private void SaveWindowState()
        {
            MolcaEditorPrefs.SetBool(Key("AutoRefresh"), _autoRefresh);
            MolcaEditorPrefs.SetBool(Key("ShowDetails"), _showDetailedInfo);
            MolcaEditorPrefs.SetBool(Key("ShowCompleted"), _showCompletedSteps);
            MolcaEditorPrefs.SetBool(Key("ShowInactive"), _showInactiveSteps);
            MolcaEditorPrefs.SetString(Key("Expanded"), string.Join("\n", _expandedStepKeys));
            if (_selectedController != null)
            {
                MolcaEditorPrefs.SetString(Key("Controller"),
                    GlobalObjectId.GetGlobalObjectIdSlow(_selectedController).ToString());
            }
        }

        /// <summary>Ref Id when available (stable identity); instance id as fallback for unsaved steps.</summary>
        private static string GetExpandKey(Step step) =>
            string.IsNullOrEmpty(step.RefId) ? step.GetInstanceID().ToString() : step.RefId;

        private bool IsStepExpanded(Step step) => _expandedSet.Contains(GetExpandKey(step));

        private void SetStepExpanded(Step step, bool expanded)
        {
            string key = GetExpandKey(step);
            if (expanded ? _expandedSet.Add(key) : _expandedSet.Remove(key))
            {
                if (expanded) _expandedStepKeys.Add(key);
                else _expandedStepKeys.Remove(key);
            }
        }

        private void ClearExpandedSteps()
        {
            _expandedSet.Clear();
            _expandedStepKeys.Clear();
        }

        #endregion

        private void OnTrackedHierarchyChanged()
        {
            _hierarchyDirty = true;
            ClearCaches();
            Repaint();
        }

        private void OnTrackedStepListChanged()
        {
            // Runtime step list was built or resized — re-subscribe and redraw fully.
            _changeTracker.AttachSteps(_selectedController != null ? _selectedController.Steps : null);
            ClearCaches();
            if (_autoRefresh) Repaint();
        }

        private void OnTrackedStepStatusChanged(Step step)
        {
            // Invalidate only the affected step's cached presentation.
            if (step != null)
            {
                int id = step.GetInstanceID();
                _stepNameCache.Remove(id);
                _stepColorCache.Remove(id);
            }
            _stepCountsValid = false;
            if (_autoRefresh) Repaint();
        }

        private void ClearCaches()
        {
            _stepNameCache.Clear();
            _stepColorCache.Clear();
            _stepTypeCache.Clear();
            _cachedRootSteps.Clear();
            _stepCountsValid = false;
            _hierarchyBuilt = false; // Invalidate hierarchy cache
            _validationDirty = true; // Findings depend on the step set / status
        }

        /// <summary>
        /// Refreshes the editor hierarchy using the dedicated StepHierarchyBuilder utility.
        /// This constructs the Parent/Child relationships based on the Transform hierarchy.
        /// </summary>
        private void RefreshEditorHierarchy()
        {
            if (_selectedController == null || !_hierarchyDirty) return;

            // BuildHierarchy already builds the complete parent-child relationships
            // by traversing up the transform hierarchy to find parent steps
            _editorModeSteps = StepHierarchyBuilder.BuildHierarchy(_selectedController);
            _changeTracker?.SetKnownEditorSteps(_editorModeSteps);
            _hierarchyDirty = false;
            _hierarchyBuilt = false; // Force rebuild of cache
        }
        
        /// <summary>
        /// Ensures the hierarchy is built and root steps are cached.
        /// Only builds if not already built to avoid redundant work.
        /// </summary>
        private void EnsureHierarchyBuilt(IReadOnlyList<Step> stepsList)
        {
            if (_hierarchyBuilt) return;
            
            if (Application.isPlaying)
            {
                // In play mode, the controller's Steps already have parent-child relationships
                // We just need to cache the root steps
                _cachedRootSteps = StepHierarchyBuilder.GetRootSteps(stepsList);
            }
            else
            {
                // In editor mode, RefreshEditorHierarchy should have been called
                // but we cache root steps here if needed
                _cachedRootSteps = StepHierarchyBuilder.GetRootSteps(stepsList);
            }
            
            _hierarchyBuilt = true;
        }
        

        #region Helper Methods & Callbacks

        
        private string GetFormattedStepName(Step step)
        {
            var stepId = step.GetInstanceID();
            
            if (!_stepNameCache.TryGetValue(stepId, out var cachedName))
            {
                // An enabled step's name uses the skin-aware heading color (white-on-light would be
                // invisible under the light editor skin); the type/status suffixes use the muted token.
                Color baseColor = Application.isPlaying ? GetStepColorInternal(step) : (step.gameObject.activeInHierarchy && step.enabled ? MolcaEditorColors.Heading : InactiveColor);
                string colorHex = ColorUtility.ToHtmlStringRGB(baseColor);
                string mutedHex = ColorUtility.ToHtmlStringRGB(MolcaEditorColors.Muted);
                string typeName = GetStepTypeName(step);

                string name = $"<color=#{colorHex}>{step.name}</color> <color=#{mutedHex}><i>({typeName})</i></color>";

                if (Application.isPlaying)
                {
                    name += $" <color=#{mutedHex}><i>({step.CurrentStatus})</i></color>";
                }
                
                _stepNameCache[stepId] = name;
                cachedName = name;
            }
            
            return cachedName;
        }

        private Color GetStepColorInternal(Step step)
        {
            if (!Application.isPlaying) return InactiveColor;
            
            var stepId = step.GetInstanceID();
            if (!_stepColorCache.TryGetValue(stepId, out var color))
            {
                color = step.CurrentStatus switch
                {
                    StepStatus.Active => ActiveColor,
                    StepStatus.Completed => CompletedColor,
                    _ => InactiveColor
                };
                _stepColorCache[stepId] = color;
            }
            
            return color;
        }

        private string GetStepTypeName(Step step)
        {
            var stepId = step.GetInstanceID();
            if (!_stepTypeCache.TryGetValue(stepId, out var typeName))
            {
                typeName = step.GetType().Name;
                _stepTypeCache[stepId] = typeName;
            }
            return typeName;
        }

        private void SelectController(SequenceController controller)
        {
            _selectedController = controller;
            _changeTracker?.SetController(controller);
            if (Application.isPlaying)
            {
                _changeTracker?.AttachSteps(controller != null ? controller.Steps : null);
            }
            ClearExpandedSteps();
            _selection.Clear();
            ClearCaches();
            _hierarchyDirty = true;
            
            // Refresh editor mode data if needed
            if (!Application.isPlaying)
            {
                RefreshEditorHierarchy();
            }
            Repaint();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // When we exit play mode, we need to manually refresh the editor hierarchy
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                _changeTracker?.DetachSteps();
                _hierarchyDirty = true;
                RefreshEditorHierarchy();
            }
            _selection.Clear();
            ClearCaches();
            Repaint();
        }
        
        // Unchanged methods
        private void AutoSelectController()
        {
            if (_selectedController != null) return;
            var controllers = UnityEngine.Object.FindObjectsByType<SequenceController>(FindObjectsSortMode.None);
            if (controllers.Length > 0) SelectController(controllers[0]);
        }
        private void OnSelectionChange()
        {
            if (_selectedController == null) return;
            // When we just set selection from this window, don't overwrite the model (preserve order and multi-select)
            if (_selection.TryConsumeUnitySyncGuard())
            {
                Repaint();
                return;
            }
            var objects = Selection.objects;
            var steps = new List<Step>();
            GameObject activeGO = Selection.activeGameObject;
            Step activeStep = null;
            foreach (var obj in objects)
            {
                var go = obj as GameObject;
                if (go == null) continue;
                var step = go.GetComponent<Step>();
                var controller = step?.GetComponentInParent<SequenceController>();
                if (step != null && controller == _selectedController)
                {
                    steps.Add(step);
                    if (go == activeGO) activeStep = step;
                }
                else if (activeGO == go && step != null && controller != _selectedController)
                {
                    SelectController(controller);
                    return;
                }
            }
            _selection.ReconcileFromUnity(steps, activeStep);
        }
        private bool HasChildMatchingSearch(Step step)
        {
            if (step.Children == null || step.Children.Count == 0) return false;

            foreach (var child in step.Children)
            {
                if (_queryFilter.Matches(child)) return true;
                if (HasChildMatchingSearch(child)) return true;
            }

            return false;
        }

        /// <summary>Keeps <see cref="_queryFilter"/> in sync with the raw search string.</summary>
        private void SyncQueryFilter()
        {
            if (_queryFilter.Query != (_searchFilter ?? string.Empty))
            {
                _queryFilter = new StepQueryFilter(_searchFilter);
            }
        }

        /// <summary>
        /// Selects every step under the controller that matches the current query and pushes
        /// the selection to Unity so the batch panel picks it up. Expands parents so the
        /// matches are visible in the tree.
        /// </summary>
        private void SelectAllMatching()
        {
            if (_selectedController == null || _queryFilter.IsEmpty) return;

            var source = Application.isPlaying ? (IEnumerable<Step>)_selectedController.Steps : _editorModeSteps;
            if (source == null) return;

            var matches = _queryFilter.Filter(source).ToList();
            if (matches.Count == 0)
            {
                Notify("No steps match the query.");
                return;
            }

            _selection.SelectMany(matches, matches[matches.Count - 1]);
            foreach (var step in matches) ExpandParents(step);
            SyncSelectionToUnity();
            Repaint();
        }
        
        private void ExpandAllSteps()
        {
            var stepsList = Application.isPlaying ? _selectedController.Steps : _editorModeSteps;
            if (stepsList == null) return;
            
            foreach (var step in stepsList)
            {
                if (step != null)
                {
                    SetStepExpanded(step, true);
                }
            }

            Repaint();
        }
        
        private void ExpandParents(Step step)
        {
            if (step == null) return;
            
            // Walk up the transform hierarchy and expand all parent steps
            Transform current = step.transform.parent;
            while (current != null)
            {
                var parentStep = current.GetComponent<Step>();
                if (parentStep != null)
                {
                    SetStepExpanded(parentStep, true);
                }
                current = current.parent;
            }
        }
        
        private bool IsStepSelected(Step step)
        {
            return _selection.IsSelected(step);
        }

        private Step GetPrimarySelectedStep()
        {
            return _selection.Primary;
        }

        private void SyncSelectionToUnity()
        {
            if (_selection.Count == 0)
            {
                Selection.activeGameObject = null;
                return;
            }
            // Defer selection to next frame so Unity Hierarchy reliably shows multi-selection (setting from GUI callback can be ignored)
            var gos = _selection.Selected.Select(s => s.gameObject).ToArray();
            var primaryGO = GetPrimarySelectedStep()?.gameObject ?? gos[gos.Length - 1];
            EditorApplication.delayCall += () =>
            {
                if (gos == null || gos.Length == 0) return;
                _selection.MarkSyncingToUnity();
                Selection.objects = gos;
                Selection.activeGameObject = primaryGO;
            };
        }
        
        #endregion
    }
}