using System;
using System.Collections.Generic;
using System.Text;
using Molca.Editor.UI.Components;
using Molca.Editor.Networking.Migration;
using Molca.Editor.Networking.Validation;
using UnityEditor;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub.Views
{
    /// <summary>
    /// The full catalog validation tree, grouped by severity, with an Open action per finding.
    /// </summary>
    /// <remarks>
    /// Reads <c>NetworkCatalogValidator</c> and nothing else. Doctor and the build gate consume the same
    /// validator, so a finding here, a finding in Doctor, and a build failure are the same finding with
    /// the same code — there is no second set of networking rules to fall out of step (plan §7.13).
    /// <para>
    /// The legacy compatibility audit is a separate section because it walks the project rather than the
    /// catalog, so it runs when asked rather than on every reload.
    /// </para>
    /// </remarks>
    internal sealed class NetworkDiagnosticsView : VisualElement
    {
        private readonly NetworkHubSession _session;
        private readonly VisualElement _legacySection;

        /// <summary>Builds the view.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkDiagnosticsView(NetworkHubSession session)
        {
            _session = session;
            AddToClassList("molca-network__view");
            style.flexGrow = 1;

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            Add(scroll);

            scroll.Add(BuildSummary());

            var report = _session.Validation;
            scroll.Add(BuildGroup("Errors", NetworkValidationSeverity.Error, report));
            scroll.Add(BuildGroup("Warnings", NetworkValidationSeverity.Warning, report));
            scroll.Add(BuildGroup("Info", NetworkValidationSeverity.Info, report));

            _legacySection = new VisualElement();
            scroll.Add(BuildLegacySection());
            scroll.Add(_legacySection);
        }

        private VisualElement BuildSummary()
        {
            var report = _session.Validation;

            var status = report.ErrorCount > 0 ? MolcaStatusKind.Error
                : report.WarningCount > 0 ? MolcaStatusKind.Warning
                : MolcaStatusKind.Ok;

            var card = NetworkHubUi.Card(
                "Catalog validation",
                report.Summarize(),
                status,
                report.IsValid ? "Usable" : "Not usable as authored");

            card.Body.Add(NetworkHubUi.Field(
                "Build gate",
                _session.Catalog.FailBuildOnValidationError
                    ? "Errors fail the build"
                    : "Errors warn only",
                "Authored on the catalog. Turn it on once the catalog is clean, so a regression cannot ship."));

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Re-validate", () => _session.Reload()),
                MolcaButtons.Mini("Copy report", CopyReport)));

            return card;
        }

        private VisualElement BuildGroup(
            string title,
            NetworkValidationSeverity severity,
            NetworkValidationReport report)
        {
            var findings = new List<NetworkValidationFinding>();
            foreach (var finding in report.Findings)
            {
                if (finding.Severity == severity) findings.Add(finding);
            }

            var card = NetworkHubUi.Card(
                title,
                null,
                findings.Count == 0 ? MolcaStatusKind.Ok : NetworkHubUi.StatusOf(severity),
                findings.Count == 0 ? "None" : findings.Count.ToString());

            if (findings.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note($"No {title.ToLowerInvariant()}."));
                return card;
            }

            // Grouped by entity so several findings about one service read as one problem to go fix,
            // rather than as several unrelated items.
            var byEntity = new Dictionary<string, List<NetworkValidationFinding>>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (var finding in findings)
            {
                string key = string.IsNullOrEmpty(finding.EntityId)
                    ? finding.EntityKind.ToString()
                    : $"{finding.EntityKind} · {finding.EntityId}";

                if (!byEntity.TryGetValue(key, out var bucket))
                {
                    bucket = new List<NetworkValidationFinding>();
                    byEntity[key] = bucket;
                    order.Add(key);
                }
                bucket.Add(finding);
            }

            foreach (string key in order)
            {
                var heading = NetworkHubUi.Heading(key);
                card.Body.Add(heading);

                foreach (var finding in byEntity[key])
                {
                    card.Body.Add(NetworkHubUi.FindingRow(
                        finding,
                        () => OpenFinding(finding)));
                }
            }

            return card;
        }

        /// <summary>
        /// Navigates to the entity a finding names, and selects its asset when it has one.
        /// </summary>
        private void OpenFinding(NetworkValidationFinding finding)
        {
            if (finding.TargetObject != null)
            {
                Selection.activeObject = finding.TargetObject;
                EditorGUIUtility.PingObject(finding.TargetObject);
            }

            _session.Navigate(NetworkHubDeepLinks.For(finding));
        }

        /// <summary>
        /// The project-level legacy audit, run on request.
        /// </summary>
        /// <remarks>
        /// Separate from catalog validation because it reads every asset in the project. Keeping it out of
        /// the automatic reload is what lets the catalog validator stay a pure function usable in a build
        /// gate and in tests.
        /// </remarks>
        private VisualElement BuildLegacySection()
        {
            var card = NetworkHubUi.Card(
                "Legacy compatibility",
                "Walks the whole project, so it runs when you ask.",
                MolcaStatusKind.None);

            card.Body.Add(NetworkHubUi.Actions(MolcaButtons.Mini("Run legacy audit", RunLegacyAudit)));
            return card;
        }

        private void RunLegacyAudit()
        {
            _legacySection.Clear();

            var report = LegacyCompatibilityAudit.Audit(_session.LegacyPlan().Report);

            var card = NetworkHubUi.Card(
                "Legacy audit",
                report.Summarize(),
                report.ErrorCount > 0 ? MolcaStatusKind.Error
                    : report.WarningCount > 0 ? MolcaStatusKind.Warning
                    : MolcaStatusKind.Ok,
                report.Findings.Count == 0 ? "Clear" : $"{report.Findings.Count} finding(s)");

            if (report.Findings.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "Nothing outstanding: every legacy artifact is either migrated or accounted for."));
                _legacySection.Add(card);
                return;
            }

            foreach (var finding in report.Findings)
            {
                card.Body.Add(NetworkHubUi.FindingRow(finding, finding.TargetObject == null ? null : () =>
                {
                    Selection.activeObject = finding.TargetObject;
                    EditorGUIUtility.PingObject(finding.TargetObject);
                }));
            }

            _legacySection.Add(card);
        }

        /// <summary>
        /// Copies the report as text, for a bug report or a code review comment.
        /// </summary>
        /// <remarks>
        /// Findings carry no credential value by construction — they name profiles and hosts, never
        /// secrets — so the export needs no redaction pass of its own.
        /// </remarks>
        private void CopyReport()
        {
            var report = _session.Validation;
            var text = new StringBuilder();

            text.AppendLine($"[Molca Network] {_session.Catalog.name} — {report.Summarize()}");

            foreach (var finding in report.Findings)
            {
                text.AppendLine($"  {finding}");
                if (!string.IsNullOrEmpty(finding.Remedy))
                    text.AppendLine($"      → {finding.Remedy}");
            }

            EditorGUIUtility.systemCopyBuffer = text.ToString();
            UnityEngine.Debug.Log($"[Network] Copied {report.Findings.Count} finding(s) to the clipboard.");
        }
    }
}
