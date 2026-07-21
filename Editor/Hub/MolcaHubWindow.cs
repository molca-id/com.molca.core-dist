using Molca.Editor.Icons;
using Molca.Editor.UI.Components;
using Molca.Editor.Hub.Sections;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Dockable UI Toolkit shell for the Molca Hub editor workspace.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>.
    /// Base class: <see cref="EditorWindow"/>.
    /// Registration: <c>Molca/Hub</c> menu item plus <see cref="Open"/>.
    /// This sprint establishes the native editor shell only; later Hub sections bind the actual
    /// settings data through <see cref="SerializedObject"/> and existing editor services.
    /// </remarks>
    public sealed class MolcaHubWindow : EditorWindow
    {
        private const string AssetDir = "Packages/com.molca.core/Editor/Hub/";

        private static readonly SectionInfo[] Sections =
        {
            new SectionInfo(MolcaHubSection.Project, "Project", "Project identity, project links, and logo configuration."),
            new SectionInfo(MolcaHubSection.BuildVersion, "Build & Version", "Semantic versioning, build profiles, and release sync."),
            new SectionInfo(MolcaHubSection.RuntimeGlobal, "Runtime & Global", "Runtime Manager, Global Settings modules, and bootstrap context."),
            new SectionInfo(MolcaHubSection.Editor, "Editor", "Editor-only helpers, Area Picker, and notification providers."),
            new SectionInfo(MolcaHubSection.Integrations, "Integrations", "External service connections and provider configuration."),
            new SectionInfo(MolcaHubSection.Tasks, "Tasks", "Your ClickUp tasks for this project's folder, with inline status changes."),
            new SectionInfo(MolcaHubSection.Mcp, "MCP", "MCP bridge, auth token, proxy, and tool provider settings."),
            new SectionInfo(MolcaHubSection.Network, "Network", "Live HTTP request counts, redacted request history, cache size, and streaming-provider status."),
            new SectionInfo(MolcaHubSection.Sequences, "Sequences", "Validation status of every SequenceController in the open scene(s)."),
            new SectionInfo(MolcaHubSection.Assistant, "Assistant", "In-editor chat assistant provider, model, and API key.")
        };

        private readonly List<Button> _workspaceButtons = new List<Button>();

        // Nested navigation rail (TreeView) state. _railRoots holds the built node hierarchy; the id maps are
        // rebuilt on every (re)build/filter so selection and expansion can be addressed by stable node id.
        private TreeView _railTree;
        private readonly List<MolcaHubRailNode> _railRoots = new List<MolcaHubRailNode>();
        private readonly Dictionary<int, MolcaHubRailNode> _itemIdToNode = new Dictionary<int, MolcaHubRailNode>();
        private readonly Dictionary<string, int> _nodeIdToItemId = new Dictionary<string, int>();
        private HashSet<string> _expandedNodeIds = new HashSet<string>();
        private bool _suppressRailSelection;
        private int _nextItemId;

        // Non-Settings workspace tabs resolved from the provider registry (Core's Doctor/Assistant/Sequence
        // plus any consumer-contributed tabs), rebuilt each time the shell is constructed.
        private IReadOnlyList<MolcaHubWorkspaceItem> _workspaceItems = System.Array.Empty<MolcaHubWorkspaceItem>();

        private MolcaHubState _state;
        private TextField _searchField;
        private Label _searchPlaceholder;
        private VisualElement _rail;
        private VisualElement _detailHeader;
        private Label _detailTitle;
        private Label _detailDescription;
        private VisualElement _detailContent;
        private VisualElement _settingsBody;
        private VisualElement _workspaceHost;
        private VisualElement _workspaceToolbar;

        /// <summary>Opens or focuses the Molca Hub window.</summary>
        [MenuItem("Molca/Hub", priority = 0)]
        public static void Open()
        {
            var window = GetWindow<MolcaHubWindow>();
            window.titleContent = MolcaEditorIcons.WindowTitle("Molca Hub");
            window.minSize = new Vector2(520, 360);
            window.Show();
        }

        /// <summary>
        /// Opens (or focuses) the Hub and switches it to the given workspace.
        /// </summary>
        /// <param name="workspace">The workspace tab to activate after the window is shown.</param>
        /// <remarks>
        /// Used by external entry points (e.g. the Assistant header's Settings button) that want to
        /// land the user on a specific Hub workspace rather than Unity's Project Settings. If the Hub's
        /// UI has not been built yet (<see cref="SelectWorkspace"/> needs the rail/buttons), the request
        /// is persisted via <see cref="MolcaHubState"/> so <see cref="CreateGUI"/> restores it.
        /// </remarks>
        internal static void Open(MolcaHubWorkspace workspace)
        {
            var window = GetWindow<MolcaHubWindow>();
            window.titleContent = MolcaEditorIcons.WindowTitle("Molca Hub");
            window.minSize = new Vector2(520, 360);
            window.Show();

            var workspaceId = MolcaHubState.WorkspaceId(workspace);
            if (window._state != null && window._workspaceButtons.Count > 0)
                window.SelectWorkspace(workspaceId);
            else
                MolcaHubState.Load().SetWorkspace(workspaceId); // CreateGUI restores this on first build
        }

        /// <summary>
        /// Opens (or focuses) the Hub, switches to the right-anchored Docs workspace, and selects the doc
        /// with the given <see cref="Docs.MolcaDocEntry.Id"/>.
        /// </summary>
        /// <param name="docId">The reference-doc id to navigate to (e.g. from a <c>molca://doc/&lt;id&gt;</c> link).</param>
        /// <remarks>
        /// The target is stashed on <see cref="Docs.DocsWorkspaceView.PendingDocId"/> and consumed when the
        /// docs view is (re)built — whether that happens now (UI already up) or later from
        /// <see cref="CreateGUI"/> restoring the persisted workspace.
        /// </remarks>
        internal static void OpenDoc(string docId)
        {
            Docs.DocsWorkspaceView.PendingDocId = docId;

            var window = GetWindow<MolcaHubWindow>();
            window.titleContent = MolcaEditorIcons.WindowTitle("Molca Hub");
            window.minSize = new Vector2(520, 360);
            window.Show();

            if (window._state != null && window._workspaceButtons.Count > 0)
                window.SelectWorkspace(Docs.DocsWorkspaceProvider.WorkspaceId);
            else
                MolcaHubState.Load().SetWorkspace(Docs.DocsWorkspaceProvider.WorkspaceId);
        }

        private void OnEnable()
        {
            titleContent = MolcaEditorIcons.WindowTitle("Molca Hub");
            MolcaHubWorkspaceRegistry.VisibilityChanged += RefreshWorkspaceToolbar;
        }

        private void OnDisable()
        {
            MolcaHubWorkspaceRegistry.VisibilityChanged -= RefreshWorkspaceToolbar;
        }

        /// <summary>Builds the initial Hub shell from package UXML/USS assets.</summary>
        public void CreateGUI()
        {
            _state = MolcaHubState.Load();
            _workspaceButtons.Clear();

            var root = rootVisualElement;
            root.Clear();

            // Shared design tokens first (applies the `molca-editor`/`molca-light` root classes and
            // loads MolcaEditorTokens.uss), then the Hub-specific layout sheet that consumes them.
            Molca.Editor.UI.MolcaEditorUi.Apply(root);
            root.AddToClassList("molca-hub-root");

            var stylesheet = LoadAsset<StyleSheet>("MolcaHubWindow.uss");
            if (stylesheet != null) root.styleSheets.Add(stylesheet);

            var layout = LoadAsset<VisualTreeAsset>("MolcaHubWindow.uxml");
            if (layout != null)
            {
                layout.CloneTree(root);
            }
            else
            {
                BuildFallbackLayout(root);
            }

            _searchField = root.Q<TextField>("settings-search");
            _rail = root.Q<VisualElement>("settings-rail");
            _detailHeader = root.Q<VisualElement>("detail-header");
            _detailTitle = root.Q<Label>("detail-title");
            _detailDescription = root.Q<Label>("detail-description");
            _detailContent = root.Q<VisualElement>("detail-content");

            // Replace the placeholder "m" monogram in the detail header with the official Molca icon.
            var logoMark = root.Q<Label>(className: "molca-hub-logo-mark");
            var logoTile = logoMark?.parent;
            var brandIcon = MolcaEditorIcons.Window;
            if (brandIcon != null && logoTile != null)
            {
                logoMark.style.display = DisplayStyle.None;
                logoTile.style.backgroundImage = new StyleBackground(brandIcon);
                logoTile.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
            }

            // Full-bleed host for the non-Settings tool workspaces (Doctor/Assistant/Visualizer). The
            // Settings body and this host are mutually exclusive; clearing the host detaches the hosted
            // view, which is how each tool view cleans up (DetachFromPanelEvent).
            _settingsBody = root.Q<VisualElement>("hub-body");
            _workspaceHost = new VisualElement();
            _workspaceHost.AddToClassList("molca-hub-workspace-host");
            _workspaceHost.style.display = DisplayStyle.None;
            root.Add(_workspaceHost);

            _workspaceToolbar = root.Q<VisualElement>("workspace-toolbar");
            _workspaceItems = MolcaHubWorkspaceRegistry.GetWorkspaces();

            BuildWorkspaceToolbar(_workspaceToolbar);
            BuildSettingsRail();
            SelectWorkspace(_state.Workspace);
        }

        private static T LoadAsset<T>(string fileName) where T : UnityEngine.Object =>
            AssetDatabase.LoadAssetAtPath<T>(AssetDir + fileName);

        private void BuildWorkspaceToolbar(VisualElement toolbar)
        {
            if (toolbar == null) return;

            // Settings is the anchored home tab (Core-owned, always first). Every other tab — Core's own
            // Doctor/Assistant/Sequence and any consumer-contributed workspace — comes from the registry.
            // Left-aligned tabs sit before the flexible spacer; right-anchored tabs (e.g. Docs) after it.
            toolbar.Add(BuildToolbarToggle(MolcaHubWorkspaceRegistry.SettingsId, "Settings"));
            foreach (var item in _workspaceItems)
                if (!item.RightAnchored)
                    toolbar.Add(BuildToolbarToggle(item.Id, item.Label));

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            toolbar.Add(spacer);

            foreach (var item in _workspaceItems)
                if (item.RightAnchored)
                    toolbar.Add(BuildToolbarToggle(item.Id, item.Label));
        }

        private void RefreshWorkspaceToolbar()
        {
            if (_workspaceToolbar == null || _state == null) return;

            _workspaceItems = MolcaHubWorkspaceRegistry.GetWorkspaces();
            _workspaceButtons.Clear();
            _workspaceToolbar.Clear();
            BuildWorkspaceToolbar(_workspaceToolbar);
            SelectWorkspace(_state.Workspace);
        }

        private Button BuildToolbarToggle(string workspaceId, string label)
        {
            var button = new Button(() => SelectWorkspace(workspaceId)) { text = label };
            button.AddToClassList("molca-hub-workspace-tab");
            button.userData = workspaceId;
            _workspaceButtons.Add(button);
            return button;
        }

        private void BuildSettingsRail()
        {
            if (_rail == null) return;

            _expandedNodeIds = _state.RailExpanded ?? new HashSet<string>();
            BuildRailNodes();

            _railTree = new TreeView
            {
                fixedItemHeight = 24,
                selectionType = SelectionType.Single,
                makeItem = MakeRailRow,
                bindItem = BindRailRow
            };
            _railTree.AddToClassList("molca-hub-rail-tree");
            _railTree.style.flexGrow = 1;
            _railTree.selectionChanged += OnRailSelectionChanged;
            _rail.Add(_railTree);

            RebuildRailTree(null);

            if (_searchField != null)
            {
                _searchField.tooltip = "Filter Molca Hub navigation.";
                _searchField.label = string.Empty;
                _searchField.SetValueWithoutNotify(string.Empty);
                _searchPlaceholder = new Label("Search");
                _searchPlaceholder.pickingMode = PickingMode.Ignore;
                _searchPlaceholder.AddToClassList("molca-hub-search-placeholder");
                _searchField.Add(_searchPlaceholder);
                _searchField.RegisterValueChangedCallback(evt => ApplyRailFilter(evt.newValue));
            }
        }

        // ---- Rail node model ----------------------------------------------------------------------

        /// <summary>Builds the settings rail hierarchy: grouped, editable settings sections.</summary>
        /// <remarks>
        /// Reference docs are deliberately not part of this rail — they are read-only content hosted in their
        /// own right-anchored "Docs" workspace tab (<see cref="Docs.DocsWorkspaceView"/>), not the editable
        /// Settings surface.
        /// </remarks>
        private void BuildRailNodes()
        {
            _railRoots.Clear();

            _railRoots.Add(Category("cat:framework", "Framework",
                SectionLeaf(MolcaHubSection.Project),
                SectionLeaf(MolcaHubSection.BuildVersion),
                SectionLeaf(MolcaHubSection.RuntimeGlobal),
                SectionLeaf(MolcaHubSection.Editor)));

            _railRoots.Add(Category("cat:tooling", "Tooling",
                SectionLeaf(MolcaHubSection.Integrations),
                SectionLeaf(MolcaHubSection.Tasks),
                SectionLeaf(MolcaHubSection.Mcp),
                SectionLeaf(MolcaHubSection.Network),
                SectionLeaf(MolcaHubSection.Sequences)));

            _railRoots.Add(SectionLeaf(MolcaHubSection.Assistant));
        }

        private static MolcaHubRailNode Category(string id, string label, params MolcaHubRailNode[] children)
            => new MolcaHubRailNode(id, label, new List<MolcaHubRailNode>(children));

        private MolcaHubRailNode SectionLeaf(MolcaHubSection section)
        {
            var info = FindSection(section);
            return new MolcaHubRailNode(section.ToString(), info.Label, () => CreateSectionContent(section), info.Description);
        }

        // ---- Rail TreeView build / bind / selection -----------------------------------------------

        private VisualElement MakeRailRow()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-rail-node");
            var label = new Label { name = "label" };
            label.AddToClassList("molca-hub-rail-node__label");
            row.Add(label);
            return row;
        }

        private void BindRailRow(VisualElement element, int index)
        {
            var node = _railTree.GetItemDataForIndex<MolcaHubRailNode>(index);
            element.userData = node;
            var label = element.Q<Label>("label");
            if (label != null) label.text = node.Label;
            element.EnableInClassList("molca-hub-rail-node--category", !node.IsLeaf);
            WireRailFoldout(element, node);
        }

        // Bridges the TreeView's auto-created foldout toggle to id-keyed expansion persistence. The toggle is
        // recycled across binds, so the callback is registered once. NOTE: never write the toggle's userData —
        // TreeView stores the item id there and casts it internally.
        private void WireRailFoldout(VisualElement element, MolcaHubRailNode node)
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
                if (contentRow?.userData is MolcaHubRailNode n)
                {
                    if (evt.newValue) _expandedNodeIds.Add(n.Id);
                    else _expandedNodeIds.Remove(n.Id);
                    _state.SetRailExpanded(_expandedNodeIds);
                }
            });
        }

        private void OnRailSelectionChanged(IEnumerable<object> selected)
        {
            if (_suppressRailSelection) return;

            MolcaHubRailNode node = null;
            foreach (var obj in selected) { node = obj as MolcaHubRailNode; break; }
            if (node == null) return;

            if (node.IsLeaf)
            {
                ShowNode(node);
                _state.SetRailNode(node.Id);
            }
            else if (_nodeIdToItemId.TryGetValue(node.Id, out var itemId))
            {
                // Selecting a category row toggles its expansion.
                if (_railTree.IsExpanded(itemId)) { _railTree.CollapseItem(itemId); _expandedNodeIds.Remove(node.Id); }
                else { _railTree.ExpandItem(itemId); _expandedNodeIds.Add(node.Id); }
                _state.SetRailExpanded(_expandedNodeIds);
            }
        }

        private void ShowNode(MolcaHubRailNode node)
        {
            if (_detailContent == null || node?.CreateContent == null) return;
            _detailContent.Clear();

            // Every rail leaf is a settings section that renders its own header, so the shared detail header
            // stays hidden. (Reference docs, which used to show it, now live in their own workspace tab.)
            if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;

            try
            {
                _detailContent.Add(node.CreateContent());
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                var error = new Label($"Failed to open '{node.Label}': {ex.Message}");
                error.AddToClassList("molca-hub-muted");
                _detailContent.Add(error);
            }
        }

        /// <summary>Rebuilds the rail TreeView from <see cref="_railRoots"/>, applying an optional label filter.</summary>
        private void RebuildRailTree(string filter)
        {
            if (_railTree == null) return;

            _itemIdToNode.Clear();
            _nodeIdToItemId.Clear();
            _nextItemId = 0;

            var roots = new List<TreeViewItemData<MolcaHubRailNode>>();
            foreach (var node in _railRoots)
            {
                var data = BuildItemData(node, filter);
                if (data.HasValue) roots.Add(data.Value);
            }

            _suppressRailSelection = true;
            try
            {
                _railTree.SetRootItems(roots);
                _railTree.Rebuild();
                ApplyRailExpansion(filter);
            }
            finally
            {
                _suppressRailSelection = false;
            }
        }

        // Builds the filtered TreeViewItemData subtree for a node, or null when it (and all descendants) are
        // filtered out. A parent whose own label matches reveals its whole subtree.
        private TreeViewItemData<MolcaHubRailNode>? BuildItemData(MolcaHubRailNode node, string filter)
        {
            bool self = string.IsNullOrEmpty(filter)
                        || node.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            // Once this node matches, descendants are all included (pass a null/empty filter downward).
            string childFilter = self ? null : filter;
            List<TreeViewItemData<MolcaHubRailNode>> children = null;
            foreach (var child in node.Children)
            {
                var data = BuildItemData(child, childFilter);
                if (!data.HasValue) continue;
                children ??= new List<TreeViewItemData<MolcaHubRailNode>>();
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
            return new TreeViewItemData<MolcaHubRailNode>(id, node, children);
        }

        private void ApplyRailExpansion(string filter)
        {
            // While filtering, expand everything so surviving matches are visible; otherwise honor the
            // persisted expansion set, defaulting to all-parents-expanded on first run (empty set).
            if (!string.IsNullOrEmpty(filter))
            {
                _railTree.ExpandAll();
                return;
            }

            _railTree.CollapseAll();
            foreach (var pair in _itemIdToNode)
            {
                var node = pair.Value;
                if (node.IsLeaf) continue;
                if (_expandedNodeIds.Count == 0 || _expandedNodeIds.Contains(node.Id))
                    _railTree.ExpandItem(pair.Key);
            }
        }

        /// <summary>Selects a node by its stable id, rebuilding unfiltered first if it is not currently shown.</summary>
        private void SelectNodeById(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || _railTree == null) return;

            if (!_nodeIdToItemId.TryGetValue(nodeId, out var itemId))
            {
                // The node may be hidden by an active filter — clear it and rebuild so cross-navigation works.
                if (_searchField != null) _searchField.SetValueWithoutNotify(string.Empty);
                if (_searchPlaceholder != null) _searchPlaceholder.style.display = DisplayStyle.Flex;
                RebuildRailTree(null);
                if (!_nodeIdToItemId.TryGetValue(nodeId, out itemId)) return;
            }

            if (!_itemIdToNode.TryGetValue(itemId, out var node)) return;

            // Highlight the row without notifying, then drive the content directly. Selecting a row inside a
            // collapsed branch does not reliably fire selectionChanged, so we do not depend on it here.
            _suppressRailSelection = true;
            try { _railTree.SetSelectionByIdWithoutNotify(new[] { itemId }); }
            finally { _suppressRailSelection = false; }

            if (node.IsLeaf)
            {
                ShowNode(node);
                _state.SetRailNode(node.Id);
            }
        }

        /// <summary>Restores the persisted active rail node (falling back to the first leaf).</summary>
        private void RestoreRailSelection()
        {
            var target = _state.RailNode;
            if (string.IsNullOrEmpty(target) || !_nodeIdToItemId.ContainsKey(target))
                target = FirstLeafId();
            if (!string.IsNullOrEmpty(target)) SelectNodeById(target);
        }

        private string FirstLeafId()
        {
            foreach (var root in _railRoots)
            {
                var leaf = FirstLeaf(root);
                if (leaf != null) return leaf.Id;
            }
            return null;
        }

        private static MolcaHubRailNode FirstLeaf(MolcaHubRailNode node)
        {
            if (node.IsLeaf) return node;
            foreach (var child in node.Children)
            {
                var leaf = FirstLeaf(child);
                if (leaf != null) return leaf;
            }
            return null;
        }

        private void SelectWorkspace(string workspaceId)
        {
            // Fall back to the anchored Settings tab if the requested/persisted id is no longer registered
            // (e.g. a consumer hid it, or it came from a removed provider).
            bool isSettings = workspaceId == MolcaHubWorkspaceRegistry.SettingsId;
            MolcaHubWorkspaceItem item = null;
            if (!isSettings)
            {
                item = FindWorkspaceItem(workspaceId);
                if (item == null)
                {
                    workspaceId = MolcaHubWorkspaceRegistry.SettingsId;
                    isSettings = true;
                }
            }

            _state.SetWorkspace(workspaceId);

            foreach (var button in _workspaceButtons)
                button.EnableInClassList("molca-hub-workspace-tab--active", (string)button.userData == workspaceId);

            _railTree?.SetEnabled(isSettings);

            if (_settingsBody != null) _settingsBody.style.display = isSettings ? DisplayStyle.Flex : DisplayStyle.None;
            if (_workspaceHost != null) _workspaceHost.style.display = isSettings ? DisplayStyle.None : DisplayStyle.Flex;

            // Clearing detaches the previously hosted tool view, triggering its DetachFromPanelEvent
            // cleanup (cancel runs, dispose controllers) so no two hosts run the same tool at once.
            _workspaceHost?.Clear();

            if (isSettings)
            {
                RestoreRailSelection();
                return;
            }

            HostWorkspaceContent(item);
        }

        private MolcaHubWorkspaceItem FindWorkspaceItem(string workspaceId)
        {
            foreach (var item in _workspaceItems)
                if (item.Id == workspaceId)
                    return item;
            return null;
        }

        private void HostWorkspaceContent(MolcaHubWorkspaceItem item)
        {
            if (_workspaceHost == null || item?.CreateContent == null) return;

            // A consumer workspace that throws while building must not break the Hub shell — surface a
            // compact error in the host instead.
            try
            {
                var content = item.CreateContent();
                if (content != null) _workspaceHost.Add(content);
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                var error = new Label($"Failed to open '{item.Label}': {ex.Message}");
                error.AddToClassList("molca-hub-muted");
                _workspaceHost.Add(error);
            }
        }

        /// <summary>Cross-navigation entry point (e.g. from a section): selects the section's rail node.</summary>
        private void SelectSection(MolcaHubSection section) => SelectNodeById(section.ToString());

        /// <summary>Builds the detail view for a settings section leaf.</summary>
        private VisualElement CreateSectionContent(MolcaHubSection section) => section switch
        {
            MolcaHubSection.Project => new MolcaHubProjectSection(),
            MolcaHubSection.BuildVersion => new MolcaHubBuildVersionSection(_state),
            MolcaHubSection.RuntimeGlobal => new MolcaHubRuntimeSection(_state),
            MolcaHubSection.Editor => new MolcaHubEditorSection(),
            MolcaHubSection.Integrations => new MolcaHubIntegrationsSection(SelectSection),
            MolcaHubSection.Tasks => new MolcaHubTasksSection(SelectSection),
            MolcaHubSection.Mcp => new MolcaHubMcpSection(),
            MolcaHubSection.Network => new MolcaHubNetworkSection(),
            MolcaHubSection.Sequences => new MolcaHubSequencesSection(),
            MolcaHubSection.Assistant => new MolcaHubAssistantSection(),
            _ => new Label("Unknown section.")
        };

        private void ApplyRailFilter(string rawFilter)
        {
            var filter = (rawFilter ?? string.Empty).Trim();
            if (_searchPlaceholder != null)
                _searchPlaceholder.style.display = string.IsNullOrEmpty(filter) ? DisplayStyle.Flex : DisplayStyle.None;

            RebuildRailTree(string.IsNullOrEmpty(filter) ? null : filter);

            // Re-assert the active selection (without firing content rebuild) if it survived the filter.
            var active = _state.RailNode;
            if (!string.IsNullOrEmpty(active) && _nodeIdToItemId.TryGetValue(active, out var itemId))
            {
                _suppressRailSelection = true;
                try { _railTree.SetSelectionByIdWithoutNotify(new[] { itemId }); }
                finally { _suppressRailSelection = false; }
            }
        }

        private static SectionInfo FindSection(MolcaHubSection section)
        {
            foreach (var info in Sections)
                if (info.Section == section)
                    return info;

            return Sections[0];
        }

        private static void BuildFallbackLayout(VisualElement root)
        {
            var toolbar = new VisualElement { name = "workspace-toolbar" };
            toolbar.AddToClassList("molca-hub-workspace-toolbar");
            root.Add(toolbar);

            var body = new TwoPaneSplitView(0, 188, TwoPaneSplitViewOrientation.Horizontal);
            body.AddToClassList("molca-hub-body");
            root.Add(body);

            var railPanel = new VisualElement();
            railPanel.AddToClassList("molca-hub-rail");
            body.Add(railPanel);

            railPanel.Add(new TextField { name = "settings-search", value = string.Empty });
            railPanel.Add(new VisualElement { name = "settings-rail" });

            var detail = new VisualElement();
            detail.AddToClassList("molca-hub-detail");
            body.Add(detail);

            var detailHeader = new VisualElement { name = "detail-header" };
            detailHeader.AddToClassList("molca-hub-detail-header");
            detail.Add(detailHeader);

            detailHeader.Add(new Label { name = "detail-title", text = "Project" });
            detailHeader.Add(new Label { name = "detail-description" });

            var detailContent = new VisualElement { name = "detail-content" };
            detailContent.AddToClassList("molca-hub-detail-content");
            detail.Add(detailContent);
        }

        private readonly struct SectionInfo
        {
            internal readonly MolcaHubSection Section;
            internal readonly string Label;
            internal readonly string Description;

            internal SectionInfo(MolcaHubSection section, string label, string description)
            {
                Section = section;
                Label = label;
                Description = description;
            }
        }
    }
}
