using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.ReferenceSystem.Repair;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// The References Hub workspace: continuous, navigable reference health, and the surface where the
    /// wiring is authored.
    /// </summary>
    /// <remarks>
    /// <para>A projection of <see cref="ReferenceAuditSnapshot"/> for everything it shows, and a client of
    /// <see cref="ReferenceAuthoringPlanner"/> for everything it changes. The view holds no scan logic, no
    /// resolution rule and no repair decision of its own — those live in the audit engine and the planners,
    /// so what the workspace shows and what a build enforces cannot drift apart.</para>
    ///
    /// <para><b>Why authoring lives here.</b> A Ref Id is a name other objects have written down, so
    /// renaming one is a refactor over an inbound set that only a surface holding the whole audit knows.
    /// Every edit offered here becomes a previewed plan through the same executor a repair uses; there is no
    /// control on this workspace that writes without showing the change first.</para>
    ///
    /// <para>Run state lives in <see cref="ReferenceHubSession"/>, not here, so a long audit survives a Hub
    /// tab switch: this view subscribes on <see cref="AttachToPanelEvent"/> and unsubscribes on
    /// <see cref="DetachFromPanelEvent"/> without ever cancelling.</para>
    ///
    /// <para>The workspace never scans on open. Building a snapshot can open scenes and take real time, and
    /// looking at a tab is not a request to do that; the header's actions are.</para>
    /// </remarks>
    public sealed class ReferenceHubView : VisualElement
    {
        private const string UssPath =
            "Packages/com.molca.core/Editor/ReferenceSystem/Hub/ReferenceHubView.uss";

        /// <summary>Width below which the table and detail panel stack instead of sitting side by side.</summary>
        private const float NarrowWidth = 780f;

        /// <summary>Row height of the virtualized table.</summary>
        private const int RowHeight = 22;

        /// <summary>Number of columns in the table.</summary>
        public const int ColumnCount = 5;

        /// <summary>
        /// Stable column names, in display order. Shared by the column definitions and the per-view
        /// retitling so a heading can never end up over the wrong column.
        /// </summary>
        internal static readonly string[] ColumnIds = { "name", "identity", "source", "state", "note" };

        /// <summary>
        /// Column headings per view. The columns hold different things in each view — a reference's "name"
        /// is its property path, a target's is its display name — so naming them once globally would
        /// mislabel two views out of three.
        /// </summary>
        /// <param name="kind">The view being displayed.</param>
        internal static (string Heading, string Tooltip)[] Columns(ReferenceHubViewKind kind) => kind switch
        {
            ReferenceHubViewKind.References => new[]
            {
                ("Property", "The serialized field that holds the reference."),
                ("Stored target", "The RefType:RefId this field asks for. '<unset>' means no reference is assigned."),
                ("Source", "The asset and the object that declares the field."),
                ("Resolves to", "What the runtime would do with this reference — see the detail panel for why."),
                ("Notes", "'legacy fallback' means the reference stores no Ref Type, so it depends on the ID-only compatibility path — which refuses to resolve the moment a second object carries the same Ref Id."),
            },
            ReferenceHubViewKind.Targets => new[]
            {
                ("Target", "Ref Type, then the targets under it, then the references that reach each one."),
                ("Identity", "The target's RefType:RefId. Renaming it here moves every inbound reference with it."),
                ("Source", "The asset and the object that provides the target."),
                ("Runtime", "Whether the runtime registry ever holds this target. A prefab or ScriptableObject provider is never registered, so it cannot answer a lookup."),
                ("Inbound", "How many references resolve here. A second number means more references claim the id than reach it, which is the duplicate symptom."),
            },
            _ => new[]
            {
                ("Finding", "The stable REFnnn code and what is wrong, in one line."),
                ("Stored target", "The RefType:RefId involved."),
                ("Source", "The asset and the object the finding is anchored to."),
                ("Resolution", "What resolution would do at runtime."),
                ("Repair", "'automatic' is covered by Preview safe repairs. 'needs a decision' means the data does not record what was intended, so you choose the target."),
            },
        };

        /// <summary>
        /// A site key the workspace should select once it is built, set by <see cref="ReferenceHubWorkspace"/>
        /// when the user picks "Open in References" from a property drawer.
        /// </summary>
        internal static string PendingSiteKey { get; set; }

        private static ReferenceHubSession Session => ReferenceHubSession.Instance;

        // Header
        private VisualElement _stateDot;
        private Label _stateLabel;
        private Label _countsLabel;
        private Label _coverageLabel;
        private Label _metaLabel;
        private Label _staleLabel;
        private Button _refreshButton;
        private Button _fullAuditButton;
        private Button _safeRepairButton;
        private Button _policyButton;
        private Button _cancelButton;
        private ProgressBar _progressBar;
        private VisualElement _progressRow;
        private VisualElement _policyPanel;

        // Views
        private VisualElement _viewTabs;
        private readonly Dictionary<ReferenceHubViewKind, Button> _viewButtons = new();

        // Filters
        private VisualElement _filterRow;
        private MolcaSearchField _search;
        private Button _errorChip;
        private Button _warnChip;
        private Button _infoChip;
        private Label _filterSummary;

        // Content
        private VisualElement _split;
        private VisualElement _tablePane;
        private MultiColumnTreeView _tree;
        private VisualElement _detailPane;
        private ScrollView _detail;
        private Label _emptyNote;

        private IReadOnlyList<ReferenceHubRow> _rows = Array.Empty<ReferenceHubRow>();
        private IReadOnlyList<ReferenceHubTreeNode> _roots = Array.Empty<ReferenceHubTreeNode>();
        private readonly Dictionary<int, ReferenceHubTreeNode> _nodesById = new();
        private List<ReferenceHubRow> _selectedRows = new();
        private ReferenceHubTreeNode _selectedNode;
        private bool _isNarrow;
        private bool _suppressSelectionEvents;

        // In-progress identity edits, kept outside the panel that renders them.
        //
        // The detail panel is rebuilt on any hierarchy selection change, because "point these at the
        // selection" has to know what the selection is. Clicking the intended target in the hierarchy is
        // therefore the single most likely thing to happen between typing a new Ref Id and pressing
        // Rename — and it would have discarded what was typed.
        private string _draftProviderKey;
        private string _draftRefId;
        private string _draftRefType;

        /// <summary>Builds the workspace.</summary>
        public ReferenceHubView()
        {
            AddToClassList("molca-references");
            style.flexGrow = 1;

            MolcaEditorUi.Apply(this);
            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null && !styleSheets.Contains(uss))
                styleSheets.Add(uss);

            BuildHeader();
            BuildViewTabs();
            BuildFilterRow();
            BuildContent();

            RegisterCallback<AttachToPanelEvent>(_ => OnAttach());
            RegisterCallback<DetachFromPanelEvent>(_ => OnDetach());

            // The split direction is decided by measured width, not by a guess about the Hub's size: the
            // workspace is docked wherever the user put it.
            RegisterCallback<GeometryChangedEvent>(evt => ApplyResponsiveLayout(evt.newRect.width));
        }

        #region Lifecycle

        private void OnAttach()
        {
            Unsubscribe();
            Subscribe();
            ConsumePendingSelection();
            Refresh();

            // The Runtime view reads live registry state, which changes without any editor event to
            // subscribe to — a provider registering mid-play raises nothing the Hub can hear. Poll,
            // but only while it is actually on screen during play, so an idle Hub costs nothing.
            schedule.Execute(RefreshLiveRuntimeView).Every(RuntimePollMilliseconds);
        }

        /// <summary>How often the Runtime view re-reads the live registry while playing.</summary>
        private const long RuntimePollMilliseconds = 1000;

        private void RefreshLiveRuntimeView()
        {
            if (EditorApplication.isPlaying && Session.View == ReferenceHubViewKind.Runtime)
                Refresh();
        }

        private void OnDetach() => Unsubscribe();

        private void Subscribe()
        {
            Session.RunStarted += OnRunStarted;
            Session.ProgressReported += OnProgress;
            Session.RunFinished += OnRunFinished;
            Session.SnapshotChanged += Refresh;
            Session.ViewStateChanged += Refresh;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            // A hierarchy selection is an input to this workspace now — "point these references at the
            // selection" has to know what the selection is without the user clicking Refresh.
            Selection.selectionChanged += RefreshDetail;
        }

        private void Unsubscribe()
        {
            Session.RunStarted -= OnRunStarted;
            Session.ProgressReported -= OnProgress;
            Session.RunFinished -= OnRunFinished;
            Session.SnapshotChanged -= Refresh;
            Session.ViewStateChanged -= Refresh;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            Selection.selectionChanged -= RefreshDetail;
        }

        private void OnRunStarted() => Refresh();

        private void OnProgress(string phase, float fraction)
        {
            _progressBar.value = fraction * 100f;
            _progressBar.title = phase;
        }

        private void OnRunFinished(bool cancelled) => Refresh();

        private void OnPlayModeChanged(PlayModeStateChange _) => Refresh();

        /// <summary>
        /// Selects the row a property drawer asked for, if one is pending.
        /// </summary>
        /// <remarks>
        /// The drawer stores a site key rather than a row, because the drawer runs in the Inspector where no
        /// audit table exists. Resolving it here means "Open in References" works whether or not the pending
        /// site is currently a finding — it lands on the References view if the reference is healthy, which is
        /// the honest answer to "show me this reference".
        /// </remarks>
        private void ConsumePendingSelection()
        {
            var siteKey = PendingSiteKey;
            if (string.IsNullOrEmpty(siteKey))
                return;

            PendingSiteKey = null;

            var issue = Session.Tables.Issues.FirstOrDefault(r => r.SiteKey == siteKey);
            if (issue != null)
            {
                Session.SetView(ReferenceHubViewKind.Issues);
                Session.SetSelectedKey(ReferenceHubViewKind.Issues, issue.Key);
                return;
            }

            Session.SetView(ReferenceHubViewKind.References);
            Session.SetSelectedKey(ReferenceHubViewKind.References, siteKey);
        }

        #endregion

        #region Header

        private void BuildHeader()
        {
            var header = new VisualElement();
            header.AddToClassList("molca-references__header");
            Add(header);

            var stateBlock = new VisualElement();
            stateBlock.AddToClassList("molca-references__state");
            header.Add(stateBlock);

            var stateLine = new VisualElement();
            stateLine.AddToClassList("molca-references__state-line");
            stateBlock.Add(stateLine);

            _stateDot = new VisualElement();
            _stateDot.AddToClassList("molca-status-dot");
            stateLine.Add(_stateDot);

            _stateLabel = new Label();
            _stateLabel.AddToClassList("molca-references__state-label");
            stateLine.Add(_stateLabel);

            _countsLabel = new Label();
            _countsLabel.AddToClassList("molca-references__state-counts");
            stateLine.Add(_countsLabel);

            _coverageLabel = new Label();
            _coverageLabel.AddToClassList("molca-references__state-detail");
            stateBlock.Add(_coverageLabel);

            _metaLabel = new Label();
            _metaLabel.AddToClassList("molca-references__state-detail");
            stateBlock.Add(_metaLabel);

            _staleLabel = new Label();
            _staleLabel.AddToClassList("molca-references__stale");
            _staleLabel.style.display = DisplayStyle.None;
            stateBlock.Add(_staleLabel);

            var actions = new VisualElement();
            actions.AddToClassList("molca-references__header-actions");
            header.Add(actions);

            _refreshButton = MolcaButtons.Toolbar("Refresh affected", () => Session.Run(wholeProject: false));
            _refreshButton.tooltip =
                "Re-audit what is already loaded: the open scenes, plus the configured prefabs and "
                + "ScriptableObjects. Does not open any scene.";
            actions.Add(_refreshButton);

            _fullAuditButton = MolcaButtons.Toolbar("Full audit", () => Session.Run(wholeProject: true));
            _fullAuditButton.tooltip =
                "Audit the whole project, opening every closed scene to read it and restoring your scene "
                + "setup afterwards. Reads only — nothing is modified.";
            actions.Add(_fullAuditButton);

            // The safe batch is a property of the snapshot, not of whichever row happens to be selected. It
            // used to be rebuilt under every row in the detail panel, alongside a ten-row severity editor.
            _safeRepairButton = MolcaButtons.Primary("Preview safe repairs", PreviewSafeRepairs);
            actions.Add(_safeRepairButton);

            _policyButton = MolcaButtons.Mini("Policy ▾", TogglePolicyPanel);
            _policyButton.tooltip =
                "Editor severities for this project. Builds always use the production policy, so an override "
                + "here cannot make a broken project build.";
            actions.Add(_policyButton);

            _cancelButton = MolcaButtons.Mini("Cancel", () => Session.Cancel());
            _cancelButton.style.display = DisplayStyle.None;
            actions.Add(_cancelButton);

            _progressRow = new VisualElement();
            _progressRow.AddToClassList("molca-references__progress");
            _progressRow.style.display = DisplayStyle.None;
            Add(_progressRow);

            _progressBar = new ProgressBar();
            _progressBar.AddToClassList("molca-references__progress-bar");
            _progressRow.Add(_progressBar);

            _policyPanel = new VisualElement();
            _policyPanel.AddToClassList("molca-references__policy-panel");
            _policyPanel.style.display = DisplayStyle.None;
            Add(_policyPanel);
        }

        private void RefreshHeader()
        {
            var health = Session.Health;

            _stateDot.RemoveFromClassList("molca-status-dot--ok");
            _stateDot.RemoveFromClassList("molca-status-dot--warn");
            _stateDot.RemoveFromClassList("molca-status-dot--error");
            _stateDot.RemoveFromClassList("molca-status-dot--idle");
            _stateDot.AddToClassList(DotClass(health.State));

            _stateLabel.text = health.Label;
            _countsLabel.text = health.DescribeCounts();
            _coverageLabel.text = health.DescribeCoverage();

            var when = health.CompletedAt.HasValue
                ? $"last audit {health.CompletedAt.Value:HH:mm:ss}"
                : "no audit yet this session";
            var repair = Session.RepairIndex;
            _metaLabel.text =
                $"{when} · {health.Mode} Mode · repair: {repair.Describe()} · "
                + $"policy: {ReferenceHubPolicyStore.Describe()}";

            _staleLabel.style.display = string.IsNullOrEmpty(health.StaleReason)
                ? DisplayStyle.None
                : DisplayStyle.Flex;
            _staleLabel.text = string.IsNullOrEmpty(health.StaleReason)
                ? string.Empty
                : $"Stale — {health.StaleReason}. Re-run before repairing.";

            var running = Session.IsRunning;
            _refreshButton.SetEnabled(!running);
            _fullAuditButton.SetEnabled(!running);
            _cancelButton.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            _progressRow.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;

            RefreshSafeRepairButton(repair, running);

            if (_policyPanel.style.display == DisplayStyle.Flex)
                BuildPolicyPanel();
        }

        /// <summary>
        /// Keeps the safe-repair button honest about what it would do right now.
        /// </summary>
        /// <remarks>
        /// Narrowed to the selection when there is one. A batch button that silently means "and forty other
        /// assets you cannot see" is the affordance this workspace is built to avoid, so the label says which
        /// of the two it currently is.
        /// </remarks>
        private void RefreshSafeRepairButton(ReferenceHubRepairIndex repair, bool running)
        {
            var scoped = SelectedOwnerKeys();
            var enabled = !running && !Session.IsStale && repair.AutomaticCount > 0;

            _safeRepairButton.text = scoped.Count > 0
                ? $"Preview safe repairs in selection ({scoped.Count} object{(scoped.Count == 1 ? "" : "s")})"
                : $"Preview safe repairs ({repair.AutomaticCount})";

            _safeRepairButton.tooltip = Session.IsStale
                ? "The snapshot is stale, so no plan can be built from it. Re-run the audit first."
                : scoped.Count > 0
                    ? "Build the plan for the unambiguous repairs that touch the selected objects only. "
                      + "Nothing is written until you approve it."
                    : "Build the plan for every unambiguous repair and show it in full. Nothing is written "
                      + "until you approve it.";

            _safeRepairButton.SetEnabled(enabled);
        }

        private void PreviewSafeRepairs()
        {
            var scoped = SelectedOwnerKeys();
            ReferenceHubAuthoring.PreviewAndApply(
                () => scoped.Count > 0
                    ? ReferenceAuthoringPlanner.PlanSafeRepairsWithin(Session.Snapshot, scoped)
                    : ReferenceRepairPlanner.PlanSafeRepairs(Session.Snapshot),
                "Apply Reference Repairs?");
        }

        private static string DotClass(ReferenceHubHealthState state) => state switch
        {
            ReferenceHubHealthState.Clean => "molca-status-dot--ok",
            ReferenceHubHealthState.Errors => "molca-status-dot--error",
            ReferenceHubHealthState.Warnings => "molca-status-dot--warn",
            ReferenceHubHealthState.Incomplete => "molca-status-dot--warn",
            ReferenceHubHealthState.Stale => "molca-status-dot--warn",
            _ => "molca-status-dot--idle",
        };

        private void TogglePolicyPanel()
        {
            var showing = _policyPanel.style.display == DisplayStyle.Flex;
            _policyPanel.style.display = showing ? DisplayStyle.None : DisplayStyle.Flex;
            _policyButton.text = showing ? "Policy ▾" : "Policy ▴";

            if (!showing)
                BuildPolicyPanel();
        }

        /// <summary>
        /// The severity-policy editor.
        /// </summary>
        /// <remarks>
        /// Behind a header control rather than appended under every selected row. It is project
        /// configuration — the same ten rows regardless of what is selected — and rebuilding it beneath each
        /// finding pushed the finding's own detail off the top of the panel.
        /// </remarks>
        private void BuildPolicyPanel()
        {
            _policyPanel.Clear();

            var card = new MolcaSectionCard(
                "Policy", subtitle: "Editor severities for this project · builds always use the production policy");
            _policyPanel.Add(card);

            foreach (var code in ReferenceHubPolicyStore.AllCodes)
            {
                var row = new VisualElement();
                row.AddToClassList("molca-references__policy-row");
                card.Body.Add(row);

                var fixedSeverity = ReferenceSeverityPolicy.IsNonLowerable(code);
                var effective = ReferenceHubPolicyStore.Effective(code);
                var baseline = ReferenceHubPolicyStore.Baseline(code);

                var label = new Label($"REF{(int)code:D3}  {code}");
                label.AddToClassList("molca-references__policy-label");
                row.Add(label);

                if (fixedSeverity)
                {
                    var locked = new Label($"{effective} (fixed)");
                    locked.AddToClassList("molca-references__policy-locked");
                    locked.tooltip =
                        "This code describes a reference that fails at runtime. Lowering it would let a build "
                        + "pass over a project that is already broken, so it cannot be configured.";
                    row.Add(locked);
                    continue;
                }

                var field = new EnumField(effective);
                field.AddToClassList("molca-references__policy-field");
                field.tooltip = $"Baseline: {baseline}";
                field.RegisterValueChangedCallback(evt =>
                {
                    ReferenceHubPolicyStore.SetOverride(code, (ReferenceFindingSeverity)evt.newValue);
                    Refresh();
                });
                row.Add(field);
            }

            if (ReferenceHubPolicyStore.HasOverrides)
            {
                card.Body.Add(MolcaButtons.Mini("Reset to production severities", () =>
                {
                    ReferenceHubPolicyStore.ClearOverrides();
                    Refresh();
                }));
                card.Body.Add(Note(
                    "Overridden severities apply to editor audits only. The build gate always uses the "
                    + "production policy, so a machine-local override cannot make a broken project build."));
            }
        }

        #endregion

        #region View tabs

        private void BuildViewTabs()
        {
            _viewTabs = new VisualElement();
            _viewTabs.AddToClassList("molca-references__views");
            Add(_viewTabs);

            foreach (var kind in (ReferenceHubViewKind[])Enum.GetValues(typeof(ReferenceHubViewKind)))
            {
                var captured = kind;
                var button = new Button(() => Session.SetView(captured)) { text = Label(captured) };
                button.AddToClassList("molca-references__view-tab");
                button.tooltip = Tooltip(captured);
                _viewButtons[kind] = button;
                _viewTabs.Add(button);
            }
        }

        private static string Label(ReferenceHubViewKind kind) => kind switch
        {
            ReferenceHubViewKind.Issues => "Issues",
            ReferenceHubViewKind.References => "References",
            ReferenceHubViewKind.Targets => "Targets",
            ReferenceHubViewKind.Runtime => "Runtime",
            _ => "Coverage",
        };

        private static string Tooltip(ReferenceHubViewKind kind) => kind switch
        {
            ReferenceHubViewKind.Issues => "Findings, most severe first.",
            ReferenceHubViewKind.References => "Every reference site and what it resolves to.",
            ReferenceHubViewKind.Targets =>
                "The wiring: Ref Type, the targets under it, and the references that reach each one. "
                + "Renaming and re-pointing happen here.",
            ReferenceHubViewKind.Runtime => "Live registrations, compared against the audit. Play Mode only.",
            _ => "What was scanned, skipped and failed. 'Clean' requires this to be complete.",
        };

        private void RefreshViewTabs()
        {
            var counts = Session.Tables;
            foreach (var pair in _viewButtons)
            {
                var active = pair.Key == Session.View;
                pair.Value.EnableInClassList("molca-references__view-tab--active", active);

                pair.Value.text = pair.Key switch
                {
                    ReferenceHubViewKind.Issues => $"Issues ({counts.Issues.Count})",
                    ReferenceHubViewKind.References => $"References ({counts.Sites.Count})",
                    ReferenceHubViewKind.Targets => $"Targets ({counts.Providers.Count})",
                    _ => Label(pair.Key),
                };
            }
        }

        #endregion

        #region Filters

        private void BuildFilterRow()
        {
            _filterRow = new VisualElement();
            _filterRow.AddToClassList("molca-references__filters");
            Add(_filterRow);

            _search = new MolcaSearchField("Filter by asset, owner, property, id or type");
            _search.AddToClassList("molca-references__search");
            _search.OnSearchChanged += query =>
            {
                Session.Filter.Query = query;
                Session.NotifyFilterChanged();
            };
            _filterRow.Add(_search);

            _errorChip = SeverityChip("Errors", () => Session.Filter.ShowErrors, v => Session.Filter.ShowErrors = v);
            _warnChip = SeverityChip("Warnings", () => Session.Filter.ShowWarnings, v => Session.Filter.ShowWarnings = v);
            _infoChip = SeverityChip("Info", () => Session.Filter.ShowInfo, v => Session.Filter.ShowInfo = v);
            _filterRow.Add(_errorChip);
            _filterRow.Add(_warnChip);
            _filterRow.Add(_infoChip);

            _filterRow.Add(MoreFiltersButton());

            _filterSummary = new Label();
            _filterSummary.AddToClassList("molca-references__filter-summary");
            _filterRow.Add(_filterSummary);
        }

        private Button SeverityChip(string label, Func<bool> get, Action<bool> set)
        {
            var chip = new Button { text = label };
            chip.AddToClassList("molca-references__chip");
            chip.clicked += () =>
            {
                set(!get());
                Session.NotifyFilterChanged();
            };
            return chip;
        }

        /// <summary>
        /// The secondary filters, in a menu rather than a row of controls.
        /// </summary>
        /// <remarks>
        /// Source kind, Ref Type and folder are populated from the rows actually present, not from every
        /// enum value: offering "PrefabAsset" in a project that scans no prefabs would suggest a filter that
        /// can only ever return nothing.
        /// </remarks>
        private Button MoreFiltersButton()
        {
            var button = MolcaButtons.Mini("Filter ▾", null);
            button.tooltip = "Source, type, folder, requiredness, legacy and repairability filters";
            button.clicked += () =>
            {
                var menu = new GenericMenu();
                var filter = Session.Filter;
                var rows = Session.AllRows(Session.View);

                menu.AddItem(new GUIContent("Any source"), string.IsNullOrEmpty(filter.SourceKind),
                    () => Apply(() => filter.SourceKind = string.Empty));
                foreach (var kind in ReferenceHubFilter.SourceKindsIn(rows))
                {
                    var captured = kind;
                    menu.AddItem(new GUIContent($"Source/{kind}"), filter.SourceKind == kind,
                        () => Apply(() => filter.SourceKind = captured));
                }

                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("Any reference type"), string.IsNullOrEmpty(filter.RefType),
                    () => Apply(() => filter.RefType = string.Empty));
                foreach (var type in ReferenceHubFilter.RefTypesIn(rows).Take(30))
                {
                    var captured = type;
                    menu.AddItem(new GUIContent($"Reference type/{type}"), filter.RefType == type,
                        () => Apply(() => filter.RefType = captured));
                }

                menu.AddSeparator(string.Empty);
                foreach (var folder in FoldersIn(rows))
                {
                    var captured = folder;
                    menu.AddItem(new GUIContent($"Folder/{folder}"), filter.FolderPrefix == folder,
                        () => Apply(() => filter.FolderPrefix = captured));
                }
                menu.AddItem(new GUIContent("Folder/Any"), string.IsNullOrEmpty(filter.FolderPrefix),
                    () => Apply(() => filter.FolderPrefix = string.Empty));

                menu.AddSeparator(string.Empty);
                foreach (var assignment in (ReferenceHubAssignmentFilter[])Enum.GetValues(typeof(ReferenceHubAssignmentFilter)))
                {
                    var captured = assignment;
                    menu.AddItem(new GUIContent($"Requiredness/{assignment}"), filter.Assignment == assignment,
                        () => Apply(() => filter.Assignment = captured));
                }

                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("Legacy fallback only (no stored Ref Type)"), filter.LegacyOnly,
                    () => Apply(() => filter.LegacyOnly = !filter.LegacyOnly));
                menu.AddItem(new GUIContent("Read-only assets only"), filter.ReadOnlyOnly,
                    () => Apply(() => filter.ReadOnlyOnly = !filter.ReadOnlyOnly));

                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("Repair/Any"), filter.Repair == null,
                    () => Apply(() => filter.Repair = null));
                menu.AddItem(new GUIContent("Repair/Automatic"),
                    filter.Repair == ReferenceHubRepairAvailability.Automatic,
                    () => Apply(() => filter.Repair = ReferenceHubRepairAvailability.Automatic));
                menu.AddItem(new GUIContent("Repair/Needs a decision"),
                    filter.Repair == ReferenceHubRepairAvailability.RequiresChoice,
                    () => Apply(() => filter.Repair = ReferenceHubRepairAvailability.RequiresChoice));
                menu.AddItem(new GUIContent("Repair/Not repairable"),
                    filter.Repair == ReferenceHubRepairAvailability.None,
                    () => Apply(() => filter.Repair = ReferenceHubRepairAvailability.None));

                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("Reset all filters"), false, () => Apply(() =>
                {
                    filter.Reset();
                    _search.Clear();
                }));

                menu.ShowAsContext();
            };
            return button;
        }

        private void Apply(Action change)
        {
            change();
            Session.NotifyFilterChanged();
        }

        /// <summary>Top-level folders present in the rows, as folder-filter options.</summary>
        private static IReadOnlyList<string> FoldersIn(IReadOnlyList<ReferenceHubRow> rows) =>
            rows.Select(r => r.AssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(TopTwoSegments)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();

        private static string TopTwoSegments(string assetPath)
        {
            var parts = assetPath.Split('/');
            return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : parts[0];
        }

        private void RefreshFilters()
        {
            var filter = Session.Filter;
            _errorChip.EnableInClassList("molca-references__chip--active", filter.ShowErrors);
            _warnChip.EnableInClassList("molca-references__chip--active", filter.ShowWarnings);
            _infoChip.EnableInClassList("molca-references__chip--active", filter.ShowInfo);

            var isTable = IsTableView(Session.View);
            _filterRow.style.display = isTable ? DisplayStyle.Flex : DisplayStyle.None;

            if (!isTable)
                return;

            var total = Session.AllRows(Session.View).Count;
            var selected = _selectedRows.Count;

            var summary = filter.IsDefault
                ? $"{total} row{(total == 1 ? "" : "s")}"
                : $"{_rows.Count} of {total} · {filter.Describe()}";

            _filterSummary.text = selected > 1 ? $"{summary} · {selected} selected" : summary;
        }

        private static bool IsTableView(ReferenceHubViewKind kind) =>
            kind == ReferenceHubViewKind.Issues
            || kind == ReferenceHubViewKind.References
            || kind == ReferenceHubViewKind.Targets;

        #endregion

        #region Table

        private void BuildContent()
        {
            _split = new VisualElement();
            _split.AddToClassList("molca-references__split");
            Add(_split);

            _tablePane = new VisualElement();
            _tablePane.AddToClassList("molca-references__table-pane");
            _split.Add(_tablePane);

            _emptyNote = new Label();
            _emptyNote.AddToClassList("molca-references__empty");
            _tablePane.Add(_emptyNote);

            BuildTree();

            _detailPane = new VisualElement();
            _detailPane.AddToClassList("molca-references__detail-pane");
            _split.Add(_detailPane);

            _detail = new ScrollView(ScrollViewMode.Vertical);
            _detail.AddToClassList("molca-references__detail");
            _detailPane.Add(_detail);
        }

        /// <summary>
        /// Builds the one table control every view renders into.
        /// </summary>
        /// <remarks>
        /// <para>A <see cref="MultiColumnTreeView"/> rather than the hand-rolled header plus fixed-basis cell
        /// classes it replaces. Column widths, resizing, reordering and sort indicators are the control's
        /// job; the previous implementation kept a shared array of USS width classes solely so the header
        /// could not drift out of alignment with the rows, which is a problem this control does not have.</para>
        ///
        /// <para>Flat views set root items with no children, so Issues and References keep reading exactly as
        /// they did. Targets nests the same rows, which is the whole point of the view.</para>
        /// </remarks>
        private void BuildTree()
        {
            _tree = new MultiColumnTreeView
            {
                fixedItemHeight = RowHeight,
                selectionType = SelectionType.Multiple,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                showBorder = false,
                sortingMode = ColumnSortingMode.Default,
            };
            _tree.AddToClassList("molca-references__table");

            for (var index = 0; index < ColumnCount; index++)
            {
                var position = index;
                var column = new Column
                {
                    name = ColumnIds[position],
                    stretchable = true,
                    resizable = true,
                    sortable = true,
                    minWidth = position == 0 ? 140 : 60,
                    width = ColumnWidth(position),
                    makeCell = () => MakeCell(position),
                    bindCell = (element, rowIndex) => BindCell(element, rowIndex, position),
                    comparison = (left, right) => CompareRows(left, right, position),
                };
                _tree.columns.Add(column);
            }

            // Fully qualified: this class has its own Columns(view) method, which otherwise shadows the
            // UIElements type in this expression.
            _tree.columns.stretchMode = UnityEngine.UIElements.Columns.StretchMode.GrowAndFill;
            _tree.selectionChanged += _ => OnSelectionChanged();
            _tablePane.Add(_tree);
        }

        private static float ColumnWidth(int position) => position switch
        {
            0 => 240f,
            1 => 190f,
            2 => 190f,
            3 => 140f,
            _ => 110f,
        };

        /// <summary>
        /// One cell. The first column carries the severity dot, so colour and text travel together.
        /// </summary>
        private static VisualElement MakeCell(int position)
        {
            if (position != 0)
            {
                var text = new Label();
                text.AddToClassList("molca-references__cell");
                if (position == 1)
                    text.AddToClassList("molca-references__cell--identity");
                return text;
            }

            var cell = new VisualElement();
            cell.AddToClassList("molca-references__name-cell");

            var dot = new VisualElement();
            dot.AddToClassList("molca-status-dot");
            dot.AddToClassList("molca-references__row-dot");
            cell.Add(dot);

            var label = new Label();
            label.AddToClassList("molca-references__cell");
            label.AddToClassList("molca-references__cell--name");
            cell.Add(label);

            return cell;
        }

        private void BindCell(VisualElement element, int rowIndex, int position)
        {
            var node = NodeAt(rowIndex);
            if (node == null)
                return;

            var text = position switch
            {
                0 => node.Label,
                1 => node.Identity,
                2 => node.Source,
                3 => node.State,
                _ => node.Note,
            };

            if (position == 0)
            {
                var dot = element[0];
                dot.RemoveFromClassList("molca-status-dot--ok");
                dot.RemoveFromClassList("molca-status-dot--warn");
                dot.RemoveFromClassList("molca-status-dot--error");
                dot.RemoveFromClassList("molca-status-dot--idle");
                dot.AddToClassList(SeverityDot(node));

                var label = (Label)element[1];
                label.text = text;
                label.EnableInClassList("molca-references__cell--group", !node.IsActionable);
                element.tooltip = node.Tooltip;
                return;
            }

            ((Label)element).text = text;
        }

        private static string SeverityDot(ReferenceHubTreeNode node) => node.Severity switch
        {
            ReferenceFindingSeverity.Error => "molca-status-dot--error",
            ReferenceFindingSeverity.Warning => "molca-status-dot--warn",
            _ => "molca-status-dot--idle",
        };

        /// <summary>
        /// Orders two rows by the clicked column.
        /// </summary>
        /// <remarks>
        /// Guarded because the control hands over source indices during a sort and a stale index throws;
        /// losing the ordering is a far better outcome than losing the workspace to an exception raised
        /// inside a comparison.
        /// </remarks>
        private int CompareRows(int left, int right, int position)
        {
            try
            {
                var a = NodeAt(left);
                var b = NodeAt(right);
                if (a == null || b == null)
                    return 0;

                var text = position switch
                {
                    0 => string.CompareOrdinal(a.Label, b.Label),
                    1 => string.CompareOrdinal(a.Identity, b.Identity),
                    2 => string.CompareOrdinal(a.Source, b.Source),
                    3 => string.CompareOrdinal(a.State, b.State),
                    _ => string.CompareOrdinal(a.Note, b.Note),
                };

                // Ties break on severity so a sort by asset still puts the broken row of that asset first.
                return text != 0 ? text : b.Severity.CompareTo(a.Severity);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private ReferenceHubTreeNode NodeAt(int index)
        {
            try
            {
                var id = _tree.GetIdForIndex(index);
                return _nodesById.TryGetValue(id, out var node) ? node : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void OnSelectionChanged()
        {
            if (_suppressSelectionEvents)
                return;

            var nodes = _tree.selectedIndices.Select(NodeAt).Where(n => n != null).ToList();

            _selectedNode = nodes.FirstOrDefault();
            _selectedRows = nodes.Where(n => n.Row != null).Select(n => n.Row).ToList();

            // The group node a click landed on has no row of its own, but the batch below it does — selecting
            // "Not assigned" and pressing Point at Selection should mean its children, which is the only
            // reading of that gesture that is not a no-op.
            if (_selectedRows.Count == 0 && _selectedNode != null)
            {
                _selectedRows = ReferenceHubTargetTree.Flatten(_selectedNode.Children)
                    .Where(n => n.Row != null)
                    .Select(n => n.Row)
                    .ToList();
            }

            Session.SetSelectedKeys(Session.View, nodes.Select(n => n.Key));
            RefreshDetail();
            RefreshFilters();
            RefreshSafeRepairButton(Session.RepairIndex, Session.IsRunning);
        }

        /// <summary>The locator keys of everything currently selected, for narrowing a plan.</summary>
        private IReadOnlyCollection<string> SelectedOwnerKeys() =>
            _selectedRows.Select(r => r.OwnerKey)
                .Where(k => !string.IsNullOrEmpty(k))
                .Distinct(StringComparer.Ordinal)
                .ToList();

        private void ApplyResponsiveLayout(float width)
        {
            var narrow = width > 0 && width < NarrowWidth;
            if (narrow == _isNarrow)
                return;

            _isNarrow = narrow;
            _split.EnableInClassList("molca-references__split--narrow", narrow);
            _viewTabs.EnableInClassList("molca-references__views--narrow", narrow);
            _filterRow.EnableInClassList("molca-references__filters--narrow", narrow);
        }

        #endregion

        #region Refresh

        /// <summary>Rebuilds every part of the workspace from the session's current state.</summary>
        private void Refresh()
        {
            RefreshHeader();
            RefreshViewTabs();

            var view = Session.View;
            if (IsTableView(view))
            {
                CaptureExpansion();

                _rows = Session.FilteredRows(view);
                _roots = view == ReferenceHubViewKind.Targets
                    ? ReferenceHubTargetTree.Targets(Session.Tables, Session.Filter)
                    : ReferenceHubTargetTree.Flat(_rows);

                RefreshTableHeader(view);
                PopulateTree();

                _tree.style.display = DisplayStyle.Flex;
                _emptyNote.style.display = _roots.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                _emptyNote.text = EmptyMessage(view);
            }
            else
            {
                _rows = Array.Empty<ReferenceHubRow>();
                _roots = Array.Empty<ReferenceHubTreeNode>();
                _selectedRows = new List<ReferenceHubRow>();
                _selectedNode = null;
                _tree.style.display = DisplayStyle.None;
                _emptyNote.style.display = DisplayStyle.None;
            }

            RefreshFilters();
            RefreshDetail();

            // Again, after the tree has restored its selection: RefreshHeader ran before that and would
            // otherwise leave the button describing the previous selection's scope.
            RefreshSafeRepairButton(Session.RepairIndex, Session.IsRunning);
        }

        private void RefreshTableHeader(ReferenceHubViewKind view)
        {
            var columns = Columns(view);
            for (var index = 0; index < ColumnCount; index++)
            {
                var column = _tree.columns[ColumnIds[index]];
                column.title = columns[index].Heading;

                // An always-empty column would still take width and still be sortable, which reads as data
                // the view is failing to show rather than as a column this view has no use for.
                column.visible = !string.IsNullOrEmpty(columns[index].Heading);
            }
        }

        /// <summary>
        /// Hands the node tree to the control, restoring expansion and selection by key.
        /// </summary>
        /// <remarks>
        /// Ids come from the session and are keyed by node key rather than by position, so an audit that
        /// removes one finding does not move every expanded group one row down.
        /// </remarks>
        private void PopulateTree()
        {
            _nodesById.Clear();

            var items = _roots.Select(BuildItem).ToList();

            _suppressSelectionEvents = true;
            try
            {
                _tree.SetRootItems(items);
                _tree.Rebuild();

                var expanded = false;
                foreach (var pair in _nodesById)
                {
                    if (!Session.ExpandedTreeKeys.Contains(pair.Value.Key))
                        continue;

                    // refresh: false per item — refreshing once per expanded group would rebuild the
                    // viewport N times for one repopulate.
                    _tree.ExpandItem(pair.Key, expandAllChildren: false, refresh: false);
                    expanded = true;
                }

                if (expanded)
                    _tree.Rebuild();

                RestoreSelection();
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        private TreeViewItemData<ReferenceHubTreeNode> BuildItem(ReferenceHubTreeNode node)
        {
            var id = Session.TreeIdFor(node.Key);
            _nodesById[id] = node;

            return node.Children.Count == 0
                ? new TreeViewItemData<ReferenceHubTreeNode>(id, node)
                : new TreeViewItemData<ReferenceHubTreeNode>(
                    id, node, node.Children.Select(BuildItem).ToList());
        }

        /// <summary>Records which groups are open before the tree is thrown away and rebuilt.</summary>
        private void CaptureExpansion()
        {
            if (_nodesById.Count == 0)
                return;

            foreach (var pair in _nodesById)
            {
                if (pair.Value.Children.Count == 0)
                    continue;

                try
                {
                    if (_tree.IsExpanded(pair.Key))
                        Session.ExpandedTreeKeys.Add(pair.Value.Key);
                    else
                        Session.ExpandedTreeKeys.Remove(pair.Value.Key);
                }
                catch (Exception)
                {
                    // An id the control no longer knows: the node is gone, so its expansion is moot.
                }
            }
        }

        private void RestoreSelection()
        {
            var keys = new HashSet<string>(Session.SelectedKeys(Session.View), StringComparer.Ordinal);
            var ids = _nodesById.Where(p => keys.Contains(p.Value.Key)).Select(p => p.Key).ToList();

            _tree.SetSelectionByIdWithoutNotify(ids);

            var nodes = ids.Select(id => _nodesById[id]).ToList();
            _selectedNode = nodes.FirstOrDefault();
            _selectedRows = nodes.Where(n => n.Row != null).Select(n => n.Row).ToList();
        }

        /// <summary>
        /// The message shown when a view is empty, which has to distinguish "nothing to report" from
        /// "your filter hides everything" from "no audit has run".
        /// </summary>
        private string EmptyMessage(ReferenceHubViewKind view)
        {
            if (!Session.HasRun)
                return "No audit has run yet. Choose Refresh affected or Full audit above — both read only.";

            if (!Session.Filter.IsDefault && Session.AllRows(view).Count > 0)
                return $"Every row is hidden by the current filter ({Session.Filter.Describe()}).";

            return view switch
            {
                ReferenceHubViewKind.Issues => Session.Snapshot.Coverage.IsComplete
                    ? "No findings over complete coverage."
                    : $"No findings, but coverage is incomplete ({Session.Snapshot.Coverage.DescribeGaps()}), "
                      + "so this is not a clean result.",
                ReferenceHubViewKind.References => "No reference sites were found in the audited scope.",
                _ => "No targets were found in the audited scope. Select a GameObject and use "
                     + "'Make selection referenceable' in the detail panel to author one.",
            };
        }

        #endregion

        #region Detail panel

        private void RefreshDetail()
        {
            _detail.Clear();

            switch (Session.View)
            {
                case ReferenceHubViewKind.Coverage:
                    BuildCoverageDetail();
                    return;
                case ReferenceHubViewKind.Runtime:
                    BuildRuntimeDetail();
                    return;
            }

            if (_selectedRows.Count > 1)
            {
                BuildBatchCard();
                return;
            }

            if (_selectedNode == null)
            {
                _detail.Add(Note(
                    "Select a row to see its full locator, its candidates, and the changes available to it. "
                    + "Select several to act on them together."));
                BuildCreateTargetCard();
                return;
            }

            if (_selectedNode.Kind == ReferenceHubTreeNodeKind.RefTypeGroup)
            {
                BuildRefTypeCard(_selectedNode);
                return;
            }

            var row = _selectedRows.FirstOrDefault();
            if (row == null)
            {
                _detail.Add(Note(_selectedNode.Tooltip));
                return;
            }

            BuildRowDetail(row);
            BuildAuthoringCard(row);
            BuildConnectionsCard(row);
            BuildActionsCard(row);
        }

        private void BuildRowDetail(ReferenceHubRow row)
        {
            var card = new MolcaSectionCard(
                string.IsNullOrEmpty(row.Code) ? row.Title : $"{row.Code}  {row.Title}",
                subtitle: row.Kind.ToString());
            _detail.Add(card);

            if (!string.IsNullOrEmpty(row.Summary))
                card.Body.Add(Note(row.Summary));

            AddField(card.Body, "Asset", row.AssetPath);
            AddField(card.Body, "Owner", row.Owner);
            AddField(card.Body, "Property", row.PropertyPath);
            AddField(card.Body, "Stored target", row.StoredTarget);
            AddField(card.Body, "Scope", $"{row.Scope} (v1 references have no scope component)");
            AddField(card.Body, "Source", row.SourceKind);
            AddField(card.Body, "Expected type", row.ExpectedType);
            AddField(card.Body, "Resolution", row.ResolutionState);
            if (row.Kind == ReferenceHubRowKind.Provider)
            {
                AddField(card.Body, "Inbound",
                    row.InboundCount == row.ClaimingCount
                        ? $"{row.InboundCount} reference(s) resolve here"
                        : $"{row.InboundCount} resolve here, {row.ClaimingCount} store this Ref Id");
            }

            if (row.IsReadOnly)
                card.Body.Add(Note("This asset is read-only, so nothing here can write to it."));

            if (row.IsLegacyFallback)
            {
                card.Body.Add(Note(
                    "This reference stores no Ref Type, so it depends on the ID-only compatibility fallback. "
                    + "That path refuses to resolve the moment a second object carries the same Ref Id."));
            }

            AddSeverityExplanation(card.Body, row);
        }

        /// <summary>
        /// States why the row has the severity it has, and whether that severity is configurable.
        /// </summary>
        /// <remarks>
        /// A user who can see that REF002 is fixed at error <i>because the runtime refuses an ambiguous
        /// lookup</i> stops looking for the setting that would turn it off.
        /// </remarks>
        private static void AddSeverityExplanation(VisualElement parent, ReferenceHubRow row)
        {
            if (row.Kind != ReferenceHubRowKind.Issue || string.IsNullOrEmpty(row.Code))
                return;

            if (!int.TryParse(row.Code.Substring(3), out var numeric)
                || !Enum.IsDefined(typeof(ReferenceFindingCode), numeric))
                return;

            var code = (ReferenceFindingCode)numeric;
            var text = ReferenceSeverityPolicy.IsNonLowerable(code)
                ? $"Severity {row.Severity} is fixed for {row.Code}: this is a runtime failure, and a project "
                  + "cannot configure it down to a warning."
                : $"Severity {row.Severity} for {row.Code} comes from the audit policy "
                  + $"(baseline {ReferenceHubPolicyStore.Baseline(code)}). Editor severities are configurable "
                  + "behind Policy in the header; builds always use the production policy.";
            parent.Add(Note(text));
        }

        #endregion

        #region Authoring

        /// <summary>
        /// The identity editor for whichever target the selected row concerns.
        /// </summary>
        /// <remarks>
        /// <para>Offered on a reference row as well as on a target row, pointed at what the reference
        /// reaches. "Rename the thing this points at" is the same act from either end, and making the author
        /// navigate to the target first would only make it likelier they rename it in the Inspector instead,
        /// which is the change that breaks the inbound set.</para>
        ///
        /// <para>Every button here builds a plan naming every reference it would move and shows it before
        /// anything is written.</para>
        /// </remarks>
        private void BuildAuthoringCard(ReferenceHubRow row)
        {
            var providerKey = row.ProviderKey;
            if (string.IsNullOrEmpty(providerKey))
                return;

            var provider = Session.Snapshot.FindProvider(providerKey);
            if (provider == null)
                return;

            var inbound = ReferenceAuthoringPlanner.InboundSites(Session.Snapshot, provider);
            var card = new MolcaSectionCard(
                "Identity",
                subtitle: $"'{provider.DisplayName}' · {inbound.Count} inbound reference(s) move with it");
            _detail.Add(card);

            if (Session.IsStale)
            {
                card.Body.Add(Note(
                    "The snapshot is stale, so the inbound set cannot be trusted and no identity change can "
                    + "be planned from it. Re-run the audit first."));
                return;
            }

            if (provider.IsReadOnly)
            {
                card.Body.Add(Note("This target lives in a read-only asset, so its identity cannot change."));
                return;
            }

            if (!string.Equals(_draftProviderKey, providerKey, StringComparison.Ordinal))
            {
                _draftProviderKey = providerKey;
                _draftRefId = provider.RefId;
                _draftRefType = provider.RefType;
            }

            var refTypeField = new TextField("Ref Type") { value = _draftRefType };
            refTypeField.AddToClassList("molca-references__authoring-field");
            refTypeField.RegisterValueChangedCallback(evt => _draftRefType = evt.newValue);
            card.Body.Add(refTypeField);

            var retype = MolcaButtons.Mini("Change type…", () =>
                ReferenceHubAuthoring.PreviewAndApply(
                    () => ReferenceAuthoringPlanner.PlanRetype(
                        Session.Snapshot, providerKey, refTypeField.value),
                    "Change Ref Type?"));
            retype.tooltip =
                "Rewrite this target's Ref Type and every reference that stores the old one, in one plan.";
            card.Body.Add(retype);

            var refIdField = new TextField("Ref Id") { value = _draftRefId };
            refIdField.AddToClassList("molca-references__authoring-field");
            refIdField.RegisterValueChangedCallback(evt => _draftRefId = evt.newValue);
            card.Body.Add(refIdField);

            var idActions = new VisualElement();
            idActions.AddToClassList("molca-references__actions");
            card.Body.Add(idActions);

            var suggest = MolcaButtons.Mini("Suggest readable id", () =>
                refIdField.value = ReferenceIdSuggestion.Suggest(
                    provider.DisplayName,
                    provider.RefType,
                    Session.Snapshot.Providers
                        .Where(p => p.RefType == provider.RefType && p.ProviderKey != providerKey)
                        .Select(p => p.RefId)));
            suggest.tooltip =
                "Propose a kebab-case id from the display name, per the framework naming convention. "
                + "Generated ref_<guid> ids are collision-safe and unreadable in a diff or a duplicate report.";
            idActions.Add(suggest);

            var rename = MolcaButtons.Mini("Rename…", () =>
                ReferenceHubAuthoring.PreviewAndApply(
                    () => ReferenceAuthoringPlanner.PlanRename(
                        Session.Snapshot, providerKey, refIdField.value),
                    "Rename Target?"));
            rename.tooltip =
                "Rewrite this target's Ref Id and every reference that names it, in one plan. Refused when "
                + "the id is already duplicated: nothing then records which references meant this target.";
            idActions.Add(rename);

            BuildScopeControl(card.Body, provider);
        }

        /// <summary>
        /// The scope selector, offered only for a component that actually declares a scope.
        /// </summary>
        private void BuildScopeControl(VisualElement parent, ReferenceProviderRecord provider)
        {
            var current = ReferenceHubAuthoring.TryReadScope(provider.Locator);
            if (current == null)
                return;

            var field = new EnumField("Scope", current.Value);
            field.AddToClassList("molca-references__authoring-field");
            field.tooltip =
                "Which space this id must be unique in. Scope is part of identity, so moving a target "
                + "between scopes changes what the runtime registers it as.";
            parent.Add(field);

            parent.Add(MolcaButtons.Mini("Change scope…", () =>
                ReferenceHubAuthoring.PreviewAndApply(
                    () => ReferenceAuthoringPlanner.PlanScope(
                        Session.Snapshot, provider.ProviderKey,
                        current.Value, (ReferenceScopeKind)field.value),
                    "Change Reference Scope?")));
        }

        /// <summary>
        /// The Ref Type group's own editor: fold this vocabulary entry into another one.
        /// </summary>
        /// <remarks>
        /// Ref Type is free text on the component, so a project accumulates <c>valve</c> beside
        /// <c>Valve</c> and each near-duplicate is a REF005 waiting for a reference to store the wrong
        /// spelling. Nothing in the editor previously showed the vocabulary as a thing that could be tidied.
        /// </remarks>
        private void BuildRefTypeCard(ReferenceHubTreeNode group)
        {
            var refType = group.Label;
            var card = new MolcaSectionCard($"Ref Type \"{refType}\"", subtitle: group.Identity);
            _detail.Add(card);
            card.Body.Add(Note(group.Tooltip));

            if (Session.IsStale || refType == ReferenceHubTargetTree.NoRefTypeLabel)
            {
                card.Body.Add(Note(Session.IsStale
                    ? "The snapshot is stale, so nothing can be planned from it. Re-run the audit first."
                    : "These targets declare no Ref Type at all, so there is no vocabulary entry to merge. "
                      + "Give each one a type from its own row."));
                return;
            }

            var others = Session.Snapshot.Providers
                .Select(p => p.RefType)
                .Where(t => !string.IsNullOrEmpty(t) && t != refType)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            if (others.Count == 0)
            {
                card.Body.Add(Note("This is the only Ref Type in the project, so there is nothing to merge into."));
                return;
            }

            var destination = new DropdownField("Merge into", others, 0);
            destination.AddToClassList("molca-references__authoring-field");
            card.Body.Add(destination);

            var merge = MolcaButtons.Mini("Merge…", () =>
                ReferenceHubAuthoring.PreviewAndApply(
                    () => ReferenceAuthoringPlanner.PlanMergeRefType(
                        Session.Snapshot, refType, destination.value),
                    "Merge Ref Types?"));
            merge.tooltip =
                "Move every target with this Ref Type onto the chosen one, carrying their inbound references. "
                + "Refused wholesale if any id would then be duplicated.";
            card.Body.Add(merge);
        }

        /// <summary>Batch actions for a multi-row selection.</summary>
        private void BuildBatchCard()
        {
            var references = _selectedRows.Where(r => !string.IsNullOrEmpty(r.SiteKey)).ToList();
            var card = new MolcaSectionCard(
                $"{_selectedRows.Count} rows selected",
                subtitle: $"{references.Count} of them are references");
            _detail.Add(card);

            if (Session.IsStale)
            {
                card.Body.Add(Note(
                    "The snapshot is stale, so no plan can be built from it. Re-run the audit first."));
                return;
            }

            card.Body.Add(Note(
                "Preview safe repairs in the header is narrowed to this selection while it is active."));

            if (references.Count == 0)
                return;

            var siteKeys = references.Select(r => r.SiteKey).ToList();
            AddPointAtSelection(card.Body, siteKeys);

            var clear = MolcaButtons.Mini($"Clear {references.Count} reference(s)…", () =>
                ReferenceHubAuthoring.PreviewAndApply(
                    () => ReferenceAuthoringPlanner.PlanClearMany(Session.Snapshot, siteKeys),
                    "Clear References?"));
            clear.tooltip =
                "Build a plan that unsets these references. Never part of a batch repair: an unset reference "
                + "passes validation without anything being fixed.";
            card.Body.Add(clear);
        }

        /// <summary>
        /// "Point these at whatever is selected in the hierarchy", which closes the loop back from the scene.
        /// </summary>
        /// <remarks>
        /// The property drawer has always been able to send the user here; nothing sent them back. Without
        /// this, re-pointing a reference from the workspace meant finding its owner in the Inspector and
        /// using the picker — the round trip that made the workspace read-only in practice.
        /// </remarks>
        private void AddPointAtSelection(VisualElement parent, IReadOnlyList<string> siteKeys)
        {
            var providerKey = ReferenceHubAuthoring.SelectionProviderKey(Session.Snapshot);
            var described = ReferenceHubAuthoring.DescribeSelection();

            if (string.IsNullOrEmpty(providerKey))
            {
                parent.Add(Note(string.IsNullOrEmpty(described)
                    ? "Select a referenceable object in the hierarchy to point these at it."
                    : $"'{described}' is not a discovered target, so nothing can point at it yet. Give it a "
                      + "Referenceable component, or re-audit if you just added one."));
                return;
            }

            var provider = Session.Snapshot.FindProvider(providerKey);
            var point = MolcaButtons.Mini(
                $"Point {siteKeys.Count} reference(s) at '{provider?.DisplayName ?? described}'…",
                () => ReferenceHubAuthoring.PreviewAndApply(
                    () => ReferenceAuthoringPlanner.PlanRewire(Session.Snapshot, siteKeys, providerKey),
                    "Re-point References?"));
            point.tooltip =
                "Build a plan pointing these references at the hierarchy selection. References whose declared "
                + "type cannot accept it are left alone and named in the plan.";
            parent.Add(point);
        }

        /// <summary>Creates targets from the hierarchy selection.</summary>
        private void BuildCreateTargetCard()
        {
            var described = ReferenceHubAuthoring.DescribeSelection();
            if (string.IsNullOrEmpty(described))
                return;

            var card = new MolcaSectionCard("Author a target", subtitle: described);
            _detail.Add(card);

            var typeField = new TextField("Ref Type") { value = "Referenceable" };
            typeField.AddToClassList("molca-references__authoring-field");
            card.Body.Add(typeField);

            card.Body.Add(Note(
                "Adds a Referenceable component to each selected GameObject that has none, with a kebab-case "
                + "id derived from its name. No plan is shown because nothing can reference an object that is "
                + "not yet a target, so this cannot re-point anything."));

            card.Body.Add(MolcaButtons.Mini("Make selection referenceable", () =>
            {
                var created = ReferenceHubAuthoring.MakeSelectionReferenceable(
                    Session.Snapshot, typeField.value);

                if (created == 0)
                {
                    Debug.Log("[ReferenceSystem] Every selected object is already referenceable.");
                    return;
                }

                Debug.Log($"[ReferenceSystem] {created} object(s) are now referenceable. Re-audit to see them.");
                Refresh();
            }));
        }

        #endregion

        #region Connections

        /// <summary>What this row reaches, what reaches it, and the neighbourhood around it.</summary>
        private void BuildConnectionsCard(ReferenceHubRow row)
        {
            var snapshot = Session.Snapshot;
            var card = new MolcaSectionCard("Connections");
            _detail.Add(card);

            if (!string.IsNullOrEmpty(row.SiteKey))
                AddPointAtSelection(card.Body, new[] { row.SiteKey });

            AddCandidates(card.Body, row);

            if (row.Kind == ReferenceHubRowKind.Issue && !string.IsNullOrEmpty(row.SiteKey)
                && row.IsAssigned && !row.IsReadOnly && !Session.IsStale)
            {
                var clear = MolcaButtons.Mini("Clear reference…", () =>
                    ReferenceHubAuthoring.PreviewAndApply(
                        () => ReferenceAuthoringPlanner.PlanClearMany(snapshot, new[] { row.SiteKey }),
                        "Clear Reference?"));
                clear.tooltip =
                    "Build a plan that unsets this reference. Never part of a batch: an unset reference "
                    + "passes validation without anything being fixed.";
                card.Body.Add(clear);
            }

            // The neighbourhood used to be a whole tab, which meant leaving the table to see it and hunting
            // for the selection once you got there. It is a peek, so it lives beside the row.
            var graph = new Foldout { text = "Neighbourhood", value = false };
            graph.AddToClassList("molca-references__graph");
            card.Body.Add(graph);

            graph.RegisterValueChangedCallback(evt =>
            {
                if (!evt.newValue || graph.contentContainer.childCount > 0)
                    return;

                var source = ReferenceHubGraph.BuildMermaid(snapshot, row);
                try
                {
                    graph.Add(new MolcaMermaidView(MolcaMermaid.ParseFlowchart(source)));
                }
                catch (Exception e)
                {
                    // A graph that will not lay out must not cost the user the workspace; the source is
                    // still useful, so it is offered as text.
                    Debug.LogWarning($"[ReferenceSystem] Neighbourhood graph could not be drawn: {e.Message}");
                    graph.Add(Note(source));
                }

                graph.Add(Note(
                    $"One hop from this row, at most {ReferenceHubGraph.MaxNeighbours} neighbours per side. "
                    + "A solid arrow is what the runtime resolves; a dashed one is a match that does not win."));
            });
        }

        private void AddCandidates(VisualElement parent, ReferenceHubRow row)
        {
            if (row.CandidateProviderKeys.Count == 0)
                return;

            var snapshot = Session.Snapshot;
            parent.Add(FieldLabel($"Candidates ({row.CandidateProviderKeys.Count})"));

            foreach (var key in row.CandidateProviderKeys)
            {
                var provider = snapshot.FindProvider(key);
                if (provider == null)
                    continue;

                var line = new VisualElement();
                line.AddToClassList("molca-references__candidate");
                parent.Add(line);

                var label = new Label($"{provider.DisplayName} · {provider.RefType}:{provider.RefId}");
                label.AddToClassList("molca-references__candidate-label");
                label.tooltip = provider.Locator.ToString();
                line.Add(label);

                line.Add(MolcaButtons.Mini("Select", () =>
                    ReferenceHubNavigation.SelectAndPing(provider.Locator, "candidate")));

                // Offered on a healthy row too. "Actually, point this at the other valve" is an ordinary
                // authoring act, and restricting it to findings meant the only way to perform it was the
                // Inspector picker — which cannot see the other references sharing this target.
                if (!string.IsNullOrEmpty(row.SiteKey) && !row.IsReadOnly && !Session.IsStale)
                {
                    var use = MolcaButtons.Mini("Point here…", () =>
                        ReferenceHubAuthoring.PreviewAndApply(
                            () => ReferenceAuthoringPlanner.PlanRewire(
                                snapshot, new[] { row.SiteKey }, key),
                            "Re-point Reference?"));
                    use.tooltip =
                        "Build a repair plan pointing this reference at this target. You approve the plan "
                        + "before anything is written.";
                    line.Add(use);
                }
            }
        }

        private void BuildActionsCard(ReferenceHubRow row)
        {
            var card = new MolcaSectionCard("Go to");
            _detail.Add(card);

            var actions = new VisualElement();
            actions.AddToClassList("molca-references__actions");
            card.Body.Add(actions);

            var snapshot = Session.Snapshot;
            var ownerLocator = OwnerLocatorFor(row, snapshot);

            actions.Add(MolcaButtons.Mini("Select owner",
                () => ReferenceHubNavigation.SelectAndPing(ownerLocator, "owner")));

            var provider = string.IsNullOrEmpty(row.ProviderKey) ? null : snapshot.FindProvider(row.ProviderKey);
            if (provider != null)
            {
                actions.Add(MolcaButtons.Mini("Ping target",
                    () => ReferenceHubNavigation.SelectAndPing(provider.Locator, "target")));
            }

            if (ReferenceHubNavigation.CanOpenScene(row.AssetPath))
                actions.Add(MolcaButtons.Mini("Open scene", () => ReferenceHubNavigation.OpenScene(row.AssetPath)));

            if (ReferenceHubNavigation.CanOpenPrefab(row.AssetPath))
                actions.Add(MolcaButtons.Mini("Open prefab", () => ReferenceHubNavigation.OpenPrefab(row.AssetPath)));

            if (!string.IsNullOrEmpty(row.AssetPath))
                actions.Add(MolcaButtons.Mini("Reveal asset", () => ReferenceHubNavigation.RevealAsset(row.AssetPath)));

            actions.Add(MolcaButtons.Mini("Copy diagnostic",
                () => ReferenceHubNavigation.Copy(ReferenceHubNavigation.BuildDiagnostic(row, snapshot))));
        }

        private static ReferenceObjectLocator OwnerLocatorFor(ReferenceHubRow row, ReferenceAuditSnapshot snapshot)
        {
            var resolution = string.IsNullOrEmpty(row.SiteKey) ? null : snapshot.FindResolution(row.SiteKey);
            if (resolution != null)
                return resolution.Site.OwnerLocator;

            var provider = string.IsNullOrEmpty(row.ProviderKey) ? null : snapshot.FindProvider(row.ProviderKey);
            return provider?.Locator ?? default;
        }

        #endregion

        #region Coverage / Runtime views

        private void BuildCoverageDetail()
        {
            var snapshot = Session.Snapshot;
            var card = new MolcaSectionCard(
                "Coverage",
                subtitle: Session.HasRun
                    ? snapshot.Coverage.DescribeGaps()
                    : "no audit has run yet");
            _detail.Add(card);

            if (!Session.HasRun)
            {
                card.Body.Add(Note(
                    "Coverage is what makes a clean result mean anything: it records what the audit looked at "
                    + "and what it did not. Run an audit to populate it."));
                BuildLoadSetCard();
                return;
            }

            foreach (var entry in snapshot.Coverage.Entries)
            {
                var row = new VisualElement();
                row.AddToClassList("molca-references__coverage-row");
                card.Body.Add(row);

                var dot = new VisualElement();
                dot.AddToClassList("molca-status-dot");
                dot.AddToClassList(entry.Status switch
                {
                    ReferenceCoverageStatus.Scanned => "molca-status-dot--ok",
                    ReferenceCoverageStatus.Failed => "molca-status-dot--error",
                    _ => "molca-status-dot--warn",
                });
                row.Add(dot);

                var text = new Label(entry.Status == ReferenceCoverageStatus.Scanned
                    ? $"{entry.Category} — {entry.Count} scanned"
                    : $"{entry.Category} — {entry.Status}{(entry.IsRequired ? string.Empty : " (optional)")}: {entry.Reason}");
                text.AddToClassList("molca-references__coverage-label");
                row.Add(text);
            }

            if (!snapshot.Coverage.IsComplete)
            {
                card.Body.Add(Note(
                    "Required coverage is incomplete, so this audit cannot report Clean no matter how few "
                    + "findings it produced. Configure the missing categories, or accept the result as a "
                    + "statement about part of the project."));
            }

            if (snapshot.Coverage.HasFailures)
            {
                card.Body.Add(Note(
                    "A category was attempted and failed, which is why the snapshot is marked stale: "
                    + "re-running may produce a better answer, and repair is blocked until it does."));
            }

            BuildLoadSetCard();
            BuildLegacyMigrationCard(snapshot);
            BuildLegacyIdListCard(snapshot);
            BuildIndexCard(snapshot);
        }

        /// <summary>
        /// Offers to remove the authored id lists the audit index replaced.
        /// </summary>
        /// <remarks>
        /// Gated on a healthy audit on purpose. Dropping the old lists while the new index cannot answer
        /// would leave the project with neither, and the cleanup would look like the cause of whatever
        /// broke next.
        /// </remarks>
        private void BuildLegacyIdListCard(ReferenceAuditSnapshot snapshot)
        {
            var state = ReferenceLegacyIdListCleanup.Inspect(snapshot);
            if (!state.HasEntries)
                return;

            var card = new MolcaSectionCard("Legacy cached id lists", subtitle: state.Describe());
            _detail.Add(card);

            card.Body.Add(Note(
                "These authored lists were the original index: a hand-maintained snapshot of every id, "
                + "written by a scan and read by validation. That made them a second source of truth able "
                + "to disagree with the assets they described — an id deleted from a scene stayed listed "
                + "forever, so validation reported providers that no longer existed. Nothing reads them as "
                + "authoritative any more."));

            if (!state.CanRemove)
            {
                card.Body.Add(Note($"Not offered yet: {state.BlockedReason}."));
                return;
            }

            card.Body.Add(MolcaButtons.Mini("Remove legacy cached id lists", () =>
            {
                ReferenceLegacyIdListCleanup.Remove(snapshot);
                Refresh();
            }));
        }

        /// <summary>
        /// The scene load sets, which are what cross-scene availability is judged against — and, now, an
        /// editor for them.
        /// </summary>
        /// <remarks>
        /// Under Coverage because an inferred load set is a coverage limitation, not a setting: it means
        /// nobody has told the tooling which scenes load together, so any cross-scene conclusion it draws
        /// rests on a guess. Saying so here is the difference between a limitation and a lie — and pointing
        /// at a JSON file to fix it was the difference between a limitation and a permanent one.
        /// </remarks>
        private void BuildLoadSetCard()
        {
            var card = new MolcaSectionCard("Scene load sets", subtitle: ReferenceLoadSetStore.Describe());
            _detail.Add(card);

            foreach (var set in ReferenceLoadSetStore.Sets)
                card.Body.Add(Note($"• {set.Describe()}"));

            if (ReferenceLoadSetStore.IsInferred)
            {
                card.Body.Add(Note(
                    "The inferred set treats every enabled scene after the first as deferred, which is the "
                    + "honest reading of unknown load order — it never claims two scenes are loaded "
                    + "together. Author explicit sets below to validate additive loading properly."));
            }

            var editor = new Foldout { text = "Edit load sets", value = false };
            card.Body.Add(editor);
            editor.RegisterValueChangedCallback(evt =>
            {
                if (!evt.newValue || editor.contentContainer.childCount > 0)
                    return;
                editor.Add(new ReferenceHubLoadSetEditor(Refresh));
            });
        }

        /// <summary>
        /// The v1-to-v2 scope migration proposal: how many legacy references could be re-homed
        /// automatically, and how many need a decision.
        /// </summary>
        /// <remarks>
        /// A proposal, never an action. The planner only narrows a scope when the data forces one
        /// conclusion, because a wrong scope turns a working reference into one that cannot resolve —
        /// silently, and across a whole project at once.
        /// </remarks>
        private void BuildLegacyMigrationCard(ReferenceAuditSnapshot snapshot)
        {
            var plan = ReferenceScopeMigrationPlanner.Plan(snapshot);
            if (plan.Migrations.Count == 0)
                return;

            var card = new MolcaSectionCard("Scope migration", subtitle: plan.Describe());
            _detail.Add(card);

            if (plan.Automatic.Count > 0)
            {
                card.Body.Add(FieldLabel($"Unambiguous ({plan.Automatic.Count})"));
                foreach (var migration in plan.Automatic.Take(20))
                    card.Body.Add(Note($"• {Short(migration.AssetPath)} → {migration.ProposedScope}: {migration.Rationale}"));
                if (plan.Automatic.Count > 20)
                    card.Body.Add(Note($"… +{plan.Automatic.Count - 20} more"));
            }

            if (plan.NeedsChoice.Count > 0)
            {
                card.Body.Add(FieldLabel($"Needs a decision ({plan.NeedsChoice.Count})"));
                foreach (var migration in plan.NeedsChoice.Take(20))
                    card.Body.Add(Note($"• {Short(migration.AssetPath)}: {migration.Rationale}"));
                if (plan.NeedsChoice.Count > 20)
                    card.Body.Add(Note($"… +{plan.NeedsChoice.Count - 20} more"));
            }

            card.Body.Add(Note(
                "Nothing is applied from here. Scope migration rewrites serialized data in every affected "
                + "asset, so it runs as an explicit, previewed repair — not as a side effect of opening a "
                + "view."));

            card.Body.Add(MolcaButtons.Mini("Copy proposal", () => ReferenceHubNavigation.Copy(
                string.Join("\n", plan.Migrations.Select(m => $"{m.AssetPath}|{m.PropertyPath}\t{m}")))));
        }

        private static string Short(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "<unknown>";

            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        /// <summary>
        /// Reports the derived on-disk index: where it is, how big, and whether this result came from it.
        /// </summary>
        /// <remarks>
        /// It lives under Coverage because that is where the workspace answers "how much do you actually
        /// know?", and "this result was restored from a cache" is part of that answer. The index is derived
        /// data under <c>Library/</c> and is never committed — a committed index would be a second source of
        /// truth able to disagree with the assets it describes.
        /// </remarks>
        private void BuildIndexCard(ReferenceAuditSnapshot snapshot)
        {
            var card = new MolcaSectionCard("Index", subtitle: ReferenceIndexScheduler.Describe());
            _detail.Add(card);

            card.Body.Add(Note(ReferenceIndexScheduler.Origin));

            if (!snapshot.CanPersist)
            {
                card.Body.Add(Note(
                    $"This result is not written to the index: {snapshot.PersistBlockedReason}. It was read "
                    + "from state that is not on disk, so a later session could not verify it still holds. "
                    + "Save your work and re-audit to store it."));
            }

            var pending = ReferenceIndexScheduler.PendingChanges;
            if (pending.Count > 0)
            {
                card.Body.Add(Note(
                    $"{pending.Count} asset(s) changed since this result: "
                    + string.Join(", ", pending.Take(5))
                    + (pending.Count > 5 ? $", +{pending.Count - 5} more" : string.Empty)));
            }

            var actions = new VisualElement();
            actions.AddToClassList("molca-references__actions");
            card.Body.Add(actions);

            actions.Add(MolcaButtons.Mini("Clear index", () =>
            {
                ReferenceIndexScheduler.Clear();
                Refresh();
            }));

            actions.Add(MolcaButtons.Mini("Reveal index", () =>
                EditorUtility.RevealInFinder(ReferenceIndexStore.AbsolutePath)));
        }

        private void BuildRuntimeDetail()
        {
            var state = ReferenceHubRuntimeState.Read(
                Session.HasRun ? Session.Snapshot : null,
                EditorApplication.isPlaying);

            var card = new MolcaSectionCard("Runtime registry", subtitle: state.Describe());
            _detail.Add(card);

            if (!state.IsAvailable)
            {
                card.Body.Add(Note(state.UnavailableReason));
                return;
            }

            if (state.PerType.Count > 0)
            {
                card.Body.Add(FieldLabel("Registrations by type"));
                foreach (var pair in state.PerType)
                    card.Body.Add(Note($"{pair.Key}: {pair.Value}"));
            }

            // The two mismatch lists are the reason this view exists: they separate "the serialized data is
            // wrong" from "the object never registered", which a failed resolve alone cannot tell you.
            if (state.ExpectedButMissing.Count > 0)
            {
                card.Body.Add(FieldLabel($"Expected but not registered ({state.ExpectedButMissing.Count})"));
                card.Body.Add(Note(
                    "The audit found these providers in serialized data, but the runtime registry does not "
                    + "hold them — a disabled object, an unloaded scene, or a lifecycle mistake."));
                foreach (var key in state.ExpectedButMissing.Take(20))
                    card.Body.Add(Note($"• {key}"));
            }

            if (state.RegisteredButUnknown.Count > 0)
            {
                card.Body.Add(FieldLabel($"Registered but outside the audit scope ({state.RegisteredButUnknown.Count})"));
                card.Body.Add(Note(
                    "Registered at runtime but not found by the audit — created at runtime, or in a scene this "
                    + "scope did not cover."));
                foreach (var key in state.RegisteredButUnknown.Take(20))
                    card.Body.Add(Note($"• {key}"));
            }

            if (state.OpenScopeCount > 0)
            {
                card.Body.Add(FieldLabel($"Open prefab scopes ({state.OpenScopeCount})"));
                card.Body.Add(Note(
                    "Each live instance of a scoped prefab holds its own scope, so identical authored "
                    + "ids inside them do not collide."));
            }

            if (state.Entries.Count > 0)
            {
                card.Body.Add(FieldLabel($"Live registrations ({state.Entries.Count})"));
                foreach (var entry in state.Entries.Take(60))
                    card.Body.Add(Note($"• {entry.Key} — {entry.DisplayName} ({entry.TypeName})"));
                if (state.Entries.Count > 60)
                    card.Body.Add(Note($"… +{state.Entries.Count - 60} more"));
            }

            BuildDiagnosticsSection(state);
        }

        /// <summary>
        /// The registry's recent event stream, newest last.
        /// </summary>
        /// <remarks>
        /// The two lists above answer "what is registered right now"; this answers "what happened to
        /// get there". A conflict or an ambiguous fallback is invisible in a steady-state listing —
        /// the losing registration simply is not there — so without the stream the most diagnostic
        /// events in the system leave no trace the Hub can show.
        /// </remarks>
        private void BuildDiagnosticsSection(ReferenceHubRuntimeState state)
        {
            if (state.Diagnostics.Count == 0)
                return;

            var problems = state.Diagnostics.Where(d => d.IsProblem).ToList();

            var card = new MolcaSectionCard(
                "Registry activity",
                subtitle: problems.Count > 0
                    ? $"{state.Diagnostics.Count} recent event(s), {problems.Count} of them problems"
                    : $"{state.Diagnostics.Count} recent event(s)");
            _detail.Add(card);

            if (problems.Count > 0)
            {
                card.Body.Add(FieldLabel($"Problems ({problems.Count})"));
                foreach (var entry in problems.Take(20))
                    card.Body.Add(Note($"• {entry}"));
            }

            card.Body.Add(FieldLabel("Recent events"));
            foreach (var entry in state.Diagnostics.Reverse().Take(40))
                card.Body.Add(Note($"• {entry}"));

            card.Body.Add(MolcaButtons.Mini("Copy events", () =>
                ReferenceHubNavigation.Copy(string.Join("\n", state.Diagnostics.Select(d => d.ToString())))));
        }

        #endregion

        #region Small builders

        private static Label Note(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-references__note");
            return label;
        }

        private static Label FieldLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-references__field-title");
            return label;
        }

        private static void AddField(VisualElement parent, string label, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            var row = new VisualElement();
            row.AddToClassList("molca-references__field");
            parent.Add(row);

            var key = new Label(label);
            key.AddToClassList("molca-references__field-key");
            row.Add(key);

            var text = new Label(value) { tooltip = value };
            text.AddToClassList("molca-references__field-value");
            row.Add(text);
        }

        #endregion
    }
}
