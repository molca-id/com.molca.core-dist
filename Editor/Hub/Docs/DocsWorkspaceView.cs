using System;
using System.Collections.Generic;
using System.IO;
using Molca.Editor.UI;
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
    /// <para>
    /// Staying current is this view's own job (see <see cref="Refresh"/>). The docs live under
    /// <c>Documentation~</c>, outside the AssetDatabase, so editing one fires no import callback and no domain
    /// reload — there is no event to subscribe to. Because the view also opts into
    /// <see cref="MolcaHubWorkspaceItem.CacheContent"/> it is not rebuilt on a tab switch either, so a
    /// front-matter edit would otherwise stay invisible until the next recompile. Re-activation re-resolves
    /// the tree, and the rail header carries a manual refresh for a reader who never left the tab.
    /// </para>
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

        private IReadOnlyList<MolcaDocProduct> _products;
        private string _currentProductKey;

        // Fingerprint of the tree _products was built from, so a refresh can tell a real change from a no-op.
        private string _signature;

        // The doc currently rendered in the detail pane, and the write time its Markdown had when rendered.
        // Together they answer "is what the reader is looking at still what is on disk?".
        private MolcaDocEntry _shownEntry;
        private DateTime _shownStamp;

        private DropdownField _productField;
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
            // A hostable view carries its own design language rather than inheriting the Hub's: the editor
            // design language allows this same element to be hosted standalone, and Apply is idempotent.
            MolcaEditorUi.Apply(this);
            AddToClassList("molca-hub-docs-workspace");
            style.flexGrow = 1;

            _products = MolcaDocsRegistry.GetProducts();
            _signature = MolcaDocsRegistry.Signature(_products);
            _currentProductKey = ResolveInitialProductKey();

            var split = new TwoPaneSplitView(0, 220, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1;
            Add(split);

            // ---- left: (product switcher) + the shared navigation rail ----
            _rail = new MolcaNavRail("Search docs", ReadExpanded, SaveExpanded);
            _rail.NodeSelected += ShowDoc;
            split.Add(_rail);

            // AddHeader inserts at the top, so this lands *below* the product switcher added next.
            _rail.AddHeader(BuildRefreshRow());

            // A product switcher only earns its space once more than one documentation set is present.
            SyncProductField();

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
        /// Re-reads the docs and consumes a deep-link target when this already-built view is shown again. The
        /// view opts into <see cref="MolcaHubWorkspaceItem.CacheContent"/>, so a second
        /// <see cref="MolcaHubWindow.OpenDoc"/> does not rebuild it — without this hook the pending id would
        /// never be read and the Hub would switch to Docs while still showing the previously selected page.
        /// </summary>
        /// <remarks>
        /// The pending id is taken <i>before</i> refreshing so <see cref="RestoreSelection"/> cannot consume it
        /// on the way through: a link into a product other than the current one would be swallowed silently.
        /// Refreshing first is what lets a link point at a doc authored since this view was built.
        /// </remarks>
        void IMolcaHubCachedView.OnWorkspaceActivated()
        {
            var pending = PendingDocId;
            PendingDocId = null;

            Refresh(force: false);

            if (!string.IsNullOrEmpty(pending)) NavigateTo(pending);
        }

        // ---- Staying current ----------------------------------------------------------------------

        /// <summary>
        /// Re-resolves the docs from disk and updates whatever actually changed: the product switcher and rail
        /// when the tree differs, the rendered body when its file was touched.
        /// </summary>
        /// <param name="force">
        /// When <c>true</c>, re-renders the shown doc even if nothing looks changed — what the rail's Refresh
        /// button means. An unchanged doc costs its scroll position, which is why activation passes <c>false</c>.
        /// </param>
        private void Refresh(bool force)
        {
            var products = MolcaDocsRegistry.GetProducts();
            var signature = MolcaDocsRegistry.Signature(products);

            if (string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                // Nothing the rail renders has changed, but the body may still have: an edit that only touches
                // prose leaves the fingerprint identical, so the file's own write time is what decides.
                if (force || ShownFileChanged()) RenderDoc(_shownEntry);
                return;
            }

            _products = products;
            _signature = signature;

            // The product being shown can be gone — its docs deleted, or its last doc's owner changed.
            if (FindProduct(_currentProductKey) == null)
                _currentProductKey = _products.Count > 0 ? _products[0].Key : null;

            SyncProductField();

            var shownId = _shownEntry?.Id;
            _shownEntry = null;
            BuildNodes(_currentProductKey);

            // Rebuilding the tree drops the highlight. When the shown doc survived, re-mark its row and
            // re-render it in place rather than routing through SelectNodeById, which would clear an active
            // search filter — a refresh must not undo the reader's search.
            if (shownId != null && _entriesByNodeId.TryGetValue("doc:" + shownId, out var entry))
            {
                RenderDoc(entry);
                _rail.ReassertSelection("doc:" + shownId);
                return;
            }

            RestoreSelection();
            if (_shownEntry == null) ClearDetail();
        }

        /// <summary>Whether the shown doc's Markdown was written since it was rendered.</summary>
        private bool ShownFileChanged() => _shownEntry != null && Stamp(_shownEntry) != _shownStamp;

        /// <summary>
        /// The doc's last-write time, or <see cref="DateTime.MinValue"/> when it has no readable file.
        /// </summary>
        /// <remarks>
        /// A provider may contribute a generated or remote doc whose <see cref="MolcaDocEntry.AbsolutePath"/>
        /// is not a file on disk. That yields a constant here rather than an exception, so such a doc simply
        /// never re-renders on its own — the Refresh button still forces it.
        /// </remarks>
        private static DateTime Stamp(MolcaDocEntry entry)
        {
            try { return File.GetLastWriteTimeUtc(entry.AbsolutePath); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>The rail-header row carrying the manual refresh.</summary>
        /// <remarks>
        /// Re-activation covers leaving and coming back; this covers the reader who never left — the common
        /// case while authoring a guide with the Hub open beside the editor.
        /// </remarks>
        private VisualElement BuildRefreshRow()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-docs-refresh");
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.FlexEnd;

            var button = MolcaButtons.Mini("Refresh", () => Refresh(force: true));
            button.tooltip = "Re-read the docs from disk.\n" +
                             "Documentation~ is outside the AssetDatabase, so editing a guide raises no import " +
                             "event — front-matter and new files need this or a trip to another tab.";
            row.Add(button);
            return row;
        }

        /// <summary>Empties the detail pane, for when no doc is left to show.</summary>
        private void ClearDetail()
        {
            _title.text = string.Empty;
            _description.text = string.Empty;
            _content.Clear();
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

        /// <summary>
        /// Brings the product switcher in line with the resolved products: creates it once more than one
        /// documentation set is present, updates its choices, and removes it when only one is left.
        /// </summary>
        /// <remarks>
        /// The selected label is written <i>without</i> notify: reflecting resolved state must not look like the
        /// reader picking a product, which would re-enter <see cref="SwitchProduct(string)"/> mid-refresh.
        /// </remarks>
        private void SyncProductField()
        {
            if (_products.Count <= 1)
            {
                if (_productField == null) return;
                _productField.RemoveFromHierarchy();
                _productField = null;
                return;
            }

            var labels = new List<string>(_products.Count);
            foreach (var product in _products) labels.Add(product.Label);

            if (_productField == null)
            {
                _productField = new DropdownField { label = null };
                _productField.AddToClassList("molca-hub-docs-product");
                _productField.tooltip = "Documentation set";
                _productField.RegisterValueChangedCallback(_ => SwitchProduct(_productField.index));
                _rail.AddHeader(_productField);
            }

            _productField.choices = labels;
            _productField.SetValueWithoutNotify(labels[Mathf.Clamp(CurrentProductIndex(), 0, labels.Count - 1)]);
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
            if (node == null) return;
            if (!_entriesByNodeId.TryGetValue(node.Id, out var entry)) return;
            RenderDoc(entry);
        }

        /// <summary>
        /// Renders a doc into the detail pane and records what is on screen.
        /// </summary>
        /// <param name="entry">The doc to render; <c>null</c> leaves the pane as it is.</param>
        /// <remarks>
        /// Header text comes from the entry rather than the rail node that led here, so a re-render triggered by
        /// a refresh — which has no node to hand — produces exactly the same header. The two agree by
        /// construction: a leaf's label is the entry's title and its description is the entry's category.
        /// </remarks>
        private void RenderDoc(MolcaDocEntry entry)
        {
            if (_content == null || entry == null) return;

            _title.text = entry.Title;
            _description.text = entry.Category ?? string.Empty;

            _content.Clear();
            _content.Add(new MolcaDocViewer(entry, NavigateTo));

            _shownEntry = entry;
            _shownStamp = Stamp(entry);
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
