using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
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
    /// construction (switching to the product that owns the target) or, when this view is being reused from
    /// the workspace view cache, on re-activation. Editor-only; main thread.
    /// </remarks>
    internal sealed class DocsWorkspaceView : VisualElement, IMolcaHubCachedView
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
        private readonly MolcaNavRail _rail;
        private readonly Label _title;
        private readonly Label _description;
        private readonly VisualElement _content;

        // Node ids are stable and namespaced ("doccat:<name>" for categories, "doc:<id>" for doc leaves).
        // The rail addresses rows by those ids; this map is how a selected id gets back to its entry, since
        // the shared node model carries navigation data only.
        private readonly Dictionary<string, MolcaDocEntry> _entriesByNodeId =
            new Dictionary<string, MolcaDocEntry>(StringComparer.Ordinal);

        internal DocsWorkspaceView()
        {
            AddToClassList("molca-hub-docs-workspace");
            style.flexGrow = 1;

            _products = MolcaDocsRegistry.GetProducts();
            _currentProductKey = ResolveInitialProductKey();

            var split = new TwoPaneSplitView(0, 220, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1;
            Add(split);

            // ---- left: (product switcher) + the shared navigation rail ----
            _rail = new MolcaNavRail("Search docs", ReadExpanded, SaveExpanded);
            _rail.NodeSelected += ShowDoc;
            split.Add(_rail);

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
                _rail.AddHeader(_productField);
            }

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
            RestoreSelection();
        }

        /// <summary>
        /// Consumes a deep-link target when this already-built view is shown again. The view opts into
        /// <see cref="MolcaHubWorkspaceItem.CacheContent"/>, so a second <see cref="MolcaHubWindow.OpenDoc"/>
        /// does not rebuild it — without this hook the pending id would never be read and the Hub would
        /// switch to Docs while still showing the previously selected page.
        /// </summary>
        void IMolcaHubCachedView.OnWorkspaceActivated()
        {
            var pending = PendingDocId;
            PendingDocId = null;
            if (!string.IsNullOrEmpty(pending)) NavigateTo(pending);
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
            _rail.ClearSearch();

            BuildNodes(key);
            RestoreSelection();
        }

        // ---- Navigation model ---------------------------------------------------------------------

        /// <summary>Builds the category→doc hierarchy for the given product.</summary>
        private void BuildNodes(string productKey)
        {
            _entriesByNodeId.Clear();

            var roots = new List<MolcaNavRailNode>();
            var product = FindProduct(productKey);

            if (product != null)
            {
                foreach (var category in product.Categories)
                {
                    var children = new List<MolcaNavRailNode>();
                    foreach (var doc in category.Docs)
                    {
                        string nodeId = "doc:" + doc.Id;
                        _entriesByNodeId[nodeId] = doc;

                        // The rail renders no content of its own here — the detail pane is this view's, and
                        // NodeSelected drives it — so a leaf only has to be a leaf. An empty factory is what
                        // says "selecting this means something" without claiming to build the panel.
                        children.Add(new MolcaNavRailNode(
                            nodeId, doc.Title, () => null, category.Name));
                    }

                    roots.Add(new MolcaNavRailNode("doccat:" + category.Name, category.Name, children));
                }
            }

            _rail.SetRoots(roots);
        }

        // ---- Row make / bind ----------------------------------------------------------------------

        // Bridges the TreeView's auto-created foldout toggle to id-keyed expansion persistence (mirrors
        // MolcaHubWindow.WireRailFoldout). The toggle is recycled across binds, so the callback is registered
        // once. NOTE: never write the toggle's userData — TreeView stores the item id there and casts it.
        // ---- Selection ----------------------------------------------------------------------------

        private void ShowDoc(MolcaNavRailNode node)
        {
            if (_content == null || node == null) return;
            if (!_entriesByNodeId.TryGetValue(node.Id, out var entry) || entry == null) return;

            _title.text = node.Label;
            _description.text = node.Description ?? string.Empty;

            _content.Clear();
            _content.Add(new MolcaDocViewer(entry, NavigateTo));
            MolcaEditorPrefs.SetString(SelectedKey, entry.Id);
        }

        /// <summary>Selects a node by its stable id; the rail clears an active filter if it hides the row.</summary>
        private void SelectNodeById(string nodeId) => _rail.SelectNodeById(nodeId);

        /// <summary>Selects the pending deep-link target, else the persisted doc, else the first doc in the product.</summary>
        private void RestoreSelection()
        {
            var pending = PendingDocId;
            PendingDocId = null;

            string nodeId = null;
            if (!string.IsNullOrEmpty(pending) && _entriesByNodeId.ContainsKey("doc:" + pending))
                nodeId = "doc:" + pending;

            if (nodeId == null)
            {
                var saved = MolcaEditorPrefs.GetString(SelectedKey, string.Empty);
                if (!string.IsNullOrEmpty(saved) && _entriesByNodeId.ContainsKey("doc:" + saved))
                    nodeId = "doc:" + saved;
            }

            nodeId ??= FirstDocId();
            if (!string.IsNullOrEmpty(nodeId)) SelectNodeById(nodeId);
        }

        private string FirstDocId()
        {
            var product = FindProduct(_currentProductKey);
            if (product == null) return null;

            foreach (var category in product.Categories)
                foreach (var doc in category.Docs)
                    return "doc:" + doc.Id;

            return null;
        }

        // ---- Tree build / filter ------------------------------------------------------------------

        /// <summary>Rebuilds the TreeView from <see cref="_roots"/>, applying an optional label filter.</summary>
        // Builds the filtered subtree for a node, or null when it (and all descendants) are filtered out. A
        // category whose own name matches reveals all its docs.
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

        private static void SaveExpanded(IEnumerable<string> expanded) =>
            MolcaEditorPrefs.SetString(ExpandedKey, string.Join("\n", expanded));
    }
}
