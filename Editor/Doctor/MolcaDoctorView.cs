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

        private MolcaSectionCard _checkCard;
        private VisualElement _checkChipRow;
        private ScrollView _results;

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
            Add(card);

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

            _checkChipRow = new VisualElement();
            _checkChipRow.AddToClassList("molca-doctor__chip-row");
            card.Body.Add(_checkChipRow);

            foreach (var check in MolcaDoctor.Checks)
                _checkChipRow.Add(MakeCheckChip(check));

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
            _checkChipRow.Clear();
            foreach (var check in MolcaDoctor.Checks)
                _checkChipRow.Add(MakeCheckChip(check));

            UpdateChecksCount();
        }

        private void BuildResults()
        {
            _results = new ScrollView();
            _results.AddToClassList("molca-doctor__results");
            Add(_results);
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
