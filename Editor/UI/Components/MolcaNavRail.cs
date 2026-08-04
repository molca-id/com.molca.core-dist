using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.UI.Components
{
    /// <summary>
    /// The editor's navigation rail: a search box over a nested tree of <see cref="MolcaNavRailNode"/>.
    /// </summary>
    /// <remarks>
    /// <para>One rail, everywhere. The Hub's Settings tab and the Docs workspace each grew their own copy of
    /// this — same <c>TreeView</c>, same filter, same foldout-to-persistence bridge, including the same
    /// hard-won note about the toggle's <c>userData</c> — while three other workspaces used a flat rail that
    /// could not nest or search at all. This is that shared implementation; the flat rail is retired, and a
    /// rail with no children is simply a tree whose nodes have none.</para>
    ///
    /// <para>The rail owns navigation and nothing else. It decides what is selected, what is expanded, and
    /// what survives the filter; the owner decides what a selection means, through
    /// <see cref="NodeSelected"/>.</para>
    ///
    /// <para>Styling comes from the Hub's sheet — <c>.molca-hub-rail</c>, <c>.molca-hub-search</c>,
    /// <c>.molca-hub-rail-tree</c>, <c>.molca-hub-rail-node</c> — so every rail renders identically without
    /// a second set of rules to keep in sync.</para>
    /// </remarks>
    public sealed class MolcaNavRail : VisualElement
    {
        /// <summary>Height of one row, matching the design language's rail row metric.</summary>
        private const float RowHeight = 24f;

        private readonly TreeView _tree;
        private readonly TextField _search;
        private readonly Label _searchPlaceholder;

        private readonly List<MolcaNavRailNode> _roots = new List<MolcaNavRailNode>();
        private readonly Dictionary<int, MolcaNavRailNode> _itemIdToNode = new Dictionary<int, MolcaNavRailNode>();
        private readonly Dictionary<string, int> _nodeIdToItemId = new Dictionary<string, int>();

        private readonly Func<HashSet<string>> _readExpanded;
        private readonly Action<IEnumerable<string>> _writeExpanded;

        private HashSet<string> _expanded;
        private bool _suppressSelection;
        private int _nextItemId;
        private string _filter;
        private MolcaNavRailNode _firstFilterMatch;

        /// <summary>Raised with the node whose row was chosen. Categories are handled internally.</summary>
        public event Action<MolcaNavRailNode> NodeSelected;

        /// <summary>
        /// Contributes extra roots while a filter is active, ahead of the authored ones.
        /// </summary>
        /// <remarks>
        /// For results that are worth finding but not worth a permanent row — the Hub uses it to offer
        /// workspace tabs from the search box, which unfiltered would just duplicate the toolbar.
        /// </remarks>
        public Func<string, IReadOnlyList<MolcaNavRailNode>> FilterOnlyRoots { get; set; }

        /// <summary>The currently selected node, or null.</summary>
        public MolcaNavRailNode SelectedNode { get; private set; }

        /// <summary>Creates a rail.</summary>
        /// <param name="searchPlaceholder">
        /// Placeholder shown in the empty search box. <c>null</c> omits the search box entirely, for an owner
        /// that already offers a richer search of its own — the Network workspace searches the whole catalog
        /// and navigates to what it finds, which a box that filtered ten view labels would only get in the
        /// way of. Two search fields in one rail is worse than either.
        /// </param>
        /// <param name="readExpanded">Reads the persisted expanded-node ids. Null keeps expansion in memory.</param>
        /// <param name="writeExpanded">Persists the expanded-node ids. Null keeps expansion in memory.</param>
        public MolcaNavRail(
            string searchPlaceholder = "Search",
            Func<HashSet<string>> readExpanded = null,
            Action<IEnumerable<string>> writeExpanded = null)
        {
            AddToClassList("molca-hub-rail");

            _readExpanded = readExpanded;
            _writeExpanded = writeExpanded;
            _expanded = readExpanded?.Invoke() ?? new HashSet<string>();

            if (searchPlaceholder != null)
            {
                _search = new TextField { name = "nav-rail-search" };
                _search.AddToClassList("molca-hub-search");
                _search.RegisterValueChangedCallback(evt => ApplyFilter(evt.newValue));
                _search.RegisterCallback<KeyDownEvent>(OnSearchKeyDown);

                _searchPlaceholder = new Label(searchPlaceholder) { pickingMode = PickingMode.Ignore };
                _searchPlaceholder.AddToClassList("molca-hub-search-placeholder");
                _search.Add(_searchPlaceholder);
                Add(_search);
            }

            _tree = new TreeView
            {
                fixedItemHeight = RowHeight,
                selectionType = SelectionType.Single,
                makeItem = MakeRow,
                bindItem = BindRow,
            };
            _tree.AddToClassList("molca-hub-rail-tree");
            _tree.style.flexGrow = 1;
            _tree.selectionChanged += OnSelectionChanged;
            Add(_tree);
        }

        /// <summary>Adds an element between the search box and the tree, e.g. a scope switcher.</summary>
        /// <param name="element">The element to insert.</param>
        public void AddHeader(VisualElement element)
        {
            if (element != null) Insert(0, element);
        }

        /// <summary>Replaces the rail's contents and rebuilds it, preserving the active filter.</summary>
        /// <param name="roots">The new root nodes.</param>
        public void SetRoots(IEnumerable<MolcaNavRailNode> roots)
        {
            _roots.Clear();
            if (roots != null) _roots.AddRange(roots);
            Rebuild();
        }

        /// <summary>Selects the row with <paramref name="nodeId"/>, clearing the filter if it hides it.</summary>
        /// <param name="nodeId">The node id to select.</param>
        /// <param name="notify">Whether to report the selection to the owner.</param>
        /// <remarks>
        /// The row is highlighted <i>without</i> notify and the owner is told directly, rather than letting
        /// <c>selectionChanged</c> carry it. Selecting a row inside a collapsed branch does not reliably
        /// raise that event — a fact both hand-rolled rails discovered and worked around the same way — so
        /// depending on it here would make deep links silently do nothing some of the time.
        /// </remarks>
        public void SelectNodeById(string nodeId, bool notify = true)
        {
            if (string.IsNullOrEmpty(nodeId)) return;

            if (!_nodeIdToItemId.ContainsKey(nodeId) && !string.IsNullOrEmpty(_filter))
            {
                // The row exists but the filter is hiding it, so clear the filter rather than silently
                // doing nothing — a deep link has to arrive somewhere the reader can see.
                _search?.SetValueWithoutNotify(string.Empty);
                UpdatePlaceholder();
                _filter = null;
                Rebuild();
            }

            if (!_nodeIdToItemId.TryGetValue(nodeId, out int itemId)) return;
            if (!_itemIdToNode.TryGetValue(itemId, out var node)) return;

            Highlight(itemId);
            SelectedNode = node;

            if (notify && node.IsLeaf) Deliver(node);
        }

        /// <summary>
        /// Re-marks a row as selected without reporting it, for when a rebuild has dropped the highlight but
        /// the detail on screen is still the right one.
        /// </summary>
        /// <param name="nodeId">The node id to re-mark. Ignored when it did not survive the filter.</param>
        public void ReassertSelection(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            if (!_nodeIdToItemId.TryGetValue(nodeId, out int itemId)) return;

            Highlight(itemId);
        }

        private void Highlight(int itemId)
        {
            _suppressSelection = true;
            try { _tree.SetSelectionByIdWithoutNotify(new[] { itemId }); }
            finally { _suppressSelection = false; }
        }

        /// <summary>Runs a command leaf, or reports a content leaf to the owner.</summary>
        /// <remarks>
        /// A command leaf is a jump, not a location, so it never reaches <see cref="NodeSelected"/> — which
        /// is what stops an owner persisting it as the row to restore next time.
        /// </remarks>
        private void Deliver(MolcaNavRailNode node)
        {
            if (node.Activate != null) node.Activate();
            else NodeSelected?.Invoke(node);
        }

        /// <summary>Clears the search box without raising a change.</summary>
        public void ClearSearch()
        {
            _search?.SetValueWithoutNotify(string.Empty);
            UpdatePlaceholder();
            _filter = null;
            Rebuild();
        }

        // ---- Filtering -------------------------------------------------------------------------------

        private void ApplyFilter(string filter)
        {
            _filter = filter;
            UpdatePlaceholder();
            Rebuild();
        }

        private void UpdatePlaceholder()
        {
            if (_searchPlaceholder == null || _search == null) return;
            _searchPlaceholder.style.display =
                string.IsNullOrEmpty(_search.value) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Enter in the search box activates the first surviving leaf, in rendered order.</summary>
        private void OnSearchKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            if (_firstFilterMatch == null) return;

            evt.StopPropagation();
            SelectNodeById(_firstFilterMatch.Id);
        }

        // ---- Tree construction -----------------------------------------------------------------------

        private void Rebuild()
        {
            _itemIdToNode.Clear();
            _nodeIdToItemId.Clear();
            _nextItemId = 0;

            var shown = new List<MolcaNavRailNode>();
            if (!string.IsNullOrEmpty(_filter))
            {
                var extra = FilterOnlyRoots?.Invoke(_filter);
                if (extra != null) shown.AddRange(extra);
            }
            shown.AddRange(_roots);

            var roots = new List<TreeViewItemData<MolcaNavRailNode>>();
            foreach (var node in shown)
            {
                var data = BuildItemData(node, _filter);
                if (data.HasValue) roots.Add(data.Value);
            }

            _firstFilterMatch = null;
            foreach (var node in shown)
            {
                var leaf = FirstVisibleLeaf(node, _filter);
                if (leaf == null) continue;
                _firstFilterMatch = leaf;
                break;
            }

            _suppressSelection = true;
            try
            {
                _tree.SetRootItems(roots);
                _tree.Rebuild();
                ApplyExpansion();
            }
            finally
            {
                _suppressSelection = false;
            }
        }

        /// <summary>
        /// Builds the filtered subtree for a node, or null when it and all its descendants are filtered out.
        /// </summary>
        /// <remarks>
        /// A node whose own label matches reveals its whole subtree — searching for a category should show
        /// what is in it, not an empty row bearing its name.
        /// </remarks>
        private TreeViewItemData<MolcaNavRailNode>? BuildItemData(MolcaNavRailNode node, string filter)
        {
            bool self = MolcaNavRailFilter.Matches(node.Label, filter);
            string childFilter = self ? null : filter;

            List<TreeViewItemData<MolcaNavRailNode>> children = null;
            foreach (var child in node.Children)
            {
                var data = BuildItemData(child, childFilter);
                if (!data.HasValue) continue;
                children ??= new List<TreeViewItemData<MolcaNavRailNode>>();
                children.Add(data.Value);
            }

            if (node.IsLeaf)
            {
                if (!self) return null;
            }
            else if (!self && children == null)
            {
                return null;
            }

            int id = _nextItemId++;
            _itemIdToNode[id] = node;
            _nodeIdToItemId[node.Id] = id;
            return new TreeViewItemData<MolcaNavRailNode>(id, node, children);
        }

        private static MolcaNavRailNode FirstVisibleLeaf(MolcaNavRailNode node, string filter)
        {
            bool self = MolcaNavRailFilter.Matches(node.Label, filter);
            if (node.IsLeaf) return self ? node : null;

            string childFilter = self ? null : filter;
            foreach (var child in node.Children)
            {
                var leaf = FirstVisibleLeaf(child, childFilter);
                if (leaf != null) return leaf;
            }

            return null;
        }

        // ---- Rows ------------------------------------------------------------------------------------

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-rail-node");

            var dot = new VisualElement { name = "status" };
            dot.AddToClassList("molca-status-dot");
            dot.style.display = DisplayStyle.None;
            row.Add(dot);

            var label = new Label { name = "label" };
            label.AddToClassList("molca-hub-rail-node__label");
            row.Add(label);

            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            var node = _tree.GetItemDataForIndex<MolcaNavRailNode>(index);
            element.userData = node;
            element.tooltip = node.Tooltip ?? string.Empty;

            var label = element.Q<Label>("label");
            if (label != null) label.text = node.Label;

            // Rows are recycled, so the dot's every class has to be reset on each bind, not just added.
            var dot = element.Q<VisualElement>("status");
            if (dot != null)
            {
                bool show = node.Status != MolcaStatusKind.None;
                dot.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                foreach (MolcaStatusKind kind in Enum.GetValues(typeof(MolcaStatusKind)))
                    dot.EnableInClassList(StatusClass(kind), show && kind == node.Status);
            }

            element.EnableInClassList("molca-hub-rail-node--category", !node.IsLeaf);
            WireFoldout(element, node);
        }

        private static string StatusClass(MolcaStatusKind kind) =>
            "molca-status-dot--" + kind.ToString().ToLowerInvariant();

        /// <summary>
        /// Bridges the TreeView's own foldout toggle to id-keyed expansion persistence.
        /// </summary>
        /// <remarks>
        /// The toggle is recycled across binds, so the callback is registered once and marked. <b>Never write
        /// the toggle's <c>userData</c></b> — TreeView stores the item id there and casts it, so borrowing the
        /// field corrupts the tree's own bookkeeping. Both hand-rolled copies of this carried that warning;
        /// it survives here because it is the kind of thing that gets rediscovered expensively.
        /// </remarks>
        private void WireFoldout(VisualElement element, MolcaNavRailNode node)
        {
            if (node.IsLeaf) return;

            var itemRow = element.parent?.parent;
            var toggle = itemRow?.Q<Toggle>(className: "unity-tree-view__item-toggle") ?? itemRow?.Q<Toggle>();
            if (toggle == null || toggle.ClassListContains("molca-foldout-wired")) return;

            toggle.AddToClassList("molca-foldout-wired");
            toggle.RegisterValueChangedCallback(evt =>
            {
                var target = evt.currentTarget as VisualElement;
                var contentRow = target?.parent?.Q(className: "molca-hub-rail-node");
                if (contentRow?.userData is not MolcaNavRailNode bound) return;

                if (evt.newValue) _expanded.Add(bound.Id);
                else _expanded.Remove(bound.Id);
                SaveExpanded();
            });
        }

        // ---- Selection and expansion -----------------------------------------------------------------

        private void OnSelectionChanged(IEnumerable<object> selected)
        {
            if (_suppressSelection) return;

            MolcaNavRailNode node = null;
            foreach (var obj in selected) { node = obj as MolcaNavRailNode; break; }
            if (node == null) return;

            if (!node.IsLeaf)
            {
                // Selecting a category is a request to open it, not to navigate somewhere.
                ToggleExpansion(node);
                return;
            }

            SelectedNode = node;
            Deliver(node);
        }

        private void ToggleExpansion(MolcaNavRailNode node)
        {
            if (!_nodeIdToItemId.TryGetValue(node.Id, out int itemId)) return;

            if (_tree.IsExpanded(itemId))
            {
                _tree.CollapseItem(itemId);
                _expanded.Remove(node.Id);
            }
            else
            {
                _tree.ExpandItem(itemId);
                _expanded.Add(node.Id);
            }

            SaveExpanded();
        }

        /// <summary>
        /// Applies persisted expansion, except while filtering, when everything surviving is shown open.
        /// </summary>
        /// <remarks>
        /// <para>A search result inside a collapsed category is a result the reader cannot see, which reads
        /// as the search having failed.</para>
        ///
        /// <para>An <i>empty</i> persisted set means first run, and first run opens every parent — not none.
        /// Both hand-rolled rails agreed on this and it is the only sensible default: a reader who has never
        /// expressed a preference should be shown what is there, not an accordion of closed rows.</para>
        /// </remarks>
        private void ApplyExpansion()
        {
            if (!string.IsNullOrEmpty(_filter))
            {
                _tree.ExpandAll();
                return;
            }

            _tree.CollapseAll();
            foreach (var pair in _itemIdToNode)
            {
                var node = pair.Value;
                if (node.IsLeaf) continue;
                if (_expanded.Count == 0 || _expanded.Contains(node.Id))
                    _tree.ExpandItem(pair.Key);
            }
        }

        private void SaveExpanded() => _writeExpanded?.Invoke(_expanded);
    }
}
