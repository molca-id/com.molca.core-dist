using System.Collections.Generic;
using System.Linq;
using Molca.Editor.UI.Components;
using Molca.Sequence;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor
{
    /// <summary>
    /// Steps tree (UI Toolkit, Phase 3): a native <see cref="TreeView"/> replacing the hand-rolled
    /// IMGUI hierarchy. The tree is a pure view over the same services — selection lives in
    /// <see cref="StepSelectionModel"/>, search in <see cref="StepQueryFilter"/>, expansion is
    /// persisted by Ref Id (see <see cref="IsStepExpanded"/>). The model is the source of truth; the
    /// tree is rebuilt from it and pushes user clicks back through <see cref="OnTreeSelectionChanged"/>.
    /// </summary>
    public partial class SequenceVisualizerView
    {
        private MolcaSearchField _searchField;
        private Button _selectMatchingButton;
        private TreeView _tree;
        private HelpBox _treeEmptyBox;

        // Maps the TreeView item id (a step's instance id) back to the Step it represents. Rebuilt
        // on every structural rebuild; used to resolve selection/expansion by id.
        private readonly Dictionary<int, Step> _idToStep = new Dictionary<int, Step>();

        // Guards the bidirectional selection bridge: set while a tree-originated selection is being
        // written into the model (so the model's change notification does not push it straight back).
        private bool _suppressTreeSelectionCallback;

        private static GUIContent _treeErrorIcon;
        private static GUIContent _treeWarningIcon;

        /// <summary>Builds the left-pane tree surface: search sub-bar + the <see cref="TreeView"/>.</summary>
        private VisualElement BuildTreePane()
        {
            var pane = new VisualElement();
            pane.style.flexGrow = 1;
            // Match the left-chrome cards' horizontal inset so the tree aligns with the cards above it.
            pane.style.paddingLeft = 6;
            pane.style.paddingRight = 6;

            // A plain row (not a Toolbar): the MolcaSearchField is a block element taller than a
            // toolbar item, so a Toolbar clips it. Search flexes; actions stay compact.
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.marginTop = 4;
            bar.style.marginBottom = 4;

            _searchField = new MolcaSearchField("Search steps…");
            _searchField.style.flexGrow = 1;
            _searchField.style.marginLeft = 0;
            _searchField.style.marginRight = 4;
            _searchField.style.marginBottom = 0;
            // The inner TextField carries Unity's default 3px field margin; zero it so the search box
            // and tree rows line up flush with the chrome cards above rather than sitting further in.
            var searchInput = _searchField.Q<TextField>();
            if (searchInput != null)
            {
                searchInput.style.marginLeft = 0;
                searchInput.style.marginRight = 0;
            }
            _searchField.tooltip = "Filter steps. Operators: ref:  type:  aux:  status:  (see the ? toolbar button).";
            _searchField.OnSearchChanged += query =>
            {
                _searchFilter = query;
                SyncQueryFilter();
                RebuildTree();
                RefreshToolbarState();
                UpdateTreeBarState();
            };
            bar.Add(_searchField);

            // Only meaningful with an active query — hidden otherwise to save horizontal estate.
            _selectMatchingButton = MolcaButtons.Mini("Select Matching", SelectAllMatching);
            _selectMatchingButton.tooltip = "Select all steps matching the search query";
            bar.Add(_selectMatchingButton);

            // Icon-only expand/collapse (foldout triangles) to keep the narrow left pane uncluttered.
            bar.Add(IconButton(new[] { "IN foldout on", "d_IN foldout on", "d_Toolbar Plus" },
                "Expand all steps", ExpandAllSteps));
            bar.Add(IconButton(new[] { "IN foldout", "d_IN foldout", "d_Toolbar Minus" },
                "Collapse all steps", () => { ClearExpandedSteps(); Repaint(); }));

            pane.Add(bar);
            UpdateTreeBarState();

            _tree = new TreeView
            {
                selectionType = SelectionType.Multiple,
                fixedItemHeight = ROW_HEIGHT,
                makeItem = MakeTreeRow,
                bindItem = BindTreeRow,
            };
            _tree.style.flexGrow = 1;
            _tree.selectionChanged += OnTreeSelectionChanged;
            pane.Add(_tree);

            _treeEmptyBox = new HelpBox(string.Empty, HelpBoxMessageType.Info);
            _treeEmptyBox.style.display = DisplayStyle.None;
            _treeEmptyBox.style.marginTop = 6;
            pane.Add(_treeEmptyBox);

            pane.RegisterCallback<KeyDownEvent>(OnTreeKeyDown);
            return pane;
        }

        // Resolves the first non-null Unity built-in icon from the candidate names (editor icon names
        // vary by version/skin, so we fall back rather than render a blank button).
        private static Texture ResolveIcon(string[] candidates)
        {
            foreach (var name in candidates)
            {
                var tex = EditorGUIUtility.IconContent(name)?.image;
                if (tex != null) return tex;
            }
            return null;
        }

        // Compact icon-only mini button backed by a Unity built-in editor icon.
        private static Button IconButton(string[] iconCandidates, string tooltip, System.Action onClick)
        {
            var button = MolcaButtons.Mini(string.Empty, onClick);
            button.tooltip = tooltip;
            button.style.marginLeft = 2;
            button.style.width = 22;
            button.style.flexShrink = 0;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            button.style.paddingTop = 0;
            button.style.paddingBottom = 0;
            // Center the icon within the fixed-width button.
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;

            var icon = new Image
            {
                image = ResolveIcon(iconCandidates),
                scaleMode = ScaleMode.ScaleToFit,
                pickingMode = PickingMode.Ignore,
            };
            icon.style.width = 14;
            icon.style.height = 14;
            icon.style.marginLeft = 0;
            icon.style.marginRight = 0;
            button.Add(icon);
            return button;
        }

        // Shows "Select Matching" only while a query is active (it is meaningless with no filter).
        private void UpdateTreeBarState()
        {
            if (_selectMatchingButton == null) return;
            _selectMatchingButton.style.display =
                string.IsNullOrEmpty(_searchFilter) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        #region Row template

        private VisualElement MakeTreeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-step-row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.flexGrow = 1;
            // Clamp the row to its container so the flexible label truncates rather than widening the row
            // (which would push the right-anchored badge/action off position).
            row.style.width = Length.Percent(100);
            row.style.overflow = Overflow.Hidden;

            var dot = new VisualElement { name = "dot" };
            dot.AddToClassList("molca-status-dot");
            dot.style.flexShrink = 0;
            row.Add(dot);

            var label = new Label { name = "name", enableRichText = true };
            label.style.flexGrow = 1;
            label.style.flexShrink = 1;
            // flexBasis:0 + minWidth:0 let the flex item shrink below its content size so the label
            // ellipsizes instead of pushing the badge/action button off their fixed right-hand position.
            label.style.flexBasis = 0;
            label.style.minWidth = 0;
            label.style.marginLeft = 6;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(label);

            var badge = new Image { name = "badge", pickingMode = PickingMode.Position };
            badge.style.width = 16;
            badge.style.height = 16;
            badge.style.flexShrink = 0;
            badge.style.display = DisplayStyle.None;
            // Click the badge to open the per-step findings menu without selecting the row.
            badge.RegisterCallback<PointerDownEvent>(evt =>
            {
                var step = row.userData as Step;
                var findings = GetFindingsForStep(step);
                if (findings != null && findings.Count > 0) ShowFindingMenu(step, findings);
                evt.StopPropagation();
            });
            row.Add(badge);

            var action = new Button { name = "action" };
            action.AddToClassList("molca-mini-button");
            action.style.flexShrink = 0;
            action.style.marginLeft = 4;
            action.style.display = DisplayStyle.None;
            row.Add(action);

            return row;
        }

        private void BindTreeRow(VisualElement element, int index)
        {
            var step = _tree.GetItemDataForIndex<Step>(index);
            element.userData = step;
            if (step == null) return;

            var label = element.Q<Label>("name");
            label.text = GetFormattedStepName(step);

            var dot = element.Q("dot");
            dot.style.backgroundColor = GetStatusDotColor(step);

            BindBadge(element.Q<Image>("badge"), step);
            BindAction(element.Q<Button>("action"), step);
            WireFoldout(element, step);
        }

        private void BindBadge(Image badge, Step step)
        {
            var findings = GetFindingsForStep(step);
            if (findings == null || findings.Count == 0)
            {
                badge.style.display = DisplayStyle.None;
                badge.image = null;
                badge.tooltip = string.Empty;
                return;
            }

            if (_treeErrorIcon == null)
            {
                _treeErrorIcon = EditorGUIUtility.IconContent("console.erroricon.sml");
                _treeWarningIcon = EditorGUIUtility.IconContent("console.warnicon.sml");
            }

            bool hasError = findings.Any(f => f.Severity == SequenceFindingSeverity.Error);
            badge.image = (hasError ? _treeErrorIcon : _treeWarningIcon).image;
            badge.tooltip = string.Join("\n", findings.Select(f => "• " + f.Message));
            badge.style.display = DisplayStyle.Flex;
        }

        private void BindAction(Button action, Step step)
        {
            if (Application.isPlaying)
            {
                if (step.CurrentStatus == StepStatus.Active && !step.IsCompleted)
                {
                    bool blocked = !step.CanCompleteNow();
                    string reason = blocked ? step.GetCompletionBlockReason() : null;
                    action.text = "Complete";
                    action.tooltip = blocked
                        ? (string.IsNullOrEmpty(reason) ? "Completion condition not met yet" : reason)
                        : string.Empty;
                    action.style.backgroundColor = blocked ? ActiveColor : StyleKeyword.Null;
                    action.clickable = new Clickable(() => step.Complete());
                    action.style.display = DisplayStyle.Flex;
                }
                else if (step.CurrentStatus == StepStatus.Inactive)
                {
                    action.text = "Activate";
                    action.tooltip = string.Empty;
                    action.style.backgroundColor = StyleKeyword.Null;
                    action.clickable = new Clickable(() => step.SetStatus(StepStatus.Active));
                    action.style.display = DisplayStyle.Flex;
                }
                else
                {
                    action.style.display = DisplayStyle.None;
                }
            }
            else
            {
                action.text = "✕";
                action.tooltip = "Delete Step";
                action.style.backgroundColor = StyleKeyword.Null;
                action.clickable = new Clickable(() =>
                {
                    if (EditorUtility.DisplayDialog("Delete Step?",
                            $"Are you sure you want to delete the step '{step.name}' and all its children?",
                            "Delete", "Cancel"))
                    {
                        RemoveStep(step);
                    }
                });
                action.style.display = DisplayStyle.Flex;
            }
        }

        // Bridges the TreeView's auto-created foldout toggle to Ref-Id-keyed expansion persistence.
        // The toggle visual is recycled across binds, so the callback is registered once and reads the
        // currently-bound step from our row element. NOTE: never write the toggle's userData — TreeView
        // stores the item id (int) there and casts it internally (InvalidCastException otherwise).
        private void WireFoldout(VisualElement element, Step step)
        {
            var itemRow = element.parent?.parent;
            var toggle = itemRow?.Q<Toggle>(className: "unity-tree-view__item-toggle") ?? itemRow?.Q<Toggle>();
            if (toggle == null || toggle.ClassListContains("molca-foldout-wired")) return;

            toggle.AddToClassList("molca-foldout-wired");
            toggle.RegisterValueChangedCallback(evt =>
            {
                // Resolve the currently-bound step from our content row (NOT toggle.userData).
                var t = evt.currentTarget as VisualElement;
                var contentRow = t?.parent?.Q(className: "molca-step-row");
                if (contentRow?.userData is Step s)
                    SetStepExpanded(s, evt.newValue);
            });
        }

        /// <summary>Status-dot colour mirroring the old IMGUI indicator (play status / edit enablement).</summary>
        private Color GetStatusDotColor(Step step)
        {
            if (Application.isPlaying)
            {
                return step.CurrentStatus switch
                {
                    StepStatus.Active => ActiveColor,
                    StepStatus.Completed => CompletedColor,
                    _ => InactiveColor,
                };
            }
            return step.gameObject.activeInHierarchy && step.enabled ? Color.white : InactiveColor;
        }

        #endregion

        #region Structural rebuild & selection bridge

        /// <summary>
        /// Rebuilds the entire tree from the current model/hierarchy, applying filters, restoring
        /// expansion (by Ref Id) and selection (from the model). Cheap enough for play-mode refresh.
        /// </summary>
        private void RebuildTree()
        {
            if (_tree == null) return;

            if (!Application.isPlaying && _selectedController != null &&
                (_hierarchyDirty || StepHierarchyBuilder.NeedsHierarchyRebuild(_selectedController, _editorModeSteps)))
            {
                RefreshEditorHierarchy();
            }

            _idToStep.Clear();
            var roots = new List<TreeViewItemData<Step>>();

            var stepsList = Application.isPlaying ? _selectedController?.Steps : _editorModeSteps;
            bool hasSteps = _selectedController != null && stepsList != null && stepsList.Count > 0;
            if (hasSteps)
            {
                EnsureHierarchyBuilt(stepsList);
                foreach (var root in _cachedRootSteps)
                {
                    var data = BuildItemData(root);
                    if (data.HasValue) roots.Add(data.Value);
                }
            }

            _suppressTreeSelectionCallback = true;
            try
            {
                _tree.SetRootItems(roots);
                _tree.Rebuild();
                ApplyExpansion();
                PushSelectionToTree();
            }
            finally
            {
                _suppressTreeSelectionCallback = false;
            }

            UpdateTreeEmptyState(hasSteps, roots.Count);
        }

        // Builds the filtered TreeViewItemData subtree for a step, or null when it (and all of its
        // descendants) are filtered out. Mirrors the old DrawStepNode visibility logic.
        private TreeViewItemData<Step>? BuildItemData(Step step)
        {
            if (step == null) return null;

            bool matchesSearch = _queryFilter.IsEmpty || _queryFilter.Matches(step);
            bool childrenMatch = !_queryFilter.IsEmpty && HasChildMatchingSearch(step);

            if (Application.isPlaying)
            {
                if ((!_showCompletedSteps && step.IsCompleted) ||
                    (!_showInactiveSteps && step.CurrentStatus == StepStatus.Inactive)) return null;
            }
            else
            {
                if (!_showInactiveSteps && (!step.gameObject.activeInHierarchy || !step.enabled)) return null;
            }

            if (!matchesSearch && !childrenMatch) return null;

            List<TreeViewItemData<Step>> children = null;
            if (step.Children != null)
            {
                foreach (var child in step.Children)
                {
                    var data = BuildItemData(child);
                    if (!data.HasValue) continue;
                    children ??= new List<TreeViewItemData<Step>>();
                    children.Add(data.Value);
                }
            }

            int id = step.GetInstanceID();
            _idToStep[id] = step;
            return new TreeViewItemData<Step>(id, step, children);
        }

        private void ApplyExpansion()
        {
            _tree.CollapseAll();
            foreach (var pair in _idToStep)
            {
                if (IsStepExpanded(pair.Value)) _tree.ExpandItem(pair.Key);
            }
        }

        private void PushSelectionToTree()
        {
            var ids = _selection.Selected
                .Where(s => s != null && _idToStep.ContainsKey(s.GetInstanceID()))
                .Select(s => s.GetInstanceID())
                .ToList();
            _tree.SetSelectionByIdWithoutNotify(ids);
        }

        // TreeView -> model. Writes the tree's selection into the model; the suppression guard keeps
        // the model's resulting change notification from pushing the same selection straight back.
        private void OnTreeSelectionChanged(IEnumerable<object> items)
        {
            if (_suppressTreeSelectionCallback) return;

            _suppressTreeSelectionCallback = true;
            try
            {
                var steps = items.OfType<Step>().ToList();
                if (steps.Count == 0) _selection.Clear();
                else _selection.SelectMany(steps, steps[steps.Count - 1]);
                SyncSelectionToUnity();
                if (steps.Count > 0) EditorGUIUtility.PingObject(steps[steps.Count - 1].gameObject);
            }
            finally
            {
                _suppressTreeSelectionCallback = false;
            }
        }

        // Model -> tree. Light refresh wired to StepSelectionModel.SelectionChanged: updates the
        // dependent chrome/details and mirrors the model selection into the tree (unless the change
        // originated from the tree, in which case the tree already reflects it).
        private void OnSelectionModelChanged()
        {
            RefreshToolbarState();
            RefreshBody();
            RebuildDetails();
            if (!_suppressTreeSelectionCallback && _tree != null)
            {
                PushSelectionToTree();
                _tree.RefreshItems();
            }
        }

        private void UpdateTreeEmptyState(bool hasSteps, int rootCount)
        {
            if (!hasSteps)
            {
                _tree.style.display = DisplayStyle.None;
                _treeEmptyBox.style.display = DisplayStyle.Flex;
                _treeEmptyBox.messageType = HelpBoxMessageType.Info;
                _treeEmptyBox.text = Application.isPlaying
                    ? "Sequence not initialized or has no steps."
                    : "No Step components found in the controller's hierarchy.";
            }
            else if (rootCount == 0)
            {
                _tree.style.display = DisplayStyle.None;
                _treeEmptyBox.style.display = DisplayStyle.Flex;
                _treeEmptyBox.messageType = HelpBoxMessageType.Warning;
                _treeEmptyBox.text = Application.isPlaying
                    ? "No active root steps found. All steps appear to be children of other steps."
                    : "No active root steps found. Check that root steps are enabled and their GameObjects are active.";
            }
            else
            {
                _tree.style.display = DisplayStyle.Flex;
                _treeEmptyBox.style.display = DisplayStyle.None;
            }
        }

        #endregion

        #region Keyboard shortcuts (UITK)

        // UI Toolkit replacement for the old IMGUI HandleKeyboardShortcuts. Hosted on the tree pane.
        private void OnTreeKeyDown(KeyDownEvent e)
        {
            bool ctrlOrCmd = e.ctrlKey || e.commandKey;

            // Delete selected step(s) in edit mode.
            if (!Application.isPlaying && e.keyCode == KeyCode.Delete && _selection.Count > 0)
            {
                int n = _selection.Count;
                string msg = n == 1
                    ? $"Are you sure you want to delete the step '{GetPrimarySelectedStep().name}' and all its children?"
                    : $"Are you sure you want to delete {n} selected steps and their children?";
                if (EditorUtility.DisplayDialog("Delete Step(s)?", msg, "Delete", "Cancel"))
                {
                    RemoveSteps(new List<Step>(_selection.Selected));
                    e.StopPropagation();
                }
                return;
            }

            // Ctrl/Cmd + D duplicates the selection (edit mode only).
            if (!Application.isPlaying && ctrlOrCmd && e.keyCode == KeyCode.D && _selection.Count > 0)
            {
                DuplicateSelectedSteps();
                e.StopPropagation();
                return;
            }

            // Plain F frames the primary selected step in the hierarchy.
            if (e.keyCode == KeyCode.F && !ctrlOrCmd && GetPrimarySelectedStep() != null)
            {
                Selection.activeGameObject = GetPrimarySelectedStep().gameObject;
                EditorGUIUtility.PingObject(GetPrimarySelectedStep().gameObject);
                e.StopPropagation();
                return;
            }

            // Ctrl/Cmd + F focuses the search field.
            if (ctrlOrCmd && e.keyCode == KeyCode.F)
            {
                _searchField?.Q<TextField>()?.Focus();
                e.StopPropagation();
                return;
            }

            // Esc clears the search first; pressed again (or with no search), clears the selection.
            if (e.keyCode == KeyCode.Escape)
            {
                if (!string.IsNullOrEmpty(_searchFilter))
                {
                    _searchField?.Clear();
                    e.StopPropagation();
                }
                else if (_selection.Count > 0)
                {
                    _selection.Clear();
                    SyncSelectionToUnity();
                    e.StopPropagation();
                }
            }
        }

        #endregion
    }
}
