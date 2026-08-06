using System;
using System.Globalization;
using System.Threading;
using Molca;
using Molca.Editor.Licensing;
using Molca.Editor.Projects;
using Molca.Editor.UI.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Read-only project health for the Molca Hub Settings workspace: what state the connected backend
    /// project is in, and what is broken.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the Health rail section is active.
    /// <para>
    /// <b>This section renders a report; it does not compute one.</b> Every severity and every line of
    /// finding text arrives from <c>GET /api/projects/:projectId/health</c> — the same payload the customer
    /// dashboard and the operator support view read. A local threshold ("a token expiring in five days is
    /// fine, actually") would mean the Hub and the dashboard telling one team two different things about one
    /// project, which is worse than the Hub not having the panel at all. The only judgement made here is
    /// which status dot colour a severity string maps to.
    /// </para>
    /// <para>
    /// It is also read-only in the strong sense: there is no health write path on any surface, so nothing
    /// here can clear a finding. Each finding names where the fix lives, and the dashboard button opens it.
    /// </para>
    /// <para>
    /// Network work follows the async contract: an <see cref="Awaitable"/> fetch, discarded explicitly
    /// because it owns its own exceptions, keyed on a cancellation scope that dies with the section, and
    /// re-checking that the element is still attached after every <c>await</c> before touching the UI.
    /// </para>
    /// </remarks>
    internal sealed class MolcaHubHealthSection : VisualElement
    {
        private readonly Action<MolcaHubSection> _navigate;
        private readonly MolcaSectionCard _reportCard;
        private readonly Label _stateLabel;
        private readonly Label _checkedLabel;
        private readonly VisualElement _panelHost = new();
        private readonly Button _refreshButton;

        // One scope, not the two the Tasks section keeps: this surface only ever reads, so there is no
        // in-flight write for a refresh to abort. Cancelled on detach and whenever a newer fetch supersedes
        // an older one; dropped to null rather than left disposed so a reattach builds a fresh scope.
        private CancellationTokenSource _fetchCts;
        private bool _busy;

        internal MolcaHubHealthSection(Action<MolcaHubSection> navigate)
        {
            _navigate = navigate;
            AddToClassList("molca-hub-health-section");

            _reportCard = new MolcaSectionCard("Project Health",
                status: MolcaStatusKind.Idle, statusText: "Not checked",
                helpTooltip: "Read-only. The Molca control plane computes every severity here, so the Hub " +
                             "and your dashboard always agree about this project.");
            Add(_reportCard);

            _refreshButton = new Button(TriggerFetch) { text = "Refresh", tooltip = "Re-read this project's health." };
            _refreshButton.AddToClassList("molca-hub-mini-button");
            _reportCard.AddHeaderAction(_refreshButton);

            var dashboard = new Button(() => Application.OpenURL(
                DevLicenseConfig.ServerBaseUrl.TrimEnd('/') + "/dashboard"))
            {
                text = "Open dashboard",
                tooltip = "Open the project dashboard, where the underlying releases, builds, and tokens live."
            };
            dashboard.AddToClassList("molca-hub-mini-button");
            _reportCard.AddHeaderAction(dashboard);

            _stateLabel = new Label();
            _stateLabel.AddToClassList("molca-hub-health-state");
            _reportCard.Body.Add(_stateLabel);

            _checkedLabel = new Label();
            _checkedLabel.AddToClassList("molca-hub-muted");
            _reportCard.Body.Add(_checkedLabel);

            _panelHost.AddToClassList("molca-hub-health-panels");
            Add(_panelHost);

            RegisterCallback<DetachFromPanelEvent>(_ => OnDetach());

            TriggerFetch();
        }

        /// <summary>
        /// Renders a report that has already been fetched.
        /// </summary>
        /// <param name="report">The server's report; <c>null</c> is treated as an empty response.</param>
        /// <remarks>
        /// Separate from the fetch so the rendering is exercisable without a control plane, and so a future
        /// caller with a report in hand (a cached payload, a remote session) can reuse the same view.
        /// </remarks>
        internal void RenderReport(ProjectHealthResponse report)
        {
            _panelHost.Clear();

            if (report == null)
            {
                ShowState("The control plane returned no report.", problem: true);
                return;
            }

            _reportCard.SetStatus(StatusFor(report.severity), SeverityLabel(report.severity));
            _stateLabel.text = string.IsNullOrEmpty(report.summary)
                ? "The control plane returned a report with no summary."
                : report.summary;
            _stateLabel.EnableInClassList("molca-hub-health-state--problem", false);
            _checkedLabel.text = CheckedText(report.generatedAt);

            var panels = report.panels ?? Array.Empty<ProjectHealthPanel>();
            if (panels.Length == 0)
            {
                // Distinct from a healthy project on purpose: no panels means the reader's role reaches none
                // of them, and rendering that as "everything is fine" would be the Hub inventing reassurance.
                _panelHost.Add(Muted("Your role does not reach any area of this project's health."));
                return;
            }

            foreach (var area in panels)
                _panelHost.Add(BuildPanelCard(area));
        }

        // `area`, not `panel`: VisualElement.panel is the attachment check every await in this file relies
        // on, and shadowing it here is how a post-await guard silently starts testing the wrong thing.
        private static VisualElement BuildPanelCard(ProjectHealthPanel area)
        {
            var card = new MolcaSectionCard(
                string.IsNullOrEmpty(area?.title) ? "Unknown area" : area.title,
                subtitle: area?.summary,
                status: StatusFor(area?.severity),
                statusText: SeverityLabel(area?.severity));

            var findings = area?.findings ?? Array.Empty<ProjectHealthFinding>();
            if (findings.Length == 0)
            {
                card.Body.Add(Muted("Nothing to do here."));
                return card;
            }

            foreach (var finding in findings)
                card.Body.Add(BuildFindingRow(finding));

            return card;
        }

        private static VisualElement BuildFindingRow(ProjectHealthFinding finding)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-health-finding");

            var dot = new VisualElement();
            dot.AddToClassList("molca-status-dot");
            dot.AddToClassList(DotClass(finding?.severity));
            row.Add(dot);

            var text = new VisualElement();
            text.AddToClassList("molca-hub-health-finding__text");
            row.Add(text);

            var message = new Label(finding?.message ?? string.Empty);
            message.AddToClassList("molca-hub-health-finding__message");
            text.Add(message);

            // The action is the half of a finding that makes it a to-do rather than a complaint, so it is
            // always rendered when the server sent one — never folded into a tooltip.
            if (!string.IsNullOrEmpty(finding?.action))
            {
                var action = new Label(finding.action);
                action.AddToClassList("molca-hub-health-finding__action");
                text.Add(action);
            }

            return row;
        }

        private void TriggerFetch()
        {
            if (_busy) return;

            string projectId = MolcaProjectSettings.Instance != null
                ? MolcaProjectSettings.Instance.ProjectId
                : string.Empty;

            if (string.IsNullOrWhiteSpace(projectId))
            {
                ShowNotConnected();
                return;
            }

            _ = FetchAsync(projectId);
        }

        private async Awaitable FetchAsync(string projectId)
        {
            CancelInFlightFetch();
            _fetchCts = new CancellationTokenSource();
            var token = _fetchCts.Token;

            _busy = true;
            _refreshButton.SetEnabled(false);
            _panelHost.Clear();
            _reportCard.SetStatus(MolcaStatusKind.Idle, "Checking");
            _stateLabel.text = "Reading this project's health…";
            _stateLabel.EnableInClassList("molca-hub-health-state--problem", false);

            try
            {
                var result = await new MolcaProjectApiClient().HealthAsync(projectId, token);

                if (panel == null || token.IsCancellationRequested) return;

                if (!result.Success)
                {
                    ShowState(result.Error, problem: true);
                    return;
                }

                RenderReport(result.Value);
            }
            catch (OperationCanceledException)
            {
                // Section detached or a newer fetch superseded this one — exit quietly, not as an error.
            }
            catch (Exception exception)
            {
                if (panel != null) ShowState(exception.Message, problem: true);
            }
            finally
            {
                _busy = false;
                if (panel != null) _refreshButton.SetEnabled(true);
            }
        }

        /// <summary>
        /// The state for a repository with no backend project, which is not a health problem to solve here.
        /// </summary>
        private void ShowNotConnected()
        {
            _panelHost.Clear();
            _reportCard.SetStatus(MolcaStatusKind.Idle, "Not connected");
            _stateLabel.text =
                "This repository is not connected to a Molca project, so there is no health to report.";
            _stateLabel.EnableInClassList("molca-hub-health-state--problem", false);
            _checkedLabel.text = string.Empty;

            var open = new Button(() => _navigate?.Invoke(MolcaHubSection.Project))
            {
                text = "Open Project settings",
                tooltip = "Connect this repository to a backend project."
            };
            open.AddToClassList("molca-hub-mini-button");
            _panelHost.Add(open);
        }

        private void ShowState(string message, bool problem)
        {
            _panelHost.Clear();
            _reportCard.SetStatus(problem ? MolcaStatusKind.Error : MolcaStatusKind.Idle,
                problem ? "Unavailable" : "Not checked");
            // A failed read must not read like a healthy project: the report is unknown, which is its own
            // state, and the reader needs to know the difference before trusting a green dot's absence.
            _stateLabel.text = string.IsNullOrEmpty(message) ? "Health is unavailable." : message;
            _stateLabel.EnableInClassList("molca-hub-health-state--problem", problem);
            _checkedLabel.text = string.Empty;
        }

        private static Label Muted(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-hub-muted");
            return label;
        }

        /// <summary>Local wall-clock time the server assembled the report, or nothing when it is unparseable.</summary>
        private static string CheckedText(string generatedAt)
        {
            if (string.IsNullOrEmpty(generatedAt)) return string.Empty;
            // RoundtripKind alone, and it cannot be combined with AdjustToUniversal — that pair throws
            // ArgumentException rather than being ignored. The server sends a Z-suffixed instant, so
            // RoundtripKind already yields a UTC DateTime for ToLocalTime to convert.
            return DateTime.TryParse(generatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var moment)
                ? $"Checked {moment.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}"
                : string.Empty;
        }

        /// <summary>The one local judgement in this section: a server severity string to a status dot.</summary>
        private static MolcaStatusKind StatusFor(string severity) => severity switch
        {
            "ok" => MolcaStatusKind.Ok,
            "attention" => MolcaStatusKind.Warning,
            "problem" => MolcaStatusKind.Error,
            _ => MolcaStatusKind.Idle,
        };

        private static string DotClass(string severity) => StatusFor(severity) switch
        {
            MolcaStatusKind.Ok => "molca-status-dot--ok",
            MolcaStatusKind.Warning => "molca-status-dot--warn",
            MolcaStatusKind.Error => "molca-status-dot--error",
            _ => "molca-status-dot--idle",
        };

        private static string SeverityLabel(string severity) => severity switch
        {
            "ok" => "Healthy",
            "attention" => "Needs attention",
            "problem" => "Problem",
            _ => "Unknown",
        };

        private void OnDetach() => CancelInFlightFetch();

        private void CancelInFlightFetch()
        {
            if (_fetchCts == null) return;
            _fetchCts.Cancel();
            _fetchCts.Dispose();
            _fetchCts = null;
        }
    }
}
