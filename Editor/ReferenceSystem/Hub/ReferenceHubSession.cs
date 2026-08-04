using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>Which of the References workspace's primary views is showing.</summary>
    /// <remarks>
    /// There is no Graph member any more. The neighbourhood used to be a mode you left the table to enter,
    /// which meant it had to hunt for a selection made in some other view; it is now a section of the detail
    /// panel beside the row it describes. An older persisted preference naming it simply fails to parse and
    /// falls back to <see cref="Issues"/>.
    /// </remarks>
    public enum ReferenceHubViewKind
    {
        /// <summary>Findings. The default.</summary>
        Issues = 0,

        /// <summary>Every reference site and its resolution state.</summary>
        References = 1,

        /// <summary>
        /// Every target, grouped by Ref Type, with the references that name it nested underneath. The
        /// authoring view: the wiring, rather than the findings about it.
        /// </summary>
        Targets = 2,

        /// <summary>Live runtime registrations, in Play Mode.</summary>
        Runtime = 4,

        /// <summary>What was scanned, skipped, and failed.</summary>
        Coverage = 5,
    }

    /// <summary>
    /// The view-independent owner of the References workspace's state: the in-flight audit, the derived
    /// tables, the filter, and the selection.
    /// </summary>
    /// <remarks>
    /// <para>The session is static and the view is disposable, which is what makes a long scan survive a tab
    /// switch: the Hub clears its workspace host on every switch (or hides a cached view), so anything owned
    /// by the view would be cancelled or rebuilt. The view subscribes on attach, unsubscribes on detach, and
    /// never cancels — cancellation is only ever the Cancel button.</para>
    ///
    /// <para>The session <b>never starts an audit on its own</b>. A scan can open scenes and costs real time,
    /// so it happens when the user asks, or when another Molca surface (Doctor, a build, an MCP call) produces
    /// a snapshot that this session then reflects. Opening a tab is not a request to scan the project.</para>
    /// </remarks>
    internal sealed class ReferenceHubSession
    {
        private const string ViewKey = "Molca.References.View";
        private const string SelectionKeyPrefix = "Molca.References.Selection.";

        private static ReferenceHubSession _instance;

        /// <summary>The single session instance.</summary>
        internal static ReferenceHubSession Instance => _instance ??= new ReferenceHubSession();

        private CancellationTokenSource _cancellation;
        private ReferenceHubTables _tables = ReferenceHubTables.Empty;
        private ReferenceHubRepairIndex _repair = ReferenceHubRepairIndex.Empty;
        private long _projectedRevision = -1;

        /// <summary>Raised when an audit starts.</summary>
        internal event Action RunStarted;

        /// <summary>Raised as an audit progresses: phase caption and fraction in <c>[0,1]</c>.</summary>
        internal event Action<string, float> ProgressReported;

        /// <summary>Raised when an audit ends; the argument is true when it was cancelled.</summary>
        internal event Action<bool> RunFinished;

        /// <summary>
        /// Raised when the reported snapshot changes — including when a surface other than this workspace
        /// produced it.
        /// </summary>
        internal event Action SnapshotChanged;

        /// <summary>Raised when the filter or the selection changes.</summary>
        internal event Action ViewStateChanged;

        /// <summary>True while an audit this session started is running.</summary>
        internal bool IsRunning { get; private set; }

        /// <summary>True once any audit has completed in this editor session.</summary>
        internal bool HasRun { get; private set; }

        /// <summary>True when the most recent run was cancelled.</summary>
        internal bool WasCancelled { get; private set; }

        /// <summary>Phase caption of the in-flight scan, or empty.</summary>
        internal string CurrentPhase { get; private set; } = string.Empty;

        /// <summary>Progress of the in-flight scan, or null.</summary>
        internal float? CurrentProgress { get; private set; }

        /// <summary>The filter shared by the row tables. Survives view rebuilds.</summary>
        internal ReferenceHubFilter Filter { get; } = new ReferenceHubFilter();

        /// <summary>The active primary view, persisted per project.</summary>
        internal ReferenceHubViewKind View { get; private set; }

        /// <summary>The snapshot being reported. Never null.</summary>
        internal ReferenceAuditSnapshot Snapshot => ReferenceAuditService.Current;

        /// <summary>Whether the reported snapshot is out of date.</summary>
        internal bool IsStale => ReferenceAuditService.IsStale;

        private ReferenceHubSession()
        {
            View = ReadView();

            // Another surface producing a snapshot is exactly as interesting as this one doing it: a Doctor
            // run or an MCP audit must update the header rather than leave it describing an older result.
            ReferenceAuditService.SnapshotChanged += OnServiceSnapshot;
            ReferenceHubPolicyStore.Changed += () => SnapshotChanged?.Invoke();
        }

        /// <summary>The current derived tables, re-projected only when the snapshot revision changes.</summary>
        internal ReferenceHubTables Tables
        {
            get
            {
                EnsureProjected();
                return _tables;
            }
        }

        /// <summary>Repair availability for the current snapshot.</summary>
        internal ReferenceHubRepairIndex RepairIndex
        {
            get
            {
                EnsureProjected();
                return _repair;
            }
        }

        /// <summary>The current header view-model.</summary>
        internal ReferenceHubHealth Health => ReferenceHubHealth.Describe(
            HasRun ? Snapshot : null,
            IsStale,
            ReferenceAuditService.StaleReason,
            HasRun,
            EditorApplication.isPlayingOrWillChangePlaymode,
            CurrentPhase,
            CurrentProgress,
            IsRunning);

        /// <summary>The rows for the active table view, filtered.</summary>
        /// <param name="view">Which table to read.</param>
        internal System.Collections.Generic.IReadOnlyList<ReferenceHubRow> FilteredRows(ReferenceHubViewKind view)
        {
            var tables = Tables;
            var rows = view switch
            {
                ReferenceHubViewKind.References => tables.Sites,
                ReferenceHubViewKind.Targets => tables.Providers,
                _ => tables.Issues,
            };
            return Filter.Apply(rows);
        }

        /// <summary>The unfiltered rows for the active table view, for populating filter dropdowns.</summary>
        /// <param name="view">Which table to read.</param>
        internal System.Collections.Generic.IReadOnlyList<ReferenceHubRow> AllRows(ReferenceHubViewKind view)
        {
            var tables = Tables;
            return view switch
            {
                ReferenceHubViewKind.References => tables.Sites,
                ReferenceHubViewKind.Targets => tables.Providers,
                _ => tables.Issues,
            };
        }

        /// <summary>Switches the active view and persists the choice.</summary>
        /// <param name="view">The view to activate.</param>
        internal void SetView(ReferenceHubViewKind view)
        {
            if (View == view)
                return;
            View = view;
            MolcaEditorPrefs.SetString(ViewKey, view.ToString());
            ViewStateChanged?.Invoke();
        }

        /// <summary>The primary selected row key for a view, or empty.</summary>
        /// <param name="view">The view whose selection to read.</param>
        /// <remarks>
        /// The first of <see cref="SelectedKeys"/>. The detail panel describes one row even when several are
        /// selected for a batch, because a panel that tried to describe forty rows would describe none.
        /// </remarks>
        internal string SelectedKey(ReferenceHubViewKind view) => SelectedKeys(view).FirstOrDefault() ?? string.Empty;

        /// <summary>Every selected row key for a view, in selection order.</summary>
        /// <param name="view">The view whose selection to read.</param>
        internal IReadOnlyList<string> SelectedKeys(ReferenceHubViewKind view)
        {
            var stored = MolcaEditorPrefs.GetString(SelectionKeyPrefix + view, string.Empty);
            return string.IsNullOrEmpty(stored)
                ? Array.Empty<string>()
                : stored.Split(SelectionSeparator).Where(k => k.Length > 0).ToList();
        }

        /// <summary>
        /// Records the selected rows for a view. Persisted per view so moving between Issues and Targets and
        /// back does not lose what the user was reading — or the batch they were assembling.
        /// </summary>
        /// <param name="view">The view whose selection changed.</param>
        /// <param name="keys">The selected <see cref="ReferenceHubRow.Key"/> values, or null to clear.</param>
        internal void SetSelectedKeys(ReferenceHubViewKind view, IEnumerable<string> keys)
        {
            // A row key can hold anything an author typed into an object name, so the separator is a
            // character no serialized path or display name can contain.
            var joined = keys == null
                ? string.Empty
                : string.Join(SelectionSeparator.ToString(), keys.Where(k => !string.IsNullOrEmpty(k)));

            MolcaEditorPrefs.SetString(SelectionKeyPrefix + view, joined);
            ViewStateChanged?.Invoke();
        }

        /// <summary>Records a single selected row.</summary>
        /// <param name="view">The view whose selection changed.</param>
        /// <param name="key">The selected key, or null to clear.</param>
        internal void SetSelectedKey(ReferenceHubViewKind view, string key) =>
            SetSelectedKeys(view, key == null ? null : new[] { key });

        /// <summary>
        /// Tree node keys the Targets view has expanded.
        /// </summary>
        /// <remarks>
        /// Lives on the session rather than on the tree control so an audit — which rebuilds every node —
        /// does not collapse the group the author was working inside.
        /// </remarks>
        internal HashSet<string> ExpandedTreeKeys { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Stable integer ids for tree nodes, keyed by node key.
        /// </summary>
        /// <remarks>
        /// <c>TreeViewItemData</c> is identified by an int, and the control tracks expansion by that id. Ids
        /// are therefore handed out once per key and reused for the life of the session: deriving them from a
        /// row's position would move every expansion one row down the first time a finding disappeared.
        /// </remarks>
        internal int TreeIdFor(string nodeKey)
        {
            if (string.IsNullOrEmpty(nodeKey))
                return -1;

            if (_treeIds.TryGetValue(nodeKey, out var id))
                return id;

            id = _treeIds.Count + 1;
            _treeIds[nodeKey] = id;
            return id;
        }

        /// <summary>ASCII unit separator: no serialized path, id or display name holds it.</summary>
        private const char SelectionSeparator = '\u001F';

        private readonly Dictionary<string, int> _treeIds = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Notifies subscribers that the filter changed.</summary>
        internal void NotifyFilterChanged() => ViewStateChanged?.Invoke();

        /// <summary>
        /// Runs an audit.
        /// </summary>
        /// <param name="wholeProject">
        /// When true, the audit may open closed scenes to cover the whole project (the header's <b>Full
        /// audit</b>); when false it covers what is already loaded (<b>Refresh affected</b>).
        /// </param>
        /// <remarks>
        /// <c>async void</c> because this is a UI command entry point; the body is a try/catch shim per the
        /// async contract, and the awaited engine yields so the editor stays responsive and the Cancel button
        /// stays live.
        /// </remarks>
        internal async void Run(bool wholeProject) // doctor:ignore async-void is intentional: UI command entry point wrapped in try/catch
        {
            if (IsRunning)
                return;

            var scope = ReferenceAuditScope
                .FromSettings(ReferenceAuditService.FindSettings(), mayOpenScenes: wholeProject)
                .WithPolicy(ReferenceHubPolicyStore.Policy);

            _cancellation = new CancellationTokenSource();
            IsRunning = true;
            WasCancelled = false;
            CurrentPhase = "starting";
            CurrentProgress = 0f;
            RunStarted?.Invoke();

            try
            {
                await ReferenceAuditService.RefreshAsync(scope, ReportProgress, _cancellation.Token);
                HasRun = true;
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
            }
            catch (Exception e)
            {
                // A scan defect must leave the workspace usable and say what happened, not take the Hub down.
                Debug.LogError($"[ReferenceSystem] Reference audit failed: {e}");
            }
            finally
            {
                IsRunning = false;
                CurrentPhase = string.Empty;
                CurrentProgress = null;
                _cancellation?.Dispose();
                _cancellation = null;
                RunFinished?.Invoke(WasCancelled);
            }
        }

        /// <summary>Requests cancellation of the in-flight audit. Idempotent.</summary>
        internal void Cancel()
        {
            if (!IsRunning)
                return;

            try
            {
                _cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run completed between the click and here; nothing to cancel.
            }
        }

        private void ReportProgress(string phase, float fraction)
        {
            CurrentPhase = phase ?? string.Empty;
            CurrentProgress = Mathf.Clamp01(fraction);
            ProgressReported?.Invoke(CurrentPhase, CurrentProgress.Value);
        }

        private void OnServiceSnapshot(ReferenceAuditSnapshot snapshot)
        {
            HasRun = true;
            _projectedRevision = -1; // force a re-projection against the new snapshot
            SnapshotChanged?.Invoke();
        }

        /// <summary>
        /// Rebuilds the derived tables and the repair index when the snapshot revision has moved on.
        /// </summary>
        /// <remarks>
        /// Projection is not free — the repair index plans every safe repair — so it happens once per
        /// snapshot rather than once per repaint. Revision is the right trigger: it is the same value the
        /// repair executor uses as its precondition, so a table and a plan built from the same revision
        /// describe the same project state.
        /// </remarks>
        private void EnsureProjected()
        {
            var snapshot = Snapshot;
            if (_projectedRevision == snapshot.Revision)
                return;

            try
            {
                _repair = ReferenceHubRepairIndex.Build(snapshot);
            }
            catch (Exception e)
            {
                // Repair planning is advisory here. Losing it must not cost the user the table.
                Debug.LogWarning($"[ReferenceSystem] Repair availability could not be determined: {e.Message}");
                _repair = ReferenceHubRepairIndex.Empty;
            }

            _tables = ReferenceHubRow.Project(snapshot, _repair);
            _projectedRevision = snapshot.Revision;
        }

        private static ReferenceHubViewKind ReadView()
        {
            var stored = MolcaEditorPrefs.GetString(ViewKey, ReferenceHubViewKind.Issues.ToString());
            return Enum.TryParse<ReferenceHubViewKind>(stored, out var view) ? view : ReferenceHubViewKind.Issues;
        }
    }
}
