using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    /// the Hub. Lifecycle (cancelling an in-flight run) is keyed on <see cref="DetachFromPanelEvent"/>
    /// rather than a window's <c>OnDisable</c>, so it cleans up wherever it is hosted.
    /// </remarks>
    public sealed class MolcaDoctorView : VisualElement
    {
        private const string UssPath = "Packages/com.molca.core/Editor/Doctor/MolcaDoctorView.uss";
        private const string ChecksCollapsedKey = "Doctor.ChecksCollapsed";
        private const string GroupExpandedKeyPrefix = "Doctor.Group.Expanded.";

        private List<DoctorIssue> _issues = new List<DoctorIssue>();
        private readonly HashSet<string> _disabledChecks = new HashSet<string>();
        private bool _showErrors = true;
        private bool _showWarnings = true;
        private bool _showInfos = true;
        private bool _hasRun;

        private bool _isRunning;
        private CancellationTokenSource _runCts;

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
            BuildResults();

            RefreshResults();
            RegisterCallback<DetachFromPanelEvent>(_ => Dispose());
        }

        /// <summary>Cancels any in-flight run. Idempotent.</summary>
        private void Dispose() => _runCts?.Cancel();

        private void BuildToolbar()
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("molca-doctor__toolbar");
            Add(toolbar);

            _runButton = MolcaButtons.Primary("Run Checks", () => _ = RunChecksAsync());
            _runButton.AddToClassList("molca-doctor__run");
            toolbar.Add(_runButton);

            _copyButton = MolcaButtons.Toolbar("Copy", () => EditorGUIUtility.systemCopyBuffer = BuildReport());
            toolbar.Add(_copyButton);

            _exportButton = MolcaButtons.Toolbar("Export…", ExportReport);
            _exportButton.style.marginLeft = 4;
            toolbar.Add(_exportButton);

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

            _cancelButton = MolcaButtons.Mini("Cancel", () => _runCts?.Cancel());
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
        /// toggle. Called on every selection change so chip active-state mirrors <see cref="_disabledChecks"/>.
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

            int enabled = checks.Count(c => !_disabledChecks.Contains(c.Id));
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
                    _disabledChecks.Remove(id);
                else
                    _disabledChecks.Add(id);
            }

            RebuildCheckChips();
            UpdateChecksCount();
        }

        /// <summary>Shows the enabled/total check count on the card header (e.g. "16/18").</summary>
        private void UpdateChecksCount()
        {
            int total = MolcaDoctor.Checks.Count;
            int enabled = total - _disabledChecks.Count;
            _checkCard.SetStatus(MolcaStatusKind.Idle, $"{enabled}/{total}");
        }

        private Button MakeCheckChip(IDoctorCheck check)
        {
            var chip = new Button { text = check.Id, tooltip = check.Description };
            chip.AddToClassList("molca-doctor__chip");
            chip.EnableInClassList("molca-doctor__chip--active", !_disabledChecks.Contains(check.Id));
            chip.clicked += () =>
            {
                if (_disabledChecks.Contains(check.Id))
                    _disabledChecks.Remove(check.Id);
                else
                    _disabledChecks.Add(check.Id);
                chip.EnableInClassList("molca-doctor__chip--active", !_disabledChecks.Contains(check.Id));
                UpdateChecksCount();
            };
            return chip;
        }

        private void SetAllChecks(bool enabled)
        {
            _disabledChecks.Clear();
            if (!enabled)
                foreach (var c in MolcaDoctor.Checks)
                    _disabledChecks.Add(c.Id);

            // Rebuild the chips so their active state mirrors the new selection.
            RebuildCheckChips();
            UpdateChecksCount();
        }

        private void BuildResults()
        {
            _results = new VisualElement();
            _results.AddToClassList("molca-doctor__results");
            _scroll.Add(_results);
        }

        /// <summary>Runs the enabled checks asynchronously with an inline, cancelable progress bar.</summary>
        public async Awaitable RunChecksAsync()
        {
            if (_isRunning)
                return;

            var enabled = new HashSet<string>(MolcaDoctor.Checks.Select(c => c.Id).Except(_disabledChecks));
            _isRunning = true;
            _runCts = new CancellationTokenSource();
            SetRunningUi(true);

            try
            {
                _issues = await MolcaDoctor.RunAllAsync(
                    enabled,
                    OnProgress,
                    _runCts.Token,
                    OnStatus);
                _hasRun = true;
            }
            catch (OperationCanceledException)
            {
                // Canceled by the user — keep whatever _issues already held; not an error.
            }
            catch (Exception e)
            {
                Debug.LogError($"[MolcaDoctor] Run failed: {e}");
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                _isRunning = false;
                SetRunningUi(false);
                RefreshResults();
            }
        }

        // RunAllAsync invokes both callbacks on the main thread, so touching UI here is safe.
        private void OnProgress(DoctorProgress p)
        {
            if (p.CurrentCheck == null)
                return;
            _progressBar.value = p.Fraction * 100f;
            _progressBar.title = $"({p.CompletedCount + 1}/{p.TotalCount}) {p.CurrentCheck.Id}";
            _progressLabel.text = p.CurrentCheck.Description;
        }

        private void OnStatus(string detail)
        {
            if (!string.IsNullOrEmpty(detail))
                _progressLabel.text = detail;
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
            bool hasIssues = _issues.Count > 0;
            _copyButton.SetEnabled(!_isRunning && hasIssues);
            _exportButton.SetEnabled(!_isRunning && hasIssues);

            UpdateChipText(_errorChip, "Errors", DoctorSeverity.Error);
            UpdateChipText(_warnChip, "Warnings", DoctorSeverity.Warning);
            UpdateChipText(_infoChip, "Info", DoctorSeverity.Info);

            _results.Clear();

            if (!_hasRun)
            {
                _results.Add(Placeholder("Run Checks to validate the project against Molca conventions."));
                return;
            }

            if (!hasIssues)
            {
                _results.Add(Placeholder("All checks passed — no findings."));
                return;
            }

            foreach (var issue in _issues.OrderByDescending(i => i.Severity))
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
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Molca Doctor — {_issues.Count} finding(s): {Count(DoctorSeverity.Error)} error(s), {Count(DoctorSeverity.Warning)} warning(s).");
            foreach (var issue in _issues.OrderByDescending(i => i.Severity))
                sb.AppendLine(issue.ToString());
            return sb.ToString();
        }

        private bool IsVisible(DoctorSeverity severity) => severity switch
        {
            DoctorSeverity.Error => _showErrors,
            DoctorSeverity.Warning => _showWarnings,
            _ => _showInfos,
        };

        private int Count(DoctorSeverity severity) => _issues.Count(i => i.Severity == severity);

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
