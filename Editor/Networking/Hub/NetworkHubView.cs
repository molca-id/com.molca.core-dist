using System;
using System.Collections.Generic;
using Molca.Editor.Hub;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Hub.Views;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using Molca.Networking.Configuration;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// The Network Hub workspace: a toolbar, a navigation rail, and one view at a time over the project's
    /// <see cref="NetworkCatalog"/>.
    /// </summary>
    /// <remarks>
    /// A projection of the catalog and its validation report. The shell holds no resolution rule and no
    /// validation rule of its own — those live in <c>NetworkRouteResolver</c> and
    /// <c>NetworkCatalogValidator</c>, and every write goes through <c>NetworkCatalogEditingService</c>,
    /// so what the workspace shows, what a build enforces, and what a request does cannot drift apart.
    /// <para>
    /// The workspace never scans the project on open. Locating and validating the catalog is cheap;
    /// walking the <c>AssetDatabase</c> for legacy assets is not, so that happens only when a view asks.
    /// </para>
    /// <para>
    /// The tab opts into <c>cacheContent</c>, so this view is hidden rather than detached on a tab switch
    /// and attach fires exactly once. Per-activation work — consuming <see cref="PendingTarget"/> and
    /// re-reading a catalog another surface may have edited while this view was hidden — therefore lives in
    /// <see cref="IMolcaHubCachedView.OnWorkspaceActivated"/>, not in the attach handler.
    /// </para>
    /// </remarks>
    public sealed class NetworkHubView : VisualElement, IMolcaHubCachedView
    {
        private const string UssPath =
            "Packages/com.molca.core/Editor/Networking/Hub/NetworkHubView.uss";

        /// <summary>Width of the navigation rail, matching the Hub's other master/detail workspaces.</summary>
        private const int RailWidth = 188;

        /// <summary>Width below which the rail collapses into a horizontal strip above the content.</summary>
        private const float NarrowWidth = 640f;

        /// <summary>
        /// Where the workspace should navigate once it is built, set by <see cref="NetworkHubWorkspace"/>.
        /// </summary>
        /// <remarks>
        /// Static because the target is set before the view exists — the Hub builds the view when the tab
        /// is activated. Consumed and cleared on attach, so it cannot leak into a later activation.
        /// </remarks>
        internal static NetworkHubNavigationTarget PendingTarget { get; set; }

        private readonly NetworkHubSession _session = new NetworkHubSession();

        // Toolbar
        private Label _catalogLabel;
        private VisualElement _validationBadge;
        private Label _validationLabel;
        private PopupField<string> _environmentField;
        private VisualElement _environmentSlot;

        // Navigation
        private MolcaNavRail _rail;

        // Content
        private TwoPaneSplitView _body;
        private VisualElement _content;
        private bool _isNarrow;

        /// <summary>Builds the workspace.</summary>
        public NetworkHubView()
        {
            AddToClassList("molca-network");
            style.flexGrow = 1;

            MolcaEditorUi.Apply(this);
            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null && !styleSheets.Contains(uss))
                styleSheets.Add(uss);

            BuildToolbar();
            BuildBody();

            RegisterCallback<AttachToPanelEvent>(_ => OnAttach());
            RegisterCallback<DetachFromPanelEvent>(_ => OnDetach());
            RegisterCallback<GeometryChangedEvent>(evt => ApplyResponsiveLayout(evt.newRect.width));
        }

        #region Lifecycle

        private void OnAttach()
        {
            _session.Changed += OnSessionChanged;
            _session.NavigationRequested += Navigate;
            Activate();
        }

        /// <summary>
        /// Re-activation hook for the Hub's workspace cache. A cached view is hidden rather than detached,
        /// so it never sees a second attach — everything that has to happen each time the tab is shown
        /// again has to be driven from here.
        /// </summary>
        void IMolcaHubCachedView.OnWorkspaceActivated() => Activate();

        /// <summary>
        /// The work that belongs to *being shown* rather than to being constructed: pick up a catalog edit
        /// another surface made while this view was hidden, then honour any pending navigation.
        /// </summary>
        private void Activate()
        {
            // Another surface may have edited the catalog while this cached view was hidden.
            _session.Reload();
            ConsumePendingTarget();
        }

        private void OnDetach()
        {
            _session.Changed -= OnSessionChanged;
            _session.NavigationRequested -= Navigate;

            // Detach means eviction from the workspace cache or a tab-set rebuild, not a tab switch —
            // this is where an in-flight console send is cancelled and its client released.
            _session.Dispose();
        }

        private void OnSessionChanged()
        {
            RefreshToolbar();
            RefreshRail();
            RefreshContent();
        }

        private void ConsumePendingTarget()
        {
            var target = PendingTarget;
            PendingTarget = default;

            if (!target.IsEmpty)
                Navigate(target);
        }

        /// <summary>
        /// Selects a view, an entity within it, and an environment to preview under.
        /// </summary>
        /// <param name="target">Where to go. An empty target is ignored.</param>
        private void Navigate(NetworkHubNavigationTarget target)
        {
            if (target.IsEmpty)
                return;

            if (!string.IsNullOrEmpty(target.EnvironmentId) &&
                _session.HasCatalog &&
                _session.Catalog.FindEnvironment(target.EnvironmentId) != null)
            {
                _session.PreviewEnvironmentId = target.EnvironmentId;
            }

            if (NetworkHubViews.IsKnown(target.ViewId))
            {
                if (!string.IsNullOrEmpty(target.EntityId))
                    _session.SetSelection(target.ViewId, target.EntityId);

                _session.SelectedView = target.ViewId;
                _rail?.SelectNodeById(target.ViewId, notify: false);
            }

            RefreshContent();
        }

        #endregion

        #region Toolbar

        private void BuildToolbar()
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("molca-network__toolbar");
            toolbar.AddToClassList("molca-workspace-toolbar");

            _catalogLabel = new Label();
            _catalogLabel.AddToClassList("molca-network__catalog");
            toolbar.Add(_catalogLabel);

            _environmentSlot = new VisualElement();
            _environmentSlot.AddToClassList("molca-network__environment-slot");
            toolbar.Add(_environmentSlot);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            toolbar.Add(spacer);

            _validationBadge = new VisualElement();
            _validationBadge.AddToClassList("molca-network__validation");
            _validationBadge.Add(NetworkHubUi.Dot(MolcaStatusKind.Idle));
            _validationLabel = new Label();
            _validationBadge.Add(_validationLabel);
            _validationBadge.RegisterCallback<ClickEvent>(
                _ => Navigate(new NetworkHubNavigationTarget(NetworkHubViews.Diagnostics)));
            toolbar.Add(_validationBadge);

            toolbar.Add(MolcaButtons.Toolbar("Validate", () => _session.Reload()));
            toolbar.Add(MolcaButtons.Toolbar("⋯", ShowOverflowMenu));

            Add(toolbar);
            RefreshToolbar();
        }

        private void RefreshToolbar()
        {
            _catalogLabel.text = _session.HasCatalog
                ? $"{_session.Catalog.name}  ·  schema v{_session.Catalog.SchemaVersion}"
                : "No catalog";

            _catalogLabel.tooltip = _session.HasCatalog
                ? _session.IsCatalogRegistered
                    ? "This catalog is registered on GlobalSettings, so the runtime loads it."
                    : "This catalog exists but is not registered on GlobalSettings, so the runtime does not load it."
                : "This project has no network catalog yet.";

            _catalogLabel.EnableInClassList(
                "molca-network__catalog--unregistered", _session.HasCatalog && !_session.IsCatalogRegistered);

            RefreshEnvironmentSelector();
            RefreshValidationBadge();
        }

        /// <summary>
        /// Rebuilds the authoring preview selector.
        /// </summary>
        /// <remarks>
        /// Rebuilt rather than repopulated because <see cref="PopupField{T}"/> takes its choices at
        /// construction; the environment list changes whenever the catalog is edited.
        /// </remarks>
        private void RefreshEnvironmentSelector()
        {
            _environmentSlot.Clear();
            _environmentField = null;

            if (!_session.HasCatalog || _session.Catalog.Environments.Count == 0)
                return;

            var choices = new List<string>();
            foreach (var environment in _session.Catalog.Environments)
            {
                if (environment != null && !string.IsNullOrEmpty(environment.Id))
                    choices.Add(environment.Id);
            }

            if (choices.Count == 0)
                return;

            string current = choices.Contains(_session.PreviewEnvironmentId)
                ? _session.PreviewEnvironmentId
                : choices[0];

            _environmentField = new PopupField<string>("Preview", choices, current);
            _environmentField.AddToClassList("molca-network__environment");
            _environmentField.tooltip =
                "The environment effective-value previews resolve under. This is an authoring preview " +
                "only — it never changes the runtime environment selection or writes to the catalog.";
            _environmentField.RegisterValueChangedCallback(evt => _session.PreviewEnvironmentId = evt.newValue);

            _environmentSlot.Add(_environmentField);
        }

        private void RefreshValidationBadge()
        {
            var report = _session.Validation;

            var status = !_session.HasCatalog ? MolcaStatusKind.Idle
                : report.ErrorCount > 0 ? MolcaStatusKind.Error
                : report.WarningCount > 0 ? MolcaStatusKind.Warning
                : MolcaStatusKind.Ok;

            _validationBadge.Clear();
            _validationBadge.Add(NetworkHubUi.Dot(status));
            _validationLabel = new Label(
                !_session.HasCatalog ? "Not configured"
                : report.ErrorCount > 0 || report.WarningCount > 0 ? report.Summarize()
                : "Valid");
            _validationBadge.Add(_validationLabel);
            _validationBadge.tooltip = "Open Diagnostics.";
        }

        private void ShowOverflowMenu()
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("Scan legacy networking…"), false,
                () => Navigate(new NetworkHubNavigationTarget(NetworkHubViews.Overview)));

            if (_session.HasCatalog)
            {
                menu.AddItem(new GUIContent("Locate catalog asset"), false, () =>
                {
                    Selection.activeObject = _session.Catalog;
                    EditorGUIUtility.PingObject(_session.Catalog);
                });

                if (!_session.IsCatalogRegistered)
                {
                    menu.AddItem(new GUIContent("Register on GlobalSettings"), false, () =>
                    {
                        NetworkCatalogLocator.Register(_session.Catalog);
                        _session.Reload();
                    });
                }
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Locate catalog asset"));
            }

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Rebuild previews"), false, () => _session.Reload());
            menu.AddItem(new GUIContent("Open networking guide"), false,
                () => NetworkHubUi.OpenDoc("NETWORKING_CATALOG"));
            menu.AddItem(new GUIContent("Open migration guide"), false,
                () => NetworkHubUi.OpenDoc("NETWORKING_MIGRATION"));

            menu.ShowAsContext();
        }

        #endregion

        #region Navigation rail

        private void BuildBody()
        {
            // A split pane rather than a flex row, so the rail drags to width exactly as it does in
            // Settings and Docs. The pane owns the width, which is why the rail pane sets none.
            _body = new TwoPaneSplitView(0, RailWidth, TwoPaneSplitViewOrientation.Horizontal);
            _body.AddToClassList("molca-network__body");
            _body.style.flexGrow = 1;

            // The rail *is* the pane, as it is in Settings and Docs. Wrapping it in a container that also
            // held the search box left the rail's surface and right-hand border spanning only the middle
            // band of the pane — a border that started below the search field and stopped above the results.
            _rail = new MolcaNavRail("Search catalog");
            _rail.NodeSelected += node =>
            {
                _session.SelectedView = node.Id;
                _rail.ClearSearch();
                RefreshContent();
            };

            // Catalog matches are contributed as rail rows while filtering, the same way the Hub offers
            // workspace tabs from its search box. That is what lets one search box serve both jobs: typing
            // filters the view list and searches the catalog at once, and a result is a row you select.
            _rail.FilterOnlyRoots = BuildSearchRoots;

            _body.Add(_rail);

            _content = new VisualElement();
            _content.AddToClassList("molca-network__content");
            _content.style.flexGrow = 1;
            _body.Add(_content);

            Add(_body);

            RefreshRail();
            RefreshContent();
        }

        private void RefreshRail()
        {
            string selected = _session.SelectedView;

            var roots = new List<MolcaNavRailNode>();
            foreach (string viewId in NetworkHubViews.All)
                roots.Add(new MolcaNavRailNode(viewId, NetworkHubViews.Label(viewId), () => null));

            _rail.SetRoots(roots);
            _rail.SelectNodeById(selected, notify: false);
        }

        /// <summary>
        /// Rebuilds the search results list under the rail.
        /// </summary>
        /// <remarks>
        /// Results navigate straight to the detail they name rather than filtering a list, because the
        /// thing a search for "identity" wants is that service's detail pane, not a shorter master list.
        /// </remarks>
        /// <summary>
        /// Catalog matches for the rail's active filter, grouped under one category.
        /// </summary>
        /// <param name="filter">The search text.</param>
        /// <returns>A single category of command leaves, or empty when nothing matches.</returns>
        /// <remarks>
        /// Command leaves, not content leaves: a result is a jump to the entity's own view, so it must not
        /// be remembered as the row to restore next time.
        /// </remarks>
        private IReadOnlyList<MolcaNavRailNode> BuildSearchRoots(string filter)
        {
            if (string.IsNullOrEmpty(filter) || !_session.HasCatalog)
                return System.Array.Empty<MolcaNavRailNode>();

            var matches = NetworkHubSearch.Find(_session.Catalog, filter);
            if (matches.Count == 0)
                return System.Array.Empty<MolcaNavRailNode>();

            var children = new List<MolcaNavRailNode>(matches.Count);
            foreach (var match in matches)
            {
                var target = match.Target;
                children.Add(MolcaNavRailNode.Command(
                    "find:" + match.Kind + ":" + match.Title,
                    match.Title,
                    () =>
                    {
                        _rail.ClearSearch();
                        Navigate(target);
                    },
                    match.Subtitle));
            }

            return new[] { new MolcaNavRailNode("cat:catalog-matches", "Catalog", children) };
        }

        #endregion

        #region Content

        private void RefreshContent()
        {
            _content.Clear();

            if (!_session.HasCatalog)
            {
                _content.Add(new NetworkEmptyStateView(_session));
                return;
            }

            _content.Add(CreateView(_session.SelectedView));
        }

        private VisualElement CreateView(string viewId)
        {
            switch (viewId)
            {
                case NetworkHubViews.Environments: return new NetworkEnvironmentsView(_session);
                case NetworkHubViews.Services: return new NetworkServicesView(_session);
                case NetworkHubViews.Endpoints: return new NetworkEndpointsView(_session);
                case NetworkHubViews.Policies: return new NetworkPoliciesView(_session);
                case NetworkHubViews.Credentials: return new NetworkCredentialsView(_session);
                case NetworkHubViews.Providers: return new NetworkProvidersView(_session);
                case NetworkHubViews.Console: return new NetworkConsoleView(_session);
                case NetworkHubViews.Live: return new NetworkLiveView(_session);
                case NetworkHubViews.Diagnostics: return new NetworkDiagnosticsView(_session);
                default: return new NetworkOverviewView(_session);
            }
        }

        private void ApplyResponsiveLayout(float width)
        {
            bool narrow = width > 0f && width < NarrowWidth;
            if (narrow == _isNarrow) return;

            _isNarrow = narrow;
            _body.EnableInClassList("molca-network__body--narrow", narrow);

            // Narrow puts the rail above the content, which for a split pane is its orientation rather than
            // a flex-direction override — the splitter has to move to the same axis the CSS is flipping to.
            _body.orientation = narrow
                ? TwoPaneSplitViewOrientation.Vertical
                : TwoPaneSplitViewOrientation.Horizontal;
        }

        #endregion
    }
}
