using Molca.Editor.Icons;
using Molca.Editor.UI.Components;
using Molca.Editor.Hub.Sections;
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
        private readonly List<Button> _sectionButtons = new List<Button>();

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
            _sectionButtons.Clear();

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
            BuildPlaceholderCard();
            SelectWorkspace(_state.Workspace);
        }

        private static T LoadAsset<T>(string fileName) where T : Object =>
            AssetDatabase.LoadAssetAtPath<T>(AssetDir + fileName);

        private void BuildWorkspaceToolbar(VisualElement toolbar)
        {
            if (toolbar == null) return;

            // Settings is the anchored home tab (Core-owned, always first). Every other tab — Core's own
            // Doctor/Assistant/Sequence and any consumer-contributed workspace — comes from the registry.
            toolbar.Add(BuildToolbarToggle(MolcaHubWorkspaceRegistry.SettingsId, "Settings"));
            foreach (var item in _workspaceItems)
                toolbar.Add(BuildToolbarToggle(item.Id, item.Label));

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            toolbar.Add(spacer);
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

            foreach (var section in Sections)
                AddRailItem(section);

            if (_searchField != null)
            {
                _searchField.tooltip = "Filter Molca Hub settings sections.";
                _searchField.label = string.Empty;
                _searchField.SetValueWithoutNotify(string.Empty);
                _searchPlaceholder = new Label("Search");
                _searchPlaceholder.pickingMode = PickingMode.Ignore;
                _searchPlaceholder.AddToClassList("molca-hub-search-placeholder");
                _searchField.Add(_searchPlaceholder);
                _searchField.RegisterValueChangedCallback(evt => ApplyRailFilter(evt.newValue));
            }
        }

        private void AddRailItem(SectionInfo section)
        {
            var row = new Button(() => SelectSection(section.Section)) { text = section.Label };
            row.AddToClassList("molca-hub-rail-item");
            row.userData = section;
            _sectionButtons.Add(row);
            _rail.Add(row);
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

            foreach (var button in _sectionButtons)
                button.SetEnabled(isSettings);

            if (_settingsBody != null) _settingsBody.style.display = isSettings ? DisplayStyle.Flex : DisplayStyle.None;
            if (_workspaceHost != null) _workspaceHost.style.display = isSettings ? DisplayStyle.None : DisplayStyle.Flex;

            // Clearing detaches the previously hosted tool view, triggering its DetachFromPanelEvent
            // cleanup (cancel runs, dispose controllers) so no two hosts run the same tool at once.
            _workspaceHost?.Clear();

            if (isSettings)
            {
                SelectSection(_state.Section);
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

        private void SelectSection(MolcaHubSection section)
        {
            _state.SetSection(section);
            var info = FindSection(section);

            foreach (var button in _sectionButtons)
            {
                var rowInfo = (SectionInfo)button.userData;
                button.EnableInClassList("molca-hub-rail-item--selected", rowInfo.Section == section);
            }

            if (_detailTitle != null) _detailTitle.text = info.Label;
            if (_detailDescription != null) _detailDescription.text = info.Description;
            if (section == MolcaHubSection.Project)
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;
                BuildProjectSection();
            }
            else if (section == MolcaHubSection.BuildVersion)
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;
                BuildBuildVersionSection();
            }
            else if (section == MolcaHubSection.RuntimeGlobal)
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;
                BuildRuntimeSection();
            }
            else if (section == MolcaHubSection.Editor)
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;
                BuildEditorSection();
            }
            else if (section == MolcaHubSection.Mcp)
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;
                BuildMcpSection();
            }
            else if (section == MolcaHubSection.Integrations)
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;
                BuildIntegrationsSection();
            }
            else if (section == MolcaHubSection.Tasks)
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;
                BuildTasksSection();
            }
            else if (section == MolcaHubSection.Network)
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;
                BuildNetworkSection();
            }
            else if (section == MolcaHubSection.Sequences)
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;
                BuildSequencesSection();
            }
            else if (section == MolcaHubSection.Assistant)
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.None;
                BuildAssistantSection();
            }
            else
            {
                if (_detailHeader != null) _detailHeader.style.display = DisplayStyle.Flex;
                BuildPlaceholderCard(info.Label, "Settings section placeholder", MolcaStatusKind.Idle, "Pending section");
            }
        }

        private void ApplyRailFilter(string rawFilter)
        {
            var filter = (rawFilter ?? string.Empty).Trim();
            if (_searchPlaceholder != null)
                _searchPlaceholder.style.display = string.IsNullOrEmpty(filter) ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (var button in _sectionButtons)
            {
                var info = (SectionInfo)button.userData;
                var matches = string.IsNullOrEmpty(filter)
                    || info.Label.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0
                    || info.Description.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0;
                button.style.display = matches ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void BuildPlaceholderCard(
            string title = "Implementation placeholder",
            string subtitle = "Sprint 26.1-26.3 foundation",
            MolcaStatusKind status = MolcaStatusKind.Idle,
            string statusText = "Layout shell")
        {
            if (_detailContent == null) return;

            _detailContent.Clear();

            var card = new MolcaSectionCard(
                title,
                subtitle,
                status,
                statusText,
                "Later sprint tasks replace this placeholder with section-specific settings views.");

            var message = new Label(
                "The Hub shell, persisted navigation state, searchable rail, and shared section-card component are ready. Section views will add serialized settings controls into this card body pattern.");
            message.AddToClassList("molca-hub-muted");
            card.Body.Add(message);

            _detailContent.Add(card);
        }

        private void BuildProjectSection()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            _detailContent.Add(new MolcaHubProjectSection());
        }

        private void BuildBuildVersionSection()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            _detailContent.Add(new MolcaHubBuildVersionSection(_state));
        }

        private void BuildRuntimeSection()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            _detailContent.Add(new MolcaHubRuntimeSection(_state));
        }

        private void BuildEditorSection()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            _detailContent.Add(new MolcaHubEditorSection());
        }

        private void BuildMcpSection()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            _detailContent.Add(new MolcaHubMcpSection());
        }

        private void BuildIntegrationsSection()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            _detailContent.Add(new MolcaHubIntegrationsSection(SelectSection));
        }

        private void BuildTasksSection()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            _detailContent.Add(new MolcaHubTasksSection(SelectSection));
        }

        private void BuildNetworkSection()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            _detailContent.Add(new MolcaHubNetworkSection());
        }

        private void BuildSequencesSection()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            _detailContent.Add(new MolcaHubSequencesSection());
        }

        private void BuildAssistantSection()
        {
            if (_detailContent == null) return;
            _detailContent.Clear();
            _detailContent.Add(new MolcaHubAssistantSection());
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
