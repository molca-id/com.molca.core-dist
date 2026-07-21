using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Docs
{
    /// <summary>
    /// Self-contained two-pane reference-docs browser hosted as the Hub's right-anchored "Docs" workspace
    /// tab: an optional product switcher and a category/doc navigation tree on the left, the rendered
    /// Markdown on the right.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Docs/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: contributed as a <see cref="MolcaHubWorkspaceItem"/> by <see cref="DocsWorkspaceProvider"/>.
    /// Docs are grouped by product (<see cref="MolcaDocProduct"/> — Core, an SDK layer, a fork, the project);
    /// when more than one product ships docs a switcher appears and each product gets the whole rail to itself
    /// (so a large fork is never a second-class sub-branch of Core). With a single product the switcher is
    /// hidden. Selected product/doc and expanded categories persist per project via <see cref="MolcaEditorPrefs"/>.
    /// A <c>molca://doc/&lt;id&gt;</c> link navigates in-view via <see cref="NavigateTo"/>; an external deep-link
    /// (<see cref="MolcaHubWindow.OpenDoc"/>) hands off through <see cref="PendingDocId"/>, consumed once on
    /// construction (switching to the product that owns the target). Editor-only; main thread.
    /// </remarks>
    internal sealed class DocsWorkspaceView : VisualElement
    {
        private const string SelectedKey = "Molca.Hub.Docs.Selected";
        private const string ExpandedKey = "Molca.Hub.Docs.Expanded";
        private const string ProductKey = "Molca.Hub.Docs.Product";

        /// <summary>
        /// A doc id to select when the next Docs workspace view is built, set by an external deep-link
        /// (<see cref="MolcaHubWindow.OpenDoc"/>). Consumed once on construction, then cleared.
        /// </summary>
        internal static string PendingDocId;

        private readonly IReadOnlyList<MolcaDocProduct> _products;
        private string _currentProductKey;

        private readonly DropdownField _productField;
        private readonly TreeView _tree;
        private readonly Label _title;
        private readonly Label _description;
        private readonly VisualElement _content;
        private readonly TextField _search;
        private readonly Label _searchPlaceholder;

        // Navigation model + id maps, rebuilt on every (re)build/filter so selection and expansion can be
        // addressed by stable node id ("doccat:<name>" for categories, "doc:<id>" for doc leaves).
        private readonly List<DocNode> _roots = new List<DocNode>();
        private readonly Dictionary<int, DocNode> _itemIdToNode = new Dictionary<int, DocNode>();
        private readonly Dictionary<string, int> _nodeIdToItemId = new Dictionary<string, int>();
        private HashSet<string> _expanded;
        private bool _suppressSelection;
        private int _nextItemId;

        internal DocsWorkspaceView()
        {
            AddToClassList("molca-hub-docs-workspace");
            style.flexGrow = 1;

            _expanded = ReadExpanded();
            _products = MolcaDocsRegistry.GetProducts();
            _currentProductKey = ResolveInitialProductKey();

            var split = new TwoPaneSplitView(0, 220, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1;
            Add(split);

            // ---- left: (product switcher) + search + navigation tree (reuses the settings-rail styling) ----
            var rail = new VisualElement();
            rail.AddToClassList("molca-hub-rail");
            split.Add(rail);

            // A product switcher only earns its space once more than one documentation set is present.
            if (_products.Count > 1)
            {
                var labels = new List<string>(_products.Count);
                foreach (var product in _products) labels.Add(product.Label);

                _productField = new DropdownField { label = null, choices = labels };
                _productField.AddToClassList("molca-hub-docs-product");
                _productField.tooltip = "Documentation set";
                _productField.index = Mathf.Max(0, CurrentProductIndex());
                _productField.RegisterValueChangedCallback(_ => SwitchProduct(_productField.index));
                rail.Add(_productField);
            }

            _search = new TextField { name = "docs-search" };
            _search.AddToClassList("molca-hub-search");
            _searchPlaceholder = new Label("Search docs") { pickingMode = PickingMode.Ignore };
            _searchPlaceholder.AddToClassList("molca-hub-search-placeholder");
            _search.Add(_searchPlaceholder);
            _search.RegisterValueChangedCallback(evt => ApplyFilter(evt.newValue));
            rail.Add(_search);

            _tree = new TreeView
            {
                fixedItemHeight = 24,
                selectionType = SelectionType.Single,
                makeItem = MakeRow,
                bindItem = BindRow
            };
            _tree.AddToClassList("molca-hub-rail-tree");
            _tree.style.flexGrow = 1;
            _tree.selectionChanged += OnSelectionChanged;
            rail.Add(_tree);

            // ---- right: doc header + scrollable rendered body ----
            var scroll = new ScrollView();
            scroll.AddToClassList("molca-hub-detail-scroll");
            split.Add(scroll);

            var detail = new VisualElement();
            detail.AddToClassList("molca-hub-detail");
            scroll.Add(detail);

            var header = new VisualElement();
            header.AddToClassList("molca-hub-detail-header");
            var stack = new VisualElement();
            stack.AddToClassList("molca-hub-title-stack");
            _title = new Label();
            _title.AddToClassList("molca-hub-title");
            _description = new Label();
            _description.AddToClassList("molca-hub-muted");
            stack.Add(_title);
            stack.Add(_description);
            header.Add(stack);
            detail.Add(header);

            _content = new VisualElement();
            _content.AddToClassList("molca-hub-detail-content");
            detail.Add(_content);

            BuildNodes(_currentProductKey);
            RebuildTree(null);
            RestoreSelection();
        }

        /// <summary>Navigates the browser to a doc by its <see cref="MolcaDocEntry.Id"/> (in-view doc→doc link).</summary>
        /// <param name="docId">The target doc id.</param>
        internal void NavigateTo(string docId)
        {
            // A cross-link may point into another product's docs — switch the active product first if so.
            var owningKey = ProductKeyContaining(docId);
            if (owningKey != null && owningKey != _currentProductKey)
                SelectProductInField(owningKey);

            SelectNodeById("doc:" + docId);
        }

        // ---- Product selection --------------------------------------------------------------------

        /// <summary>Resolves the product to show first: the pending deep-link's owner, else the saved one, else the first.</summary>
        private string ResolveInitialProductKey()
        {
            if (!string.IsNullOrEmpty(PendingDocId))
            {
                var owning = ProductKeyContaining(PendingDocId);
                if (owning != null) return owning;
            }

            var saved = MolcaEditorPrefs.GetString(ProductKey, string.Empty);
            if (!string.IsNullOrEmpty(saved) && FindProduct(saved) != null) return saved;

            return _products.Count > 0 ? _products[0].Key : null;
        }

        private int CurrentProductIndex()
        {
            for (int i = 0; i < _products.Count; i++)
                if (string.Equals(_products[i].Key, _currentProductKey, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private MolcaDocProduct FindProduct(string key)
        {
            foreach (var product in _products)
                if (string.Equals(product.Key, key, StringComparison.OrdinalIgnoreCase))
                    return product;
            return null;
        }

        private string ProductKeyContaining(string docId)
        {
            if (string.IsNullOrEmpty(docId)) return null;
            foreach (var product in _products)
                foreach (var category in product.Categories)
                    foreach (var doc in category.Docs)
                        if (string.Equals(doc.Id, docId, StringComparison.OrdinalIgnoreCase))
                            return product.Key;
            return null;
        }

        /// <summary>Reflects a product change into the switcher (which drives <see cref="SwitchProduct"/>).</summary>
        private void SelectProductInField(string key)
        {
            if (_productField == null) { SwitchProduct(key); return; }
            var product = FindProduct(key);
            if (product != null) _productField.value = product.Label; // fires the value-changed → SwitchProduct
        }

        private void SwitchProduct(int index)
        {
            if (index < 0 || index >= _products.Count) return;
            SwitchProduct(_products[index].Key);
        }

        private void SwitchProduct(string key)
        {
            if (string.IsNullOrEmpty(key) || string.Equals(key, _currentProductKey, StringComparison.OrdinalIgnoreCase))
                return;

            _currentProductKey = key;
            MolcaEditorPrefs.SetString(ProductKey, key);

            // Reset the filter so the freshly shown product is fully browsable.
            _search?.SetValueWithoutNotify(string.Empty);
            if (_searchPlaceholder != null) _searchPlaceholder.style.display = DisplayStyle.Flex;

            BuildNodes(key);
            RebuildTree(null);
            RestoreSelection();
        }

        // ---- Navigation model ---------------------------------------------------------------------

        /// <summary>Builds the category→doc hierarchy for the given product.</summary>
        private void BuildNodes(string productKey)
        {
            _roots.Clear();
            var product = FindProduct(productKey);
            if (product == null) return;

            foreach (var category in product.Categories)
            {
                var categoryNode = new DocNode("doccat:" + category.Name, category.Name, null, category.Name);
                foreach (var doc in category.Docs)
                    categoryNode.Children.Add(new DocNode("doc:" + doc.Id, doc.Title, doc, category.Name));
                _roots.Add(categoryNode);
            }
        }

        // ---- Row make / bind ----------------------------------------------------------------------

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-rail-node");
            var label = new Label { name = "label" };
            label.AddToClassList("molca-hub-rail-node__label");
            row.Add(label);
            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            var node = _tree.GetItemDataForIndex<DocNode>(index);
            element.userData = node;
            var label = element.Q<Label>("label");
            if (label != null) label.text = node.Label;
            element.EnableInClassList("molca-hub-rail-node--category", !node.IsLeaf);
            WireFoldout(element, node);
        }

        // Bridges the TreeView's auto-created foldout toggle to id-keyed expansion persistence (mirrors
        // MolcaHubWindow.WireRailFoldout). The toggle is recycled across binds, so the callback is registered
        // once. NOTE: never write the toggle's userData — TreeView stores the item id there and casts it.
        private void WireFoldout(VisualElement element, DocNode node)
        {
            if (node.IsLeaf) return;
            var itemRow = element.parent?.parent;
            var toggle = itemRow?.Q<Toggle>(className: "unity-tree-view__item-toggle") ?? itemRow?.Q<Toggle>();
            if (toggle == null || toggle.ClassListContains("molca-foldout-wired")) return;

            toggle.AddToClassList("molca-foldout-wired");
            toggle.RegisterValueChangedCallback(evt =>
            {
                var t = evt.currentTarget as VisualElement;
                var contentRow = t?.parent?.Q(className: "molca-hub-rail-node");
                if (contentRow?.userData is DocNode n)
                {
                    if (evt.newValue) _expanded.Add(n.Id);
                    else _expanded.Remove(n.Id);
                    SaveExpanded();
                }
            });
        }

        // ---- Selection ----------------------------------------------------------------------------

        private void OnSelectionChanged(IEnumerable<object> selected)
        {
            if (_suppressSelection) return;

            DocNode node = null;
            foreach (var obj in selected) { node = obj as DocNode; break; }
            if (node == null) return;

            if (node.IsLeaf)
            {
                ShowDoc(node);
            }
            else if (_nodeIdToItemId.TryGetValue(node.Id, out var itemId))
            {
                // Selecting a category row toggles its expansion.
                if (_tree.IsExpanded(itemId)) { _tree.CollapseItem(itemId); _expanded.Remove(node.Id); }
                else { _tree.ExpandItem(itemId); _expanded.Add(node.Id); }
                SaveExpanded();
            }
        }

        private void ShowDoc(DocNode node)
        {
            if (_content == null || node?.Entry == null) return;

            _title.text = node.Label;
            _description.text = node.Description ?? string.Empty;

            _content.Clear();
            _content.Add(new MolcaDocViewer(node.Entry, NavigateTo));
            MolcaEditorPrefs.SetString(SelectedKey, node.Entry.Id);
        }

        /// <summary>Selects a node by its stable id, rebuilding unfiltered first if it is hidden by a filter.</summary>
        private void SelectNodeById(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || _tree == null) return;

            if (!_nodeIdToItemId.TryGetValue(nodeId, out var itemId))
            {
                // The node may be hidden by an active filter — clear it and rebuild so cross-navigation works.
                _search?.SetValueWithoutNotify(string.Empty);
                if (_searchPlaceholder != null) _searchPlaceholder.style.display = DisplayStyle.Flex;
                RebuildTree(null);
                if (!_nodeIdToItemId.TryGetValue(nodeId, out itemId)) return;
            }

            if (!_itemIdToNode.TryGetValue(itemId, out var node)) return;

            // Highlight the row without notifying, then drive content directly: selecting a row inside a
            // collapsed branch does not reliably fire selectionChanged, so we do not depend on it.
            _suppressSelection = true;
            try { _tree.SetSelectionByIdWithoutNotify(new[] { itemId }); }
            finally { _suppressSelection = false; }

            if (node.IsLeaf) ShowDoc(node);
        }

        /// <summary>Selects the pending deep-link target, else the persisted doc, else the first doc in the product.</summary>
        private void RestoreSelection()
        {
            var pending = PendingDocId;
            PendingDocId = null;

            string nodeId = null;
            if (!string.IsNullOrEmpty(pending) && _nodeIdToItemId.ContainsKey("doc:" + pending))
                nodeId = "doc:" + pending;

            if (nodeId == null)
            {
                var saved = MolcaEditorPrefs.GetString(SelectedKey, string.Empty);
                if (!string.IsNullOrEmpty(saved) && _nodeIdToItemId.ContainsKey("doc:" + saved))
                    nodeId = "doc:" + saved;
            }

            nodeId ??= FirstDocId();
            if (!string.IsNullOrEmpty(nodeId)) SelectNodeById(nodeId);
        }

        private string FirstDocId()
        {
            foreach (var root in _roots)
                foreach (var child in root.Children)
                    if (child.IsLeaf)
                        return child.Id;
            return null;
        }

        // ---- Tree build / filter ------------------------------------------------------------------

        /// <summary>Rebuilds the TreeView from <see cref="_roots"/>, applying an optional label filter.</summary>
        private void RebuildTree(string filter)
        {
            if (_tree == null) return;

            _itemIdToNode.Clear();
            _nodeIdToItemId.Clear();
            _nextItemId = 0;

            var roots = new List<TreeViewItemData<DocNode>>();
            foreach (var node in _roots)
            {
                var data = BuildItemData(node, filter);
                if (data.HasValue) roots.Add(data.Value);
            }

            _suppressSelection = true;
            try
            {
                _tree.SetRootItems(roots);
                _tree.Rebuild();
                ApplyExpansion(filter);
            }
            finally
            {
                _suppressSelection = false;
            }
        }

        // Builds the filtered subtree for a node, or null when it (and all descendants) are filtered out. A
        // category whose own name matches reveals all its docs.
        private TreeViewItemData<DocNode>? BuildItemData(DocNode node, string filter)
        {
            bool self = string.IsNullOrEmpty(filter)
                        || node.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            string childFilter = self ? null : filter;
            List<TreeViewItemData<DocNode>> children = null;
            foreach (var child in node.Children)
            {
                var data = BuildItemData(child, childFilter);
                if (!data.HasValue) continue;
                children ??= new List<TreeViewItemData<DocNode>>();
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
            return new TreeViewItemData<DocNode>(id, node, children);
        }

        private void ApplyExpansion(string filter)
        {
            // While filtering, expand everything so surviving matches are visible; otherwise honor the
            // persisted expansion set, defaulting to all-categories-expanded on first run (empty set).
            if (!string.IsNullOrEmpty(filter))
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

        private void ApplyFilter(string rawFilter)
        {
            var filter = (rawFilter ?? string.Empty).Trim();
            if (_searchPlaceholder != null)
                _searchPlaceholder.style.display = string.IsNullOrEmpty(filter) ? DisplayStyle.Flex : DisplayStyle.None;

            RebuildTree(string.IsNullOrEmpty(filter) ? null : filter);

            // Re-assert the persisted selection (without rebuilding content) if it survived the filter.
            var active = MolcaEditorPrefs.GetString(SelectedKey, string.Empty);
            if (!string.IsNullOrEmpty(active) && _nodeIdToItemId.TryGetValue("doc:" + active, out var itemId))
            {
                _suppressSelection = true;
                try { _tree.SetSelectionByIdWithoutNotify(new[] { itemId }); }
                finally { _suppressSelection = false; }
            }
        }

        // ---- Persistence --------------------------------------------------------------------------

        private static HashSet<string> ReadExpanded()
        {
            var raw = MolcaEditorPrefs.GetString(ExpandedKey, string.Empty);
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(raw)) return set;
            foreach (var part in raw.Split('\n'))
                if (!string.IsNullOrEmpty(part)) set.Add(part);
            return set;
        }

        private void SaveExpanded() => MolcaEditorPrefs.SetString(ExpandedKey, string.Join("\n", _expanded));

        /// <summary>One node in the docs navigation tree: a category parent or a doc leaf.</summary>
        private sealed class DocNode
        {
            internal string Id { get; }
            internal string Label { get; }
            internal string Description { get; }
            internal MolcaDocEntry Entry { get; }
            internal List<DocNode> Children { get; } = new List<DocNode>();
            internal bool IsLeaf => Entry != null;

            internal DocNode(string id, string label, MolcaDocEntry entry, string description)
            {
                Id = id;
                Label = label;
                Entry = entry;
                Description = description;
            }
        }
    }
}
