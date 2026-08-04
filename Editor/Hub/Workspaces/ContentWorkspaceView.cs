using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;
using Molca.ContentPackage;
using Molca.ContentPackage.Editor;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// The Content workspace: a navigation rail over the project's packages, its release identity, its
    /// delivery settings, and the build-and-publish pages.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/</c>, with its pages under
    /// <c>Content/</c>. <b>Registration:</b> contributed as a Hub workspace by
    /// <see cref="ContentWorkspaceProvider"/>.
    /// <para>
    /// <b>Shape: a package is a destination, not a stage.</b> This replaced a four-tab strip —
    /// Packages, Release, Verify, Publish — which is a pipeline, and a pipeline has nowhere to put a
    /// package's own form. So the tabs showed every authored value as a label and ended with a line
    /// telling the reader to go and edit it in the <c>ContentPackageSettings</c> inspector instead. The
    /// rail gives each package a row and the whole detail pane, which is what makes authoring possible
    /// here at all.
    /// </para>
    /// <para>
    /// <b>The view computes nothing.</b> Every number comes from a service —
    /// <see cref="ContentValidation"/>, <see cref="ContentBuildGraph"/>,
    /// <see cref="ContentReleaseCandidate"/> — and where a service cannot answer, the page shows nothing
    /// and says why. That rule is the point: the surface this descends from inferred bundle ownership
    /// from filename prefixes and reported download sizes smaller than what a player actually fetched,
    /// and nobody re-checks a plausible number.
    /// </para>
    /// <para>
    /// <b>And it writes nothing directly.</b> Every edit goes through
    /// <see cref="ContentPackageEditingService"/> by way of <see cref="ContentWorkspaceContext"/>, the
    /// same path the MCP tools and remediation use.
    /// </para>
    /// </remarks>
    internal sealed class ContentWorkspaceView : VisualElement
    {
        /// <summary>Left-rail width, per the editor design language.</summary>
        private const int RailWidth = 188;

        /// <summary>Width below which the rail moves above the content instead of beside it.</summary>
        private const float NarrowWidth = 640f;

        private readonly MolcaWorkspaceHeader _header = new MolcaWorkspaceHeader("Content");
        private readonly MolcaNavRail _rail;
        private readonly TwoPaneSplitView _split =
            new TwoPaneSplitView(0, RailWidth, TwoPaneSplitViewOrientation.Horizontal);
        private readonly VisualElement _content = new VisualElement();

        private readonly ContentRuntimeProbe _runtime;

        private ContentPackageSettings _settings;
        private ContentWorkspaceContext _context;
        private bool? _narrow;

        /// <summary>Builds the workspace and restores the last selected row.</summary>
        public ContentWorkspaceView()
        {
            AddToClassList("molca-workspace");
            AddToClassList("molca-workspace--railed");
            MolcaEditorUi.Apply(this);

            BuildHeader();
            Add(_header);

            _rail = new MolcaNavRail(
                "Search packages",
                ContentWorkspaceSession.ReadExpanded,
                ContentWorkspaceSession.WriteExpanded);
            _rail.NodeSelected += OnNodeSelected;

            _split.AddToClassList("molca-workspace-split");
            _split.Add(_rail);

            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { minHeight = 0 } };
            scroll.AddToClassList("molca-workspace-split__content");
            scroll.Add(_content);
            _split.Add(scroll);
            Add(_split);

            _runtime = new ContentRuntimeProbe(KnownPackageIds);
            _runtime.Changed += Rebuild;

            _split.RegisterCallback<GeometryChangedEvent>(evt => ApplyResponsiveLayout(evt.newRect.width));
            RegisterCallback<DetachFromPanelEvent>(_ => OnDetach());

            Rebuild();
        }

        // ── Shell ────────────────────────────────────────────────────────────

        private void BuildHeader()
        {
            _header.AddAction(MolcaButtons.Toolbar("Locate asset", () =>
            {
                if (_settings == null) return;
                Selection.activeObject = _settings;
                EditorGUIUtility.PingObject(_settings);
            }));

            _header.AddAction(MolcaButtons.Toolbar("Refresh", Rebuild));
        }

        /// <summary>
        /// Re-resolves the settings asset, rebuilds the rail, and re-renders the selected page.
        /// </summary>
        /// <remarks>
        /// The whole workspace, not the one page: a package edit changes the rail's status dots and the
        /// header's finding count as often as it changes the form, and a page that refreshed only itself
        /// would leave a row reading "Valid" beside a form showing the error that just appeared.
        /// </remarks>
        private void Rebuild()
        {
            ResolveSettings();
            RefreshRail();
            RenderContent();
            RefreshHeader();
        }

        private void ResolveSettings()
        {
            var found = AssetDatabase.FindAssets("t:ContentPackageSettings")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ContentPackageSettings>)
                .Where(asset => asset != null)
                .ToList();

            _settings = found.FirstOrDefault();
            _context = _settings != null
                ? new ContentWorkspaceContext(_settings, Rebuild, _runtime)
                : null;
        }

        /// <summary>The packages this project defines, for the runtime probe to ask the service about.</summary>
        private IEnumerable<string> KnownPackageIds() =>
            _settings == null
                ? Enumerable.Empty<string>()
                : _settings.packageConfigs
                    .Where(config => config != null && !string.IsNullOrEmpty(config.packageId))
                    .Select(config => config.packageId);

        private void RefreshHeader()
        {
            if (ContentWorkspaceSession.Busy)
            {
                _header.SetSummary(ContentWorkspaceSession.BusyProgress >= 0f
                    ? $"{ContentWorkspaceSession.BusyStatus} · {ContentWorkspaceSession.BusyProgress:P0}"
                    : ContentWorkspaceSession.BusyStatus);
                return;
            }

            if (!string.IsNullOrEmpty(ContentWorkspaceSession.LastPublishSummary))
            {
                _header.SetSummary(ContentWorkspaceSession.LastPublishSummary);
                return;
            }

            if (_context == null)
            {
                _header.SetSummary("No package settings found");
                return;
            }

            var report = _context.Report;
            string findings = report.ErrorCount > 0 ? $"{report.ErrorCount} error(s)"
                : report.WarningCount > 0 ? $"{report.WarningCount} warning(s)"
                : "valid";

            _header.SetSummary($"{_settings.packageConfigs.Count} package(s) · {findings}");
        }

        // ── Rail ─────────────────────────────────────────────────────────────

        private void RefreshRail()
        {
            var roots = new List<MolcaNavRailNode>
            {
                new MolcaNavRailNode("cat:packages", "Packages", BuildPackageNodes()),
                new MolcaNavRailNode("cat:release", "Release", new List<MolcaNavRailNode>
                {
                    Leaf(ContentWorkspaceNodes.Compatibility, "Compatibility",
                        "Version, app range, and changelog for the next release."),
                    Leaf(ContentWorkspaceNodes.Protocol, "Protocol & keys",
                        "How content is resolved, and which keys may sign a release."),
                    Leaf(ContentWorkspaceNodes.Delivery, "Delivery",
                        "Remote catalog, cache budget, and download behaviour."),
                }),
                new MolcaNavRailNode("cat:ship", "Ship", new List<MolcaNavRailNode>
                {
                    Leaf(ContentWorkspaceNodes.Verify, "Verify", "Build clean and validate against the build."),
                    Leaf(ContentWorkspaceNodes.Publish, "Publish", "Sign and promote verified content."),
                }),
            };

            _rail.SetRoots(roots);
            _rail.ReassertSelection(ContentWorkspaceSession.SelectedNode);
        }

        /// <summary>
        /// The Packages category: the cross-package list, one row per package, then the add command.
        /// </summary>
        /// <remarks>
        /// Rows carry the package's worst finding as a status dot, so the rail answers "which one needs
        /// attention?" without opening any of them. The add row is a command leaf, which the rail never
        /// reports as a location — that is what stops "Add package" being restored as the selected row
        /// next session and silently creating a second package.
        /// </remarks>
        private List<MolcaNavRailNode> BuildPackageNodes()
        {
            var nodes = new List<MolcaNavRailNode>
            {
                Leaf(ContentWorkspaceNodes.Packages, "All packages",
                    "Every package, with its findings and build ownership."),
            };

            if (_context != null)
            {
                foreach (var config in _settings.packageConfigs.Where(entry => entry != null))
                {
                    string id = config.packageId ?? "";
                    string label = string.IsNullOrEmpty(config.displayName) ? id : config.displayName;
                    if (string.IsNullOrEmpty(label)) label = "(unnamed package)";

                    nodes.Add(new MolcaNavRailNode(
                        ContentWorkspaceNodes.ForPackage(id),
                        label,
                        () => null,
                        description: id,
                        status: ContentWorkspaceUi.StatusOf(_context.Report, id),
                        tooltip: id));
                }

                if (!_context.IsReadOnly)
                {
                    nodes.Add(MolcaNavRailNode.Command(
                        ContentWorkspaceNodes.AddPackage, "＋ Add package", AddPackage,
                        "Creates a package with a placeholder id and opens it."));
                }
            }

            return nodes;
        }

        private static MolcaNavRailNode Leaf(string id, string label, string tooltip = null) =>
            new MolcaNavRailNode(id, label, () => null, tooltip: tooltip);

        private void OnNodeSelected(MolcaNavRailNode node)
        {
            string id = node.Id;
            ContentWorkspaceSession.SelectedNode = id;

            string packageId = ContentWorkspaceNodes.PackageOf(id);
            if (packageId != null) ContentWorkspaceSession.SelectedPackageId = packageId;

            _rail.ClearSearch();
            RenderContent();
        }

        /// <summary>Selects a row and shows its page, e.g. after an add or a rename.</summary>
        /// <param name="nodeId">The node to select.</param>
        private void Navigate(string nodeId)
        {
            ContentWorkspaceSession.SelectedNode = nodeId;

            string packageId = ContentWorkspaceNodes.PackageOf(nodeId);
            if (packageId != null) ContentWorkspaceSession.SelectedPackageId = packageId;

            Rebuild();
            _rail.SelectNodeById(nodeId, notify: false);
        }

        private void AddPackage()
        {
            if (_context == null) return;

            var result = _context.Editing.AddPackage();
            if (!result.Changed)
            {
                UnityEngine.Debug.LogWarning($"[ContentPackage] {result.Message}");
                return;
            }

            AssetDatabase.SaveAssets();
            ContentWorkspaceSession.InvalidateBuild();
            Navigate(ContentWorkspaceNodes.ForPackage(result.After));
        }

        // ── Content ──────────────────────────────────────────────────────────

        private void RenderContent()
        {
            _content.Clear();

            if (_context == null)
            {
                _content.Add(ContentWorkspaceUi.Help(
                    "No ContentPackageSettings asset was found.\n\n" +
                    "Create one under Assets/ — not inside a package. Package assets are replaced on " +
                    "upgrade, and this asset holds the trusted release signing keys.",
                    HelpBoxMessageType.Warning));
                return;
            }

            if (_context.IsReadOnly)
                _content.Add(ContentWorkspaceUi.Help(_context.ReadOnlyReason, HelpBoxMessageType.Warning));

            string node = ContentWorkspaceSession.SelectedNode;
            string packageId = ContentWorkspaceNodes.PackageOf(node);

            if (packageId != null)
            {
                var config = _settings.packageConfigs.FirstOrDefault(entry => entry?.packageId == packageId);
                if (config == null)
                {
                    // The package was renamed or removed from another surface while this row was
                    // selected. Falling back to the list beats a blank pane claiming to show it.
                    ContentWorkspaceSession.SelectedNode = ContentWorkspaceNodes.Packages;
                    _content.Add(new ContentPackagesView(_context, Navigate));
                    return;
                }

                _content.Add(new ContentPackageDetailView(_context, config, Navigate));
                return;
            }

            switch (node)
            {
                case ContentWorkspaceNodes.Compatibility:
                    _content.Add(new ContentCompatibilityView(_context));
                    break;
                case ContentWorkspaceNodes.Protocol:
                    _content.Add(new ContentProtocolView(_context));
                    break;
                case ContentWorkspaceNodes.Delivery:
                    _content.Add(new ContentDeliveryView(_context));
                    break;
                case ContentWorkspaceNodes.Verify:
                    _content.Add(new ContentVerifyView(_context, RefreshHeader, Rebuild));
                    break;
                case ContentWorkspaceNodes.Publish:
                    _content.Add(new ContentPublishView(_context, RefreshHeader, Rebuild));
                    break;
                default:
                    _content.Add(new ContentPackagesView(_context, Navigate));
                    break;
            }
        }

        /// <summary>
        /// Releases everything that outlives a render when the workspace goes away.
        /// </summary>
        /// <remarks>
        /// Detach means the workspace cache evicted this view or the tab set was rebuilt — not a tab
        /// switch, which the cached content survives. Both things released here would otherwise keep
        /// running against a view that no longer exists: an upload reporting its result into nothing,
        /// and an <c>EditorApplication.update</c> hook rebuilding a detached element once a second.
        /// </remarks>
        private void OnDetach()
        {
            ContentPublishView.CancelRunning();

            _runtime.Changed -= Rebuild;
            _runtime.Dispose();
        }

        private void ApplyResponsiveLayout(float width)
        {
            bool narrow = width > 0f && width < NarrowWidth;
            if (_narrow == narrow) return;

            _narrow = narrow;

            // The split lays panes out from its own orientation, so a narrow dock flips that rather than
            // a flex-direction in USS — otherwise the panes and the drag handle end up on different axes.
            _split.orientation = narrow
                ? TwoPaneSplitViewOrientation.Vertical
                : TwoPaneSplitViewOrientation.Horizontal;
        }
    }
}
