using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Reusable Molca Doctor UI as a <see cref="VisualElement"/>: runs the convention checks and lists
    /// findings with severity filtering and click-to-ping. Hosted by both the standalone
    /// <see cref="MolcaDoctorWindow"/> and the Molca Hub Doctor workspace (Sprint 26.10).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Doctor/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// The surface is pure UI Toolkit (Sprint 44 rewrite), styled by the shared design tokens
    /// (<see cref="MolcaEditorUi.Apply"/>) plus <c>MolcaDoctorView.uss</c>, so it renders uniformly with
    /// the Hub. The run itself lives in <see cref="DoctorRunSession"/>, not in this view: the view
    /// <b>subscribes</b> on <see cref="AttachToPanelEvent"/> (rebuilding its display from the session's
    /// current state) and <b>unsubscribes</b> on <see cref="DetachFromPanelEvent"/> without cancelling.
    /// A run therefore survives the view being detached and rebuilt — e.g. switching Hub tabs away and
    /// back, which clears the hosted view — and a run started in one host (Hub tab, standalone window) is
    /// mirrored live in the other. Cancellation is explicit, via the Cancel button.
    /// </remarks>
    public sealed class MolcaDoctorView : VisualElement
    {
        private const string UssPath = "Packages/com.molca.core/Editor/Doctor/MolcaDoctorView.uss";
        private const string ChecksCollapsedKey = "Doctor.ChecksCollapsed";
        private const string GroupExpandedKeyPrefix = "Doctor.Group.Expanded.";
        private const string LogCollapsedKey = "Doctor.LogCollapsed";

        /// <summary>The shared, view-independent run owner this view renders and drives.</summary>
        private static DoctorRunSession Session => DoctorRunSession.Instance;

        /// <summary>Checks the user has turned off for the next run (lives in the session so it survives view rebuilds).</summary>
        private static ISet<string> Disabled => Session.DisabledChecks;

        private bool _showErrors = true;
        private bool _showWarnings = true;
        private bool _showInfos = true;

        private Button _runButton;
        private Button _copyButton;
        private Button _exportButton;
        private Button _errorChip;
        private Button _warnChip;
        private Button _infoChip;

        private VisualElement _progressRow;
        private ProgressBar _progressBar;
        private Label _progressLabel;
        private Button _cancelButton;

        private ScrollView _scroll;
        private MolcaSectionCard _checkCard;
        private VisualElement _checkGroups;
        private VisualElement _results;

        // Run Log (live per-check trace). Checks run strictly sequentially, so a single set of
        // "current row" references is enough to finalize the row when its check completes.
        private MolcaSectionCard _logCard;
        private ScrollView _logScroll;
        private Button _copyLogButton;
        private VisualElement _currentLogDot;
        private Label _currentLogHead;
        private Label _currentLogDetail;
        private string _currentHeadPrefix;
        private IVisualElementScheduledItem _tick;

        public MolcaDoctorView()
        {
            AddToClassList("molca-doctor");
            style.flexGrow = 1;

            MolcaEditorUi.Apply(this);
            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null && !styleSheets.Contains(uss))
                styleSheets.Add(uss);

            BuildToolbar();
            BuildProgress();

            // The toolbar and progress bar stay pinned; the checks panel and the findings list share one
            // scroll below them, so a long grouped check list never pushes the results out of reach.
            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.AddToClassList("molca-doctor__scroll");
            Add(_scroll);

            BuildCheckCard();
            BuildLogCard();
            BuildResults();

            // Subscribe to the session while attached and sync the display from its current state; detach
            // only unsubscribes (and stops the local ticker) — it must NOT cancel the run, so switching Hub
            // tabs leaves a run executing in the background.
            RegisterCallback<AttachToPanelEvent>(_ => OnAttach());
            RegisterCallback<DetachFromPanelEvent>(_ => OnDetach());
        }

        private void OnAttach()
        {
            // Unsubscribe first so a re-attach (element moved between panels) never double-subscribes.
            Unsubscribe();
            Subscribe();
            SyncFromSession();
        }

        private void OnDetach()
        {
            Unsubscribe();
            StopTick();
        }

        private void Subscribe()
        {
            Session.RunStarted += HandleRunStarted;
            Session.ProgressReported += HandleProgress;
            Session.StatusReported += HandleStatus;
            Session.CheckCompleted += HandleCheckCompleted;
            Session.RunFinished += HandleRunFinished;
        }

        private void Unsubscribe()
        {
            Session.RunStarted -= HandleRunStarted;
            Session.ProgressReported -= HandleProgress;
            Session.StatusReported -= HandleStatus;
            Session.CheckCompleted -= HandleCheckCompleted;
            Session.RunFinished -= HandleRunFinished;
        }

        /// <summary>Rebuilds the entire display to match the session's current state (called on attach).</summary>
        private void SyncFromSession()
        {
            RebuildCheckChips();
            UpdateChecksCount();

            SetRunningUi(Session.IsRunning);
            if (Session.IsRunning && Session.CurrentProgress.HasValue)
                ApplyProgressToBar(Session.CurrentProgress.Value, Session.CurrentStatus);

            SyncLogFromSession();
            RefreshResults();
        }

        private void BuildToolbar()
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("molca-doctor__toolbar");
            Add(toolbar);

            _runButton = MolcaButtons.Primary("Run Checks", () => Session.Run());
            _runButton.AddToClassList("molca-doctor__run");
            toolbar.Add(_runButton);

            _copyButton = MolcaButtons.Toolbar("Copy", () => EditorGUIUtility.systemCopyBuffer = BuildReport());
            toolbar.Add(_copyButton);

            _exportButton = MolcaButtons.Toolbar("Export…", ExportReport);
            _exportButton.style.marginLeft = 4;
            toolbar.Add(_exportButton);

            // Doctor reports; it never repairs (IDoctorCheck is side-effect free by contract). This is the
            // hand-off to the surface that does, so "what is wrong" and "fix it" are one click apart.
            var remediate = MolcaButtons.Toolbar(
                "Remediation…",
                () => Hub.MolcaHubWindow.OpenWorkspace(
                    Remediation.Hub.RemediationWorkspaceProvider.WorkspaceId));
            remediate.style.marginLeft = 4;
            remediate.tooltip = "Open the Remediation workspace, which repairs the findings that have a "
                                + "single correct answer and explains the rest.";
            toolbar.Add(remediate);

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-doctor__toolbar-spacer");
            toolbar.Add(spacer);

            _errorChip = MakeFilterChip(DoctorSeverity.Error, v => _showErrors = v);
            _warnChip = MakeFilterChip(DoctorSeverity.Warning, v => _showWarnings = v);
            _infoChip = MakeFilterChip(DoctorSeverity.Info, v => _showInfos = v);
            toolbar.Add(_errorChip);
            toolbar.Add(_warnChip);
            toolbar.Add(_infoChip);
        }

        private Button MakeFilterChip(DoctorSeverity severity, Action<bool> setVisible)
        {
            var chip = new Button();
            chip.AddToClassList("molca-doctor__chip");
            chip.AddToClassList("molca-doctor__chip--active");
            chip.clicked += () =>
            {
                bool active = !chip.ClassListContains("molca-doctor__chip--active");
                chip.EnableInClassList("molca-doctor__chip--active", active);
                setVisible(active);
                RefreshResults();
            };
            return chip;
        }

        private void BuildProgress()
        {
            _progressRow = new VisualElement();
            _progressRow.AddToClassList("molca-doctor__progress");
            _progressRow.style.display = DisplayStyle.None;
            Add(_progressRow);

            _progressBar = new ProgressBar();
            _progressBar.AddToClassList("molca-doctor__progress-bar");
            _progressRow.Add(_progressBar);

            _progressLabel = new Label();
            _progressLabel.AddToClassList("molca-doctor__progress-label");
            _progressRow.Add(_progressLabel);

            _cancelButton = MolcaButtons.Mini("Cancel", () => Session.Cancel());
            _cancelButton.style.marginLeft = 8;
            _progressRow.Add(_cancelButton);
        }

        private void BuildCheckCard()
        {
            _checkCard = new MolcaSectionCard("Checks", subtitle: "Convention validations to run against the project");
            var card = _checkCard;
            _scroll.Add(card);

            // Collapsible body: a chevron header action toggles Body visibility, persisted across
            // domain reloads via MolcaEditorPrefs so the panel reopens in the user's last state.
            bool collapsed = MolcaEditorPrefs.GetBool(ChecksCollapsedKey, false);
            var chevron = MolcaButtons.Mini(collapsed ? "▸" : "▾", null);
            chevron.tooltip = "Show/hide checks";
            chevron.clicked += () =>
            {
                collapsed = !collapsed;
                card.Body.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;
                chevron.text = collapsed ? "▸" : "▾";
                MolcaEditorPrefs.SetBool(ChecksCollapsedKey, collapsed);
            };
            card.AddHeaderAction(chevron);
            card.Body.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;

            var head = new VisualElement();
            head.AddToClassList("molca-doctor__chip-head");
            card.Body.Add(head);

            head.Add(MolcaButtons.Mini("All", () => SetAllChecks(enabled: true)));
            var none = MolcaButtons.Mini("None", () => SetAllChecks(enabled: false));
            none.style.marginLeft = 4;
            head.Add(none);

            // One flat wrapping flow, not a wrap-container per group: a wrapping row nested inside a
            // column hits Unity's two-pass flex-wrap measurement bug (measured at unconstrained width →
            // one line → too-short reserved height → wrapped chips overlap the next element). A single
            // top-level wrapping flow measures correctly; each group header claims its own line via a
            // width:100% break element instead.
            _checkGroups = new VisualElement();
            _checkGroups.AddToClassList("molca-doctor__chip-flow");
            card.Body.Add(_checkGroups);

            RebuildCheckChips();
            UpdateChecksCount();
        }

        /// <summary>
        /// (Re)builds the check chips grouped by <see cref="IDoctorCheck.Category"/>. Categories appear
        /// in the order their first check does — i.e. the curated built-in order (see
        /// <see cref="DoctorCheckRegistry.BuiltInOrder"/>) — and each group carries its own All/None
        /// toggle. Called on every selection change so chip active-state mirrors <see cref="Disabled"/>.
        /// </summary>
        private void RebuildCheckChips()
        {
            _checkGroups.Clear();

            // Preserve the curated check order; a category is created the first time a check of that
            // category is seen, so groups land in the same order the checks run in.
            var groups = new List<(string Category, List<IDoctorCheck> Checks)>();
            var indexByCategory = new Dictionary<string, int>();
            foreach (var check in MolcaDoctor.Checks)
            {
                var category = check.Category;
                if (!indexByCategory.TryGetValue(category, out var gi))
                {
                    gi = groups.Count;
                    indexByCategory[category] = gi;
                    groups.Add((category, new List<IDoctorCheck>()));
                }
                groups[gi].Checks.Add(check);
            }

            foreach (var group in groups)
            {
                bool expanded = IsCategoryExpanded(group.Category);
                _checkGroups.Add(BuildGroupHeader(group.Category, group.Checks, expanded));

                // Collapsed by default: a category contributes only its header line until expanded, so
                // the panel stays compact. Chips live in the same flat flow (not a nested wrap container)
                // to avoid Unity's flex-wrap height bug; the width:100% header forces the line breaks.
                if (expanded)
                    foreach (var check in group.Checks)
                        _checkGroups.Add(MakeCheckChip(check));
            }
        }

        /// <summary>
        /// Builds a full-width category header — an expand/collapse trigger (chevron + name), the
        /// enabled/total count, and a single whole-group toggle. Forces its own line in the flat chip
        /// flow so the chips that follow read as a distinct section.
        /// </summary>
        private VisualElement BuildGroupHeader(string category, List<IDoctorCheck> checks, bool expanded)
        {
            var header = new VisualElement();
            header.AddToClassList("molca-doctor__group-head");

            // Chevron + name is the expand/collapse target (a Label with a click manipulator, not a
            // button, so it reads as a section title rather than a control).
            var trigger = new Label($"{(expanded ? "▾" : "▸")}  {category}");
            trigger.AddToClassList("molca-doctor__group-title");
            trigger.tooltip = expanded ? "Collapse category" : "Expand category";
            trigger.AddManipulator(new Clickable(() => ToggleCategory(category)));
            header.Add(trigger);

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-doctor__group-spacer");
            header.Add(spacer);

            int enabled = checks.Count(c => !Disabled.Contains(c.Id));
            var count = new Label($"{enabled}/{checks.Count}");
            count.AddToClassList("molca-doctor__group-count");
            header.Add(count);

            // One toggle flips the whole group: enable all unless already all-on, in which case disable
            // all. The label reflects current state (all / mixed / none).
            bool allOn = enabled == checks.Count;
            var state = allOn ? "on" : enabled == 0 ? "off" : "mixed";
            var ids = checks.Select(c => c.Id).ToList();
            var toggle = MolcaButtons.Mini(state, () => SetGroupChecks(ids, enabled: !allOn));
            toggle.AddToClassList("molca-doctor__group-toggle");
            toggle.EnableInClassList("molca-doctor__group-toggle--on", allOn);
            toggle.tooltip = allOn ? "Disable all in this category" : "Enable all in this category";
            header.Add(toggle);

            return header;
        }

        /// <summary>Whether a category's checks are currently expanded (persisted across domain reloads).</summary>
        private static bool IsCategoryExpanded(string category) =>
            MolcaEditorPrefs.GetBool(GroupExpandedKeyPrefix + category, false);

        /// <summary>Flips a category's expanded state, persists it, and repaints the flow.</summary>
        private void ToggleCategory(string category)
        {
            MolcaEditorPrefs.SetBool(GroupExpandedKeyPrefix + category, !IsCategoryExpanded(category));
            RebuildCheckChips();
        }

        /// <summary>Enables or disables every check in one category, then repaints the chips.</summary>
        private void SetGroupChecks(IEnumerable<string> ids, bool enabled)
        {
            foreach (var id in ids)
            {
                if (enabled)
                    Disabled.Remove(id);
                else
                    Disabled.Add(id);
            }

            RebuildCheckChips();
            UpdateChecksCount();
        }

        /// <summary>Shows the enabled/total check count on the card header (e.g. "16/18").</summary>
        private void UpdateChecksCount()
        {
            int total = MolcaDoctor.Checks.Count;
            int enabled = total - Disabled.Count;
            _checkCard.SetStatus(MolcaStatusKind.Idle, $"{enabled}/{total}");
        }

        private Button MakeCheckChip(IDoctorCheck check)
        {
            var chip = new Button { text = check.Id, tooltip = check.Description };
            chip.AddToClassList("molca-doctor__chip");
            chip.EnableInClassList("molca-doctor__chip--active", !Disabled.Contains(check.Id));
            chip.clicked += () =>
            {
                if (Disabled.Contains(check.Id))
                    Disabled.Remove(check.Id);
                else
                    Disabled.Add(check.Id);
                chip.EnableInClassList("molca-doctor__chip--active", !Disabled.Contains(check.Id));
                UpdateChecksCount();
            };
            return chip;
        }

        private void SetAllChecks(bool enabled)
        {
            Disabled.Clear();
            if (!enabled)
                foreach (var c in MolcaDoctor.Checks)
                    Disabled.Add(c.Id);

            // Rebuild the chips so their active state mirrors the new selection.
            RebuildCheckChips();
            UpdateChecksCount();
        }

        /// <summary>
        /// Builds the collapsible "Run Log" card: a bounded, auto-scrolling trace that fills live as the
        /// run proceeds — one row per check with a status dot, the check id, its elapsed time, and the
        /// findings it produced — so the user can watch progress in detail rather than a single bar.
        /// </summary>
        private void BuildLogCard()
        {
            _logCard = new MolcaSectionCard("Run Log", subtitle: "Per-check trace of the most recent run");
            _scroll.Add(_logCard);

            bool collapsed = MolcaEditorPrefs.GetBool(LogCollapsedKey, false);
            var chevron = MolcaButtons.Mini(collapsed ? "▸" : "▾", null);
            chevron.tooltip = "Show/hide run log";
            chevron.clicked += () =>
            {
                collapsed = !collapsed;
                _logCard.Body.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;
                chevron.text = collapsed ? "▸" : "▾";
                MolcaEditorPrefs.SetBool(LogCollapsedKey, collapsed);
            };
            _logCard.AddHeaderAction(chevron);

            _copyLogButton = MolcaButtons.Mini("Copy", () => EditorGUIUtility.systemCopyBuffer = BuildTraceReport());
            _copyLogButton.tooltip = "Copy the run log to the clipboard";
            _copyLogButton.SetEnabled(false);
            _logCard.AddHeaderAction(_copyLogButton);

            _logCard.Body.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;

            // Own bounded scroll (max-height in USS) so a long run scrolls internally and never pushes the
            // findings list out of reach; it auto-scrolls to the newest entry as checks complete.
            _logScroll = new ScrollView(ScrollViewMode.Vertical);
            _logScroll.AddToClassList("molca-doctor__log");
            _logCard.Body.Add(_logScroll);

            ShowLogPlaceholder();
        }

        /// <summary>Resets the log to its idle placeholder (no run yet).</summary>
        private void ShowLogPlaceholder()
        {
            _logScroll.Clear();
            var note = new Label("No run yet — click Run Checks to see a live trace.");
            note.AddToClassList("molca-doctor__log-note");
            _logScroll.Add(note);
            _logCard.SetStatus(MolcaStatusKind.None);
        }

        /// <summary>Clears the log and starts a fresh trace for a run of <paramref name="total"/> checks.</summary>
        private void ResetLog(int total)
        {
            StopTick();
            _logScroll.Clear();
            _currentLogDot = null;
            _currentLogHead = null;
            _currentLogDetail = null;
            _copyLogButton.SetEnabled(false);
            _logCard.SetStatus(MolcaStatusKind.Idle, "running");
            AddLogNote($"Running {total} check{(total == 1 ? "" : "s")}…");
        }

        /// <summary>Rebuilds the whole log from the session's trace: completed rows, the in-flight row, or the summary.</summary>
        private void SyncLogFromSession()
        {
            StopTick();
            _currentLogDot = null;
            _currentLogHead = null;
            _currentLogDetail = null;
            _logScroll.Clear();

            if (!Session.HasRun && !Session.IsRunning && Session.Reports.Count == 0)
            {
                ShowLogPlaceholder();
                return;
            }

            AddLogNote($"Running {Session.TotalCount} check{(Session.TotalCount == 1 ? "" : "s")}…");
            foreach (var report in Session.Reports)
                AppendCompletedRow(report);

            if (Session.IsRunning)
            {
                _logCard.SetStatus(MolcaStatusKind.Idle, "running");
                if (Session.CurrentProgress.HasValue)
                {
                    BeginCheckLog(Session.CurrentProgress.Value);
                    if (!string.IsNullOrEmpty(Session.CurrentStatus))
                        _currentLogDetail.text = Session.CurrentStatus;
                }
            }
            else
            {
                AppendFinishNote(Session.WasCanceled);
            }
        }

        /// <summary>Appends a dotless, muted note line (run header / summary) and scrolls to it.</summary>
        private void AddLogNote(string text)
        {
            var note = new Label(text);
            note.AddToClassList("molca-doctor__log-note");
            _logScroll.Add(note);
            ScrollLogToEnd();
        }

        /// <summary>Appends a "running" row for the check about to execute; kept until it completes.</summary>
        private void BeginCheckLog(DoctorProgress p)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-doctor__log-row");

            _currentLogDot = new VisualElement();
            _currentLogDot.AddToClassList("molca-doctor__log-dot");
            _currentLogDot.AddToClassList("molca-doctor__log-dot--running");
            row.Add(_currentLogDot);

            var body = new VisualElement();
            body.AddToClassList("molca-doctor__log-body");
            row.Add(body);

            _currentHeadPrefix = $"[{p.CompletedCount + 1}/{p.TotalCount}] {p.CurrentCheck.Id}";
            _currentLogHead = new Label(_currentHeadPrefix);
            _currentLogHead.AddToClassList("molca-doctor__log-head");
            body.Add(_currentLogHead);

            _currentLogDetail = new Label(p.CurrentCheck.Description);
            _currentLogDetail.AddToClassList("molca-doctor__log-detail");
            body.Add(_currentLogDetail);

            _logScroll.Add(row);
            ScrollLogToEnd();

            // Tick the running row's elapsed time (read from the session's stopwatch, so a view that
            // attaches mid-check shows the true elapsed) so even a check that never reports sub-progress is
            // visibly alive. The scheduler only fires between the check's own yields, so a long synchronous
            // stretch still pauses it — hence also instrumenting the slow checks (DocLinks / DocsCoverage).
            _tick?.Pause();
            _tick = schedule.Execute(TickRunning).Every(250);
        }

        /// <summary>Refreshes the running row's head with its live elapsed time from the session.</summary>
        private void TickRunning()
        {
            if (_currentLogHead == null)
                return;
            _currentLogHead.text = $"{_currentHeadPrefix}  ·  running {FormatElapsed(Session.CurrentCheckElapsedMs)}";
        }

        /// <summary>Stops the live elapsed ticker. Idempotent.</summary>
        private void StopTick()
        {
            _tick?.Pause();
            _tick = null;
        }

        /// <summary>Appends a fully finalized row for an already-completed check (used when rebuilding).</summary>
        private void AppendCompletedRow(DoctorCheckReport report)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-doctor__log-row");

            var dot = new VisualElement();
            dot.AddToClassList("molca-doctor__log-dot");
            dot.AddToClassList(LogDotClass(report));
            row.Add(dot);

            var body = new VisualElement();
            body.AddToClassList("molca-doctor__log-body");
            row.Add(body);

            var summary = report.IsClean ? "clean" : DescribeCounts(report);
            var headLabel = new Label(
                $"[{report.Index + 1}/{report.TotalCount}] {report.Check.Id}  ·  {FormatElapsed(report.ElapsedMilliseconds)}  ·  {summary}");
            headLabel.AddToClassList("molca-doctor__log-head");
            body.Add(headLabel);

            var detailLabel = new Label(report.Check.Description);
            detailLabel.AddToClassList("molca-doctor__log-detail");
            body.Add(detailLabel);

            _logScroll.Add(row);
            ScrollLogToEnd();
        }

        /// <summary>Finalizes the current running row with its outcome, timing, and finding counts.</summary>
        private void CompleteCheckLog(DoctorCheckReport report)
        {
            StopTick();

            // No live row to finalize (e.g. this view attached in the gap between checks) — append a
            // finalized one so the trace stays complete.
            if (_currentLogDot == null)
            {
                AppendCompletedRow(report);
                return;
            }

            _currentLogDot.RemoveFromClassList("molca-doctor__log-dot--running");
            _currentLogDot.AddToClassList(LogDotClass(report));

            var summary = report.IsClean ? "clean" : DescribeCounts(report);
            _currentLogHead.text =
                $"[{report.Index + 1}/{report.TotalCount}] {report.Check.Id}  ·  {FormatElapsed(report.ElapsedMilliseconds)}  ·  {summary}";
            _currentLogDetail.text = report.Check.Description;

            _currentLogDot = null;
            _currentLogHead = null;
            _currentLogDetail = null;
            ScrollLogToEnd();
        }

        /// <summary>Closes out any running row and appends the run-summary line.</summary>
        private void FinishLog(bool canceled)
        {
            StopTick();

            // Close out a row still marked running (e.g. cancelled mid-check).
            if (_currentLogDot != null)
            {
                _currentLogDot.RemoveFromClassList("molca-doctor__log-dot--running");
                _currentLogDot.AddToClassList("molca-status-dot--idle");
                _currentLogDot = null;
                _currentLogHead = null;
                _currentLogDetail = null;
            }

            AppendFinishNote(canceled);
        }

        /// <summary>Appends the run-summary note (from the session's trace) and enables Copy.</summary>
        private void AppendFinishNote(bool canceled)
        {
            int ran = Session.Reports.Count;
            double totalMs = Session.Reports.Sum(r => r.ElapsedMilliseconds);
            int findings = Session.Reports.Sum(r => r.Findings.Count);
            string verb = canceled ? "Canceled" : "Done";
            AddLogNote($"{verb} — {ran} check{(ran == 1 ? "" : "s")} in {FormatElapsed(totalMs)}, "
                     + $"{findings} finding{(findings == 1 ? "" : "s")}.");

            _logCard.SetStatus(canceled ? MolcaStatusKind.Warning : MolcaStatusKind.Ok,
                canceled ? "canceled" : FormatElapsed(totalMs));
            _copyLogButton.SetEnabled(ran > 0);
        }

        /// <summary>Scrolls the log to its newest row once layout has resolved.</summary>
        private void ScrollLogToEnd()
        {
            if (_logScroll.childCount == 0)
                return;
            var last = _logScroll[_logScroll.childCount - 1];
            _logScroll.schedule.Execute(() => _logScroll.ScrollTo(last));
        }

        /// <summary>Comma-joined severity breakdown for a check's findings (e.g. "2 errors, 1 warning").</summary>
        private static string DescribeCounts(DoctorCheckReport report)
        {
            var parts = new List<string>();
            void Add(DoctorSeverity severity, string singular)
            {
                int n = report.CountAt(severity);
                if (n > 0)
                    parts.Add($"{n} {singular}{(n == 1 ? "" : "s")}");
            }

            Add(DoctorSeverity.Error, "error");
            Add(DoctorSeverity.Warning, "warning");
            // "info" reads the same singular/plural; keep it uncounted-plural for consistency.
            int infos = report.CountAt(DoctorSeverity.Info);
            if (infos > 0)
                parts.Add($"{infos} info");

            return parts.Count == 0 ? "clean" : string.Join(", ", parts);
        }

        /// <summary>Human-readable duration: milliseconds under a second, seconds under a minute, else m:ss.</summary>
        private static string FormatElapsed(double milliseconds)
        {
            if (milliseconds < 1000)
                return $"{milliseconds:0} ms";
            double seconds = milliseconds / 1000.0;
            if (seconds < 60)
                return $"{seconds:0.0} s";
            int minutes = (int)(seconds / 60);
            return $"{minutes}m {seconds - minutes * 60:00} s";
        }

        /// <summary>Status-dot class for a completed check: worst severity wins; clean is green.</summary>
        private static string LogDotClass(DoctorCheckReport report)
        {
            if (report.Crashed || report.CountAt(DoctorSeverity.Error) > 0)
                return "molca-status-dot--error";
            if (report.CountAt(DoctorSeverity.Warning) > 0)
                return "molca-status-dot--warn";
            if (report.CountAt(DoctorSeverity.Info) > 0)
                return "molca-status-dot--idle";
            return "molca-status-dot--ok";
        }

        /// <summary>Builds the plain-text run log for the Copy action.</summary>
        private string BuildTraceReport()
        {
            var reports = Session.Reports;
            var sb = new System.Text.StringBuilder();
            double totalMs = reports.Sum(r => r.ElapsedMilliseconds);
            sb.AppendLine($"Molca Doctor — run log ({reports.Count} check(s), {FormatElapsed(totalMs)}).");
            foreach (var r in reports)
            {
                var summary = r.IsClean ? "clean" : DescribeCounts(r);
                sb.AppendLine($"[{r.Index + 1}/{r.TotalCount}] {r.Check.Id} · {FormatElapsed(r.ElapsedMilliseconds)} · {summary}");
            }
            return sb.ToString();
        }

        private void BuildResults()
        {
            _results = new VisualElement();
            _results.AddToClassList("molca-doctor__results");
            _scroll.Add(_results);
        }

        // ── Session event handlers (all raised on the main thread) ───────────────────────────────

        private void HandleRunStarted()
        {
            SetRunningUi(true);
            ResetLog(Session.TotalCount);
            RefreshResults();
        }

        private void HandleProgress(DoctorProgress p)
        {
            if (p.CurrentCheck == null)
                return;
            ApplyProgressToBar(p, p.CurrentCheck.Description);
            BeginCheckLog(p);
        }

        private void HandleStatus(string detail)
        {
            if (string.IsNullOrEmpty(detail))
                return;
            _progressLabel.text = detail;
            // Mirror sub-check detail into the current log row so the trace shows progress within a check.
            if (_currentLogDetail != null)
                _currentLogDetail.text = detail;
        }

        private void HandleCheckCompleted(DoctorCheckReport report)
        {
            CompleteCheckLog(report);
            RefreshResults(); // findings accumulate live so a mid-run view shows partial results
        }

        private void HandleRunFinished(bool canceled)
        {
            SetRunningUi(false);
            FinishLog(canceled);
            RefreshResults();
        }

        private void ApplyProgressToBar(DoctorProgress p, string detail)
        {
            _progressBar.value = p.Fraction * 100f;
            _progressBar.title = $"({p.CompletedCount + 1}/{p.TotalCount}) {p.CurrentCheck?.Id}";
            _progressLabel.text = string.IsNullOrEmpty(detail) ? p.CurrentCheck?.Description : detail;
        }

        private void SetRunningUi(bool running)
        {
            _progressRow.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            _runButton.text = running ? "Running…" : "Run Checks";
            _runButton.SetEnabled(!running);
            if (running)
            {
                _progressBar.value = 0f;
                _progressBar.title = string.Empty;
                _progressLabel.text = string.Empty;
            }
        }

        private void RefreshResults()
        {
            var issues = Session.Issues;
            bool hasIssues = issues.Count > 0;
            _copyButton.SetEnabled(!Session.IsRunning && hasIssues);
            _exportButton.SetEnabled(!Session.IsRunning && hasIssues);

            UpdateChipText(_errorChip, "Errors", DoctorSeverity.Error);
            UpdateChipText(_warnChip, "Warnings", DoctorSeverity.Warning);
            UpdateChipText(_infoChip, "Info", DoctorSeverity.Info);

            _results.Clear();

            if (!Session.HasRun && !Session.IsRunning)
            {
                _results.Add(Placeholder("Run Checks to validate the project against Molca conventions."));
                return;
            }

            if (!hasIssues)
            {
                _results.Add(Placeholder(Session.IsRunning ? "Running…" : "All checks passed — no findings."));
                return;
            }

            foreach (var issue in issues.OrderByDescending(i => i.Severity))
            {
                if (!IsVisible(issue.Severity))
                    continue;
                _results.Add(BuildRow(issue));
            }
        }

        private void UpdateChipText(Button chip, string label, DoctorSeverity severity) =>
            chip.text = $"{label} ({Count(severity)})";

        private static Label Placeholder(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-doctor__placeholder");
            return label;
        }

        private static VisualElement BuildRow(DoctorIssue issue)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-doctor__row");

            var dot = new VisualElement();
            dot.AddToClassList("molca-doctor__row-dot");
            dot.AddToClassList(StatusClass(issue.Severity));
            row.Add(dot);

            var body = new VisualElement();
            body.AddToClassList("molca-doctor__row-body");
            row.Add(body);

            var checkId = new Label(issue.CheckId);
            checkId.AddToClassList("molca-doctor__row-check");
            body.Add(checkId);

            var message = new Label(issue.Message);
            message.AddToClassList("molca-doctor__row-message");
            body.Add(message);

            if (!string.IsNullOrEmpty(issue.Path))
            {
                var location = issue.Line > 0 ? $"{issue.Path}:{issue.Line}" : issue.Path;
                var link = new Button(() => PingLocation(issue)) { text = location };
                link.AddToClassList("molca-doctor__row-location");
                body.Add(link);
            }

            return row;
        }

        private void ExportReport()
        {
            var path = EditorUtility.SaveFilePanel("Export Doctor Report", "", "molca-doctor-report.txt", "txt");
            if (!string.IsNullOrEmpty(path))
                System.IO.File.WriteAllText(path, BuildReport());
        }

        private string BuildReport()
        {
            var issues = Session.Issues;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Molca Doctor — {issues.Count} finding(s): {Count(DoctorSeverity.Error)} error(s), {Count(DoctorSeverity.Warning)} warning(s).");
            foreach (var issue in issues.OrderByDescending(i => i.Severity))
                sb.AppendLine(issue.ToString());
            return sb.ToString();
        }

        private bool IsVisible(DoctorSeverity severity) => severity switch
        {
            DoctorSeverity.Error => _showErrors,
            DoctorSeverity.Warning => _showWarnings,
            _ => _showInfos,
        };

        private int Count(DoctorSeverity severity) => Session.Issues.Count(i => i.Severity == severity);

        private static string StatusClass(DoctorSeverity severity) => severity switch
        {
            DoctorSeverity.Error => "molca-status-dot--error",
            DoctorSeverity.Warning => "molca-status-dot--warn",
            _ => "molca-status-dot--idle",
        };

        private static void PingLocation(DoctorIssue issue)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(issue.Path);
            if (asset == null)
                return;
            if (issue.Line > 0)
                AssetDatabase.OpenAsset(asset, issue.Line);
            else
                EditorGUIUtility.PingObject(asset);
        }
    }
}
