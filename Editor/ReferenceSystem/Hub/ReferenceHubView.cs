using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.ReferenceSystem.Repair;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// The References Hub workspace: continuous, navigable reference health with previewed repair.
    /// </summary>
    /// <remarks>
    /// <para>A projection of <see cref="ReferenceAuditSnapshot"/> and nothing else. The view holds no scan
    /// logic, no resolution rule and no repair decision of its own — those live in the audit engine and the
    /// repair planner, so what the workspace shows and what a build enforces cannot drift apart. Everything
    /// this class contributes is presentation, filtering, and navigation.</para>
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

        /// <summary>Row height of the virtualized tables.</summary>
        private const int RowHeight = 22;

        /// <summary>Number of text columns in a table row, after the severity dot.</summary>
        internal const int ColumnCount = 6;

        /// <summary>
        /// The USS width class of each column, in order. Shared by the header and the rows so the two cannot
        /// drift out of alignment — a header that does not sit over its column is worse than none.
        /// </summary>
        internal static readonly string[] ColumnClasses =
        {
            "molca-references__cell--code",
            "molca-references__cell--title",
            "molca-references__cell--owner",
            "molca-references__cell--target",
            "molca-references__cell--state",
            "molca-references__cell--repair",
        };

        /// <summary>
        /// Column headings per view. The columns hold different things in each table — a site's "title" is its
        /// property path, a provider's is its display name — so naming them once globally would mislabel two
        /// tables out of three.
        /// </summary>
        internal static (string Heading, string Tooltip)[] Columns(ReferenceHubViewKind kind) => kind switch
        {
            ReferenceHubViewKind.References => new[]
            {
                ("", ""),
                ("Property", "The serialized field that holds the reference."),
                ("Source", "The asset and the object that declares the field."),
                ("Stored target", "The serialized RefType:RefId this field asks for. '<unset>' means no reference is assigned."),
                ("Resolves to", "What the runtime would do with this reference — see the detail panel for why."),
                ("", ""),
            },
            ReferenceHubViewKind.Providers => new[]
            {
                ("", ""),
                ("Provider", "The target's display name. Presentation only — never its identity."),
                ("Source", "The asset and the object that provides the reference."),
                ("Identity · inbound", "The provider's RefType:RefId, and how many references resolve to it. A count of 0 next to a duplicated id means nothing reaches it."),
                ("Runtime", "Whether the runtime registry ever holds this provider. A prefab or ScriptableObject provider is never registered, so it cannot answer a lookup."),
                ("", ""),
            },
            _ => new[]
            {
                ("Code", "The stable REFnnn finding code. It reads the same in Doctor, build errors and MCP payloads."),
                ("Finding", "What is wrong, in one line. The full explanation is in the detail panel."),
                ("Source", "The asset and the object the finding is anchored to."),
                ("Stored target", "The RefType:RefId involved."),
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
        private Button _cancelButton;
        private ProgressBar _progressBar;
        private VisualElement _progressRow;

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
        private VisualElement _tableHeader;
        private readonly Label[] _headerCells = new Label[ColumnCount];
        private ListView _table;
        private VisualElement _detailPane;
        private VisualElement _detail;
        private Label _emptyNote;

        private IReadOnlyList<ReferenceHubRow> _rows = Array.Empty<ReferenceHubRow>();
        private ReferenceHubRow _selected;
        private bool _isNarrow;

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
        }

        private void Unsubscribe()
        {
            Session.RunStarted -= OnRunStarted;
            Session.ProgressReported -= OnProgress;
            Session.RunFinished -= OnRunFinished;
            Session.SnapshotChanged -= Refresh;
            Session.ViewStateChanged -= Refresh;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
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

            _fullAuditButton = MolcaButtons.Primary("Full audit", () => Session.Run(wholeProject: true));
            _fullAuditButton.tooltip =
                "Audit the whole configured project, opening closed scenes to read them and restoring your "
                + "scene setup afterwards. Reads only — nothing is modified.";
            actions.Add(_fullAuditButton);

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
            _metaLabel.text = $"{when} · {health.Mode} Mode · policy: {ReferenceHubPolicyStore.Describe()}";

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
            ReferenceHubViewKind.Providers => "Providers",
            ReferenceHubViewKind.Graph => "Graph",
            ReferenceHubViewKind.Runtime => "Runtime",
            _ => "Coverage",
        };

        private static string Tooltip(ReferenceHubViewKind kind) => kind switch
        {
            ReferenceHubViewKind.Issues => "Findings, most severe first.",
            ReferenceHubViewKind.References => "Every reference site and what it resolves to.",
            ReferenceHubViewKind.Providers => "Every target and how many references reach it.",
            ReferenceHubViewKind.Graph => "The neighbourhood of the selected row — not the whole project.",
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
                    ReferenceHubViewKind.Providers => $"Providers ({counts.Providers.Count})",
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
            _filterSummary.text = filter.IsDefault
                ? $"{total} row{(total == 1 ? "" : "s")}"
                : $"{_rows.Count} of {total} · {filter.Describe()}";
        }

        private static bool IsTableView(ReferenceHubViewKind kind) =>
            kind == ReferenceHubViewKind.Issues
            || kind == ReferenceHubViewKind.References
            || kind == ReferenceHubViewKind.Providers;

        #endregion

        #region Content

        private void BuildContent()
        {
            _split = new VisualElement();
            _split.AddToClassList("molca-references__split");
            Add(_split);

            _tablePane = new VisualElement();
            _tablePane.AddToClassList("molca-references__table-pane");
            _split.Add(_tablePane);

            BuildTableHeader();

            _emptyNote = new Label();
            _emptyNote.AddToClassList("molca-references__empty");
            _tablePane.Add(_emptyNote);

            // A virtualized ListView rather than a column of built rows: the plan budgets a 10,000-row
            // snapshot, and building 10,000 elements to show 30 of them is the one way to miss it outright.
            _table = new ListView
            {
                fixedItemHeight = RowHeight,
                selectionType = SelectionType.Single,
                showBorder = false,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                makeItem = MakeRow,
                bindItem = BindRow,
            };
            _table.AddToClassList("molca-references__table");
            _table.selectionChanged += OnSelectionChanged;
            _tablePane.Add(_table);

            _detailPane = new VisualElement();
            _detailPane.AddToClassList("molca-references__detail-pane");
            _split.Add(_detailPane);

            _detail = new ScrollView(ScrollViewMode.Vertical);
            _detail.AddToClassList("molca-references__detail");
            _detailPane.Add(_detail);
        }

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

        /// <summary>
        /// Builds the column-heading row above the table.
        /// </summary>
        /// <remarks>
        /// The severity dot has no heading — a colour needs no label, and the header cell would only push the
        /// text columns out of line with the rows. A spacer of the dot's exact footprint stands in its place.
        /// </remarks>
        private void BuildTableHeader()
        {
            _tableHeader = new VisualElement();
            _tableHeader.AddToClassList("molca-references__table-header");
            _tablePane.Add(_tableHeader);

            var dotSpacer = new VisualElement();
            dotSpacer.AddToClassList("molca-references__header-dot-spacer");
            _tableHeader.Add(dotSpacer);

            for (int i = 0; i < ColumnCount; i++)
            {
                var cell = Cell(ColumnClasses[i]);
                cell.AddToClassList("molca-references__header-cell");
                _headerCells[i] = cell;
                _tableHeader.Add(cell);
            }
        }

        private void RefreshTableHeader(ReferenceHubViewKind view)
        {
            var columns = Columns(view);
            for (int i = 0; i < ColumnCount; i++)
            {
                _headerCells[i].text = columns[i].Heading;
                _headerCells[i].tooltip = columns[i].Tooltip;
            }
        }

        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-references__row");

            var dot = new VisualElement();
            dot.AddToClassList("molca-status-dot");
            dot.AddToClassList("molca-references__row-dot");
            row.Add(dot);

            foreach (var columnClass in ColumnClasses)
                row.Add(Cell(columnClass));

            return row;
        }

        private static Label Cell(string className)
        {
            var label = new Label();
            label.AddToClassList("molca-references__cell");
            label.AddToClassList(className);
            return label;
        }

        private void BindRow(VisualElement element, int index)
        {
            if (index < 0 || index >= _rows.Count)
                return;

            var row = _rows[index];

            var dot = element[0];
            dot.RemoveFromClassList("molca-status-dot--ok");
            dot.RemoveFromClassList("molca-status-dot--warn");
            dot.RemoveFromClassList("molca-status-dot--error");
            dot.RemoveFromClassList("molca-status-dot--idle");
            dot.AddToClassList(SeverityDot(row));

            ((Label)element[1]).text = row.Code;
            ((Label)element[2]).text = row.Title;
            ((Label)element[3]).text = Compact(row.AssetPath, row.Owner);
            ((Label)element[4]).text = row.Kind == ReferenceHubRowKind.Provider
                ? $"{row.StoredTarget}  ({row.InboundCount} in)"
                : row.StoredTarget;
            ((Label)element[5]).text = row.ResolutionState;
            ((Label)element[6]).text = RepairLabel(row.Repair);

            element.tooltip = string.IsNullOrEmpty(row.Summary) ? row.Title : row.Summary;
        }

        private static string RepairLabel(ReferenceHubRepairAvailability availability) => availability switch
        {
            ReferenceHubRepairAvailability.Automatic => "automatic",
            ReferenceHubRepairAvailability.RequiresChoice => "needs a decision",
            _ => string.Empty,
        };

        private static string SeverityDot(ReferenceHubRow row)
        {
            if (row.Kind != ReferenceHubRowKind.Issue)
            {
                // Site and provider rows are facts, not judgements — except for the one fact that reads as a
                // problem on sight: a set reference that resolves to nothing.
                var unresolved = row.IsAssigned
                    && !row.ResolutionState.StartsWith("Resolved", StringComparison.Ordinal)
                    && row.Kind == ReferenceHubRowKind.Site;
                return unresolved ? "molca-status-dot--error" : "molca-status-dot--idle";
            }

            return row.Severity switch
            {
                ReferenceFindingSeverity.Error => "molca-status-dot--error",
                ReferenceFindingSeverity.Warning => "molca-status-dot--warn",
                _ => "molca-status-dot--idle",
            };
        }

        private static string Compact(string assetPath, string owner)
        {
            var asset = string.IsNullOrEmpty(assetPath)
                ? string.Empty
                : System.IO.Path.GetFileNameWithoutExtension(assetPath);
            if (string.IsNullOrEmpty(owner))
                return asset;
            return string.IsNullOrEmpty(asset) ? owner : $"{asset} :: {owner}";
        }

        private void OnSelectionChanged(IEnumerable<object> selection)
        {
            _selected = selection?.OfType<ReferenceHubRow>().FirstOrDefault();
            Session.SetSelectedKey(Session.View, _selected?.Key);
            RefreshDetail();
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
                _rows = Session.FilteredRows(view);
                _table.itemsSource = (System.Collections.IList)_rows;
                _table.style.display = DisplayStyle.Flex;
                _table.Rebuild();
                RestoreSelection(view);

                RefreshTableHeader(view);
                _tableHeader.style.display = DisplayStyle.Flex;

                _emptyNote.style.display = _rows.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                _emptyNote.text = EmptyMessage(view);
            }
            else
            {
                _rows = Array.Empty<ReferenceHubRow>();
                _table.itemsSource = null;
                _table.style.display = DisplayStyle.None;
                _tableHeader.style.display = DisplayStyle.None;
                _emptyNote.style.display = DisplayStyle.None;
            }

            RefreshFilters();
            RefreshDetail();
        }

        private void RestoreSelection(ReferenceHubViewKind view)
        {
            var key = Session.SelectedKey(view);
            var index = string.IsNullOrEmpty(key)
                ? -1
                : IndexOfKey(key);

            if (index >= 0)
            {
                _table.SetSelectionWithoutNotify(new[] { index });
                _selected = _rows[index];
            }
            else
            {
                _selected = null;
            }
        }

        private int IndexOfKey(string key)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (string.Equals(_rows[i].Key, key, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        /// <summary>
        /// The message shown when a table is empty, which has to distinguish "nothing to report" from
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
                _ => "No providers were found in the audited scope.",
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
                case ReferenceHubViewKind.Graph:
                    BuildGraphDetail();
                    return;
            }

            if (_selected == null)
            {
                _detail.Add(Note("Select a row to see its full locator, candidates and available repairs."));
                BuildRepairCard();
                return;
            }

            BuildRowDetail(_selected);
            BuildRepairCard();
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
                card.Body.Add(Note("This asset is read-only, so no repair can write to it."));

            if (row.IsLegacyFallback)
            {
                card.Body.Add(Note(
                    "This reference stores no Ref Type, so it depends on the ID-only compatibility fallback. "
                    + "That path refuses to resolve the moment a second object carries the same Ref Id."));
            }

            AddSeverityExplanation(card.Body, row);
            AddCandidates(card.Body, row);
            AddActions(card.Body, row);
        }

        /// <summary>
        /// States why the row has the severity it has, and whether that severity is configurable.
        /// </summary>
        /// <remarks>
        /// Required by the plan's detail panel, and it earns its space: a user who can see that REF002 is
        /// fixed at error <i>because the runtime refuses an ambiguous lookup</i> stops looking for the setting
        /// that would turn it off.
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
                  + "in Policy below; builds always use the production policy.";
            parent.Add(Note(text));
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

                var select = MolcaButtons.Mini("Select", () =>
                    ReferenceHubNavigation.SelectAndPing(provider.Locator, "candidate"));
                line.Add(select);

                // Redirecting is offered only where it is a real repair: the site is known, the snapshot is
                // current, and the asset can be written. Everything else would build a plan the executor is
                // going to refuse anyway.
                if (row.Kind == ReferenceHubRowKind.Issue && !string.IsNullOrEmpty(row.SiteKey)
                    && !row.IsReadOnly && !Session.IsStale)
                {
                    var use = MolcaButtons.Mini("Point here…", () => PreviewRedirect(row.SiteKey, key));
                    use.tooltip =
                        "Build a repair plan pointing this reference at this provider. You approve the plan "
                        + "before anything is written.";
                    line.Add(use);
                }
            }
        }

        private void AddActions(VisualElement parent, ReferenceHubRow row)
        {
            var actions = new VisualElement();
            actions.AddToClassList("molca-references__actions");
            parent.Add(actions);

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

            if (row.Kind == ReferenceHubRowKind.Issue && !string.IsNullOrEmpty(row.SiteKey)
                && row.IsAssigned && !row.IsReadOnly && !Session.IsStale)
            {
                var clear = MolcaButtons.Mini("Clear reference…", () => PreviewClear(row.SiteKey));
                clear.tooltip =
                    "Build a plan that unsets this reference. Never part of a batch: an unset reference "
                    + "passes validation without anything being fixed.";
                actions.Add(clear);
            }
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

        #region Repair

        /// <summary>
        /// The repair card: what the safe batch would do, what needs a decision, and the policy editor.
        /// </summary>
        /// <remarks>
        /// There is no button here that changes anything without first showing a plan. That is the whole
        /// contract of the repair system, and the workspace is the surface where it would be most tempting to
        /// add a convenient "fix all" — which is exactly the affordance that used to point references at the
        /// wrong objects.
        /// </remarks>
        private void BuildRepairCard()
        {
            var repair = Session.RepairIndex;
            var card = new MolcaSectionCard("Repair", subtitle: repair.Describe());
            _detail.Add(card);

            if (Session.IsStale)
            {
                card.Body.Add(Note(
                    "The snapshot is stale, so no plan can be built from it — a repair applied to unreviewed "
                    + "data is worse than no repair. Re-run the audit first."));
                return;
            }

            if (repair.AutomaticCount == 0 && repair.Choices.Count == 0)
            {
                card.Body.Add(Note("Nothing in this snapshot can be repaired automatically."));
                BuildPolicyCard();
                return;
            }

            if (repair.AutomaticCount > 0)
            {
                var preview = MolcaButtons.Primary(
                    $"Preview safe repairs ({repair.AutomaticCount})", PreviewSafeRepairs);
                preview.tooltip =
                    "Build the plan for every unambiguous repair and show it in full. Nothing is written "
                    + "until you approve it.";
                card.Body.Add(preview);
            }

            if (repair.Choices.Count > 0)
            {
                card.Body.Add(FieldLabel($"Needs a decision ({repair.Choices.Count})"));
                foreach (var choice in repair.Choices.Take(6))
                {
                    var note = Note($"{choice.Finding.CodeString} — {choice.Question}");
                    note.tooltip = choice.Finding.Summary;
                    card.Body.Add(note);
                }
                if (repair.Choices.Count > 6)
                    card.Body.Add(Note($"… +{repair.Choices.Count - 6} more in the Issues table."));
            }

            BuildPolicyCard();
        }

        /// <summary>
        /// The severity-policy editor, moved here from the settings Inspector.
        /// </summary>
        /// <remarks>
        /// It lives next to the findings it governs rather than in an Inspector two navigations away, and it
        /// says plainly which severities it can and cannot change: the codes that describe runtime failures
        /// are not configurable, and nothing authored here reaches a build (see
        /// <see cref="ReferenceHubPolicyStore"/>).
        /// </remarks>
        private void BuildPolicyCard()
        {
            var card = new MolcaSectionCard(
                "Policy", subtitle: "Editor severities for this project · builds always use the production policy");
            _detail.Add(card);

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
                var reset = MolcaButtons.Mini("Reset to production severities", () =>
                {
                    ReferenceHubPolicyStore.ClearOverrides();
                    Refresh();
                });
                card.Body.Add(reset);
                card.Body.Add(Note(
                    "Overridden severities apply to editor audits only. The build gate always uses the "
                    + "production policy, so a machine-local override cannot make a broken project build."));
            }
        }

        private void PreviewSafeRepairs() =>
            PreviewAndApply(() => ReferenceRepairPlanner.PlanSafeRepairs(Session.Snapshot));

        private void PreviewRedirect(string siteKey, string providerKey) =>
            PreviewAndApply(() => ReferenceRepairPlanner.PlanRedirect(Session.Snapshot, siteKey, providerKey));

        private void PreviewClear(string siteKey) =>
            PreviewAndApply(() => ReferenceRepairPlanner.PlanClear(Session.Snapshot, siteKey));

        /// <summary>
        /// Builds a plan, shows it in full, and applies it only on explicit approval.
        /// </summary>
        /// <remarks>
        /// <c>async void</c> because this is a UI command entry point; the body is a try/catch shim per the
        /// async contract.
        /// </remarks>
        private static async void PreviewAndApply(Func<ReferenceRepairPlan> build) // doctor:ignore async-void is intentional: UI command entry point wrapped in try/catch
        {
            try
            {
                var plan = build();

                if (plan.IsEmpty)
                {
                    EditorUtility.DisplayDialog(
                        "No Repair Available",
                        "Nothing can be changed without guessing what was intended.\n\n" + plan.Preview(),
                        "OK");
                    return;
                }

                // The preview is the approval, and logging it means the exact approved change survives the
                // dialog being dismissed.
                Debug.Log(plan.Preview());

                if (!EditorUtility.DisplayDialog(
                        "Apply Reference Repairs?",
                        $"{plan.DescribeSummary()}.\n\nThe full plan is in the Console. Reversibility: "
                        + $"{plan.Reversibility}.\n\n"
                        + (plan.Warnings.Count > 0 ? "! " + string.Join("\n! ", plan.Warnings) + "\n\n" : string.Empty)
                        + "Apply it?",
                        "Apply", "Cancel"))
                    return;

                var result = await ReferenceRepairExecutor.ApplyAsync(plan);

                if (result.Introduced.Count > 0)
                    Debug.LogError(result.Describe());
                else if (result.WasRejected || result.Skipped.Count > 0)
                    Debug.LogWarning(result.Describe());
                else
                    Debug.Log(result.Describe());
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReferenceSystem] Repair failed: {e}");
            }
        }

        #endregion

        #region Coverage / Runtime / Graph views

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
        /// Reports the declared scene load sets, which are what cross-scene availability is judged against.
        /// </summary>
        /// <remarks>
        /// Under Coverage because an inferred load set is a coverage limitation, not a setting: it means
        /// nobody has told the tooling which scenes load together, so any cross-scene conclusion it
        /// draws rests on a guess. Saying so here is the difference between a limitation and a lie.
        /// </remarks>
        private void BuildLoadSetCard()
        {
            var sets = ReferenceLoadSetStore.Sets;
            var card = new MolcaSectionCard("Scene load sets", subtitle: ReferenceLoadSetStore.Describe());
            _detail.Add(card);

            foreach (var set in sets)
                card.Body.Add(Note($"• {set.Describe()}"));

            if (ReferenceLoadSetStore.IsInferred)
            {
                card.Body.Add(Note(
                    "The inferred set treats every enabled scene after the first as deferred, which is the "
                    + "honest reading of unknown load order — it never claims two scenes are loaded "
                    + $"together. Author {ReferenceLoadSetStore.FilePath} to validate additive loading "
                    + "properly."));
            }
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

        private void BuildGraphDetail()
        {
            var selected = SelectedRowAcrossViews();
            var card = new MolcaSectionCard(
                "Neighbourhood",
                subtitle: selected == null
                    ? "select a row in Issues, References or Providers"
                    : selected.Title);
            _detail.Add(card);

            var source = ReferenceHubGraph.BuildMermaid(Session.Snapshot, selected);
            try
            {
                card.Body.Add(new MolcaMermaidView(MolcaMermaid.ParseFlowchart(source)));
            }
            catch (Exception e)
            {
                // A graph that will not lay out must not cost the user the workspace; the source is still
                // useful, so it is offered as text.
                Debug.LogWarning($"[ReferenceSystem] Neighbourhood graph could not be drawn: {e.Message}");
                card.Body.Add(Note(source));
            }

            card.Body.Add(Note(
                $"One hop from the selection, at most {ReferenceHubGraph.MaxNeighbours} neighbours per side. "
                + "A solid arrow is what the runtime resolves; a dashed one is a match that does not win."));
        }

        /// <summary>
        /// The row the Graph view should focus: the selection of whichever table the user last used.
        /// </summary>
        private ReferenceHubRow SelectedRowAcrossViews()
        {
            foreach (var view in new[]
                     {
                         ReferenceHubViewKind.Issues, ReferenceHubViewKind.References, ReferenceHubViewKind.Providers,
                     })
            {
                var key = Session.SelectedKey(view);
                if (string.IsNullOrEmpty(key))
                    continue;

                var row = Session.AllRows(view).FirstOrDefault(r => r.Key == key);
                if (row != null)
                    return row;
            }

            return null;
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
