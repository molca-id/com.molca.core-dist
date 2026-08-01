using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Validation;
using Molca.Networking.Configuration;
using UnityEditor;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub.Views
{
    /// <summary>
    /// Answers one question: is this project ready to communicate safely?
    /// </summary>
    /// <remarks>
    /// Every metric here leads somewhere. A count that cannot be acted on is decoration, so the counts are
    /// buttons, the matrix cells navigate to the binding they describe, and the action list is ordered by
    /// what would block a release first (plan §7.4).
    /// </remarks>
    internal sealed class NetworkOverviewView : VisualElement
    {
        private readonly NetworkHubSession _session;

        /// <summary>Builds the overview.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkOverviewView(NetworkHubSession session)
        {
            _session = session;
            AddToClassList("molca-network__view");
            style.flexGrow = 1;

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            Add(scroll);

            scroll.Add(BuildReadiness());
            scroll.Add(BuildActions());
            scroll.Add(BuildMatrix());
            scroll.Add(BuildCredentialReadiness());
            scroll.Add(BuildLegacyStatus());
        }

        private VisualElement BuildReadiness()
        {
            var catalog = _session.Catalog;
            var report = _session.Validation;

            var status = report.ErrorCount > 0 ? MolcaStatusKind.Error
                : report.WarningCount > 0 ? MolcaStatusKind.Warning
                : MolcaStatusKind.Ok;

            var card = NetworkHubUi.Card(
                catalog.name,
                report.Summarize(),
                status,
                report.ErrorCount > 0 ? "Not usable as authored" : report.WarningCount > 0 ? "Usable" : "Valid");

            card.Body.Add(NetworkHubUi.Field("Schema version", $"v{catalog.SchemaVersion}",
                catalog.RequiresSchemaMigration
                    ? "This asset predates the installed framework and needs a schema migration."
                    : null));

            card.Body.Add(NetworkHubUi.Field(
                "Runtime default environment",
                string.IsNullOrEmpty(catalog.DefaultEnvironmentId) ? null : catalog.DefaultEnvironmentId,
                "The environment a call site targets when it names none."));

            card.Body.Add(NetworkHubUi.Field(
                "Authoring preview",
                _session.PreviewEnvironmentId,
                "Affects previews in this workspace only. It never changes what the runtime does."));

            card.Body.Add(NetworkHubUi.Field(
                "Loaded at runtime",
                _session.IsCatalogRegistered ? "Yes" : "No — not registered on GlobalSettings",
                _session.IsCatalogRegistered
                    ? null
                    : "The asset exists but nothing loads it. Register it from the ⋯ menu."));

            var counts = new VisualElement();
            counts.AddToClassList("molca-network__counts");
            counts.Add(CountChip(catalog.Environments.Count, "environments", NetworkHubViews.Environments));
            counts.Add(CountChip(catalog.Services.Count, "services", NetworkHubViews.Services));
            counts.Add(CountChip(catalog.EndpointCollections.Count, "collections", NetworkHubViews.Endpoints));
            counts.Add(CountChip(catalog.PolicyProfiles.Count, "policies", NetworkHubViews.Policies));
            counts.Add(CountChip(catalog.CredentialProfiles.Count, "credentials", NetworkHubViews.Credentials));
            card.Body.Add(counts);

            return card;
        }

        private Button CountChip(int count, string label, string viewId)
        {
            var chip = new Button(() => _session.Navigate(new NetworkHubNavigationTarget(viewId)))
            {
                text = $"{count} {label}"
            };
            chip.AddToClassList("molca-network__count-chip");
            if (count == 0) chip.AddToClassList("molca-network__muted");
            return chip;
        }

        /// <summary>
        /// The prioritized action list: errors first, then warnings, each navigating to the entity.
        /// </summary>
        private VisualElement BuildActions()
        {
            var report = _session.Validation;
            var actionable = report.AtLeast(NetworkValidationSeverity.Warning);

            var card = NetworkHubUi.Card(
                "What to do next",
                actionable.Count == 0 ? "Nothing is blocking this catalog." : null,
                actionable.Count == 0 ? MolcaStatusKind.Ok : NetworkHubUi.StatusOf(actionable[0].Severity),
                actionable.Count == 0 ? "Clear" : $"{actionable.Count} item(s)");

            if (actionable.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "Every environment resolves, every service is bound where it is used, and no credential " +
                    "scope conflicts with a bound host."));
                return card;
            }

            // AtLeast preserves report order, which is already deterministic; sorting by severity here puts
            // the release-blocking errors above the advisory warnings without disturbing that.
            actionable.Sort((left, right) => right.Severity.CompareTo(left.Severity));

            int shown = 0;
            foreach (var finding in actionable)
            {
                if (shown++ >= MaxActions) break;
                card.Body.Add(NetworkHubUi.FindingRow(finding, () => NavigateTo(finding)));
            }

            if (actionable.Count > MaxActions)
            {
                card.Body.Add(NetworkHubUi.Actions(MolcaButtons.Mini(
                    $"See all {actionable.Count} in Diagnostics",
                    () => _session.Navigate(new NetworkHubNavigationTarget(NetworkHubViews.Diagnostics)))));
            }

            return card;
        }

        /// <summary>How many findings the overview lists before deferring to Diagnostics.</summary>
        private const int MaxActions = 6;

        private void NavigateTo(NetworkValidationFinding finding) =>
            _session.Navigate(NetworkHubDeepLinks.For(finding));

        /// <summary>
        /// The environment-by-service binding matrix — the one place the multi-environment shape of the
        /// project is visible at a glance.
        /// </summary>
        /// <remarks>
        /// An empty cell is meaningful and is rendered as such: it means the service does not exist in that
        /// environment, which the resolver reports rather than papering over with another environment's
        /// origin.
        /// </remarks>
        private VisualElement BuildMatrix()
        {
            var catalog = _session.Catalog;
            var card = NetworkHubUi.Card(
                "Bindings",
                "Service by environment. An empty cell means the service does not exist there.",
                MolcaStatusKind.None);

            if (catalog.Environments.Count == 0 || catalog.Services.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "Add at least one environment and one service to see the binding matrix."));
                return card;
            }

            var table = new VisualElement();
            table.AddToClassList("molca-network__matrix");

            var header = new VisualElement();
            header.AddToClassList("molca-network__matrix-row");
            header.Add(MatrixCell("Service", header: true));
            foreach (var environment in catalog.Environments)
            {
                if (environment == null) continue;
                header.Add(MatrixCell(environment.Id, header: true));
            }
            table.Add(header);

            foreach (var service in catalog.Services)
            {
                if (service == null) continue;

                var row = new VisualElement();
                row.AddToClassList("molca-network__matrix-row");

                var name = MatrixCell(service.Id, header: false);
                name.AddToClassList("molca-network__matrix-cell--name");
                row.Add(name);

                foreach (var environment in catalog.Environments)
                {
                    if (environment == null) continue;
                    row.Add(MatrixCell(service, environment));
                }

                table.Add(row);
            }

            card.Body.Add(table);
            return card;
        }

        private static VisualElement MatrixCell(string text, bool header)
        {
            var cell = new Label(text);
            cell.AddToClassList("molca-network__matrix-cell");
            if (header) cell.AddToClassList("molca-network__matrix-cell--header");
            return cell;
        }

        private VisualElement MatrixCell(NetworkServiceDefinition service, NetworkEnvironmentProfile environment)
        {
            var binding = service.FindBinding(environment.Id);

            string glyph;
            MolcaStatusKind status;
            string tooltip;

            if (binding == null)
            {
                glyph = "·";
                status = MolcaStatusKind.Idle;
                tooltip = $"'{service.Id}' has no binding in '{environment.Id}'. A request there fails with a " +
                          "route-resolution error rather than falling back to another environment.";
            }
            else if (!binding.Enabled)
            {
                glyph = "○";
                status = MolcaStatusKind.Warning;
                tooltip = $"'{service.Id}' is bound in '{environment.Id}' but disabled.";
            }
            else if (string.IsNullOrWhiteSpace(binding.HttpOrigin))
            {
                glyph = "!";
                status = MolcaStatusKind.Error;
                tooltip = $"'{service.Id}' is bound in '{environment.Id}' with no HTTP origin.";
            }
            else
            {
                glyph = "●";
                status = MolcaStatusKind.Ok;
                tooltip = binding.HttpOrigin;
            }

            var cell = new Button(() => _session.Navigate(
                NetworkHubNavigationTarget.Service(service.Id, environment.Id)))
            {
                text = glyph,
                tooltip = tooltip
            };
            cell.AddToClassList("molca-network__matrix-cell");
            cell.AddToClassList("molca-network__matrix-cell--" + status.ToString().ToLowerInvariant());
            return cell;
        }

        /// <summary>
        /// Credential-source readiness, by profile. Never a value — only whether a source is declared.
        /// </summary>
        private VisualElement BuildCredentialReadiness()
        {
            var catalog = _session.Catalog;
            var card = NetworkHubUi.Card(
                "Credential readiness",
                "Provider metadata only. No value is read, shown, or stored.",
                MolcaStatusKind.None);

            if (catalog.CredentialProfiles.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note("No credential profiles. Every service sends anonymously."));
                return card;
            }

            foreach (var profile in catalog.CredentialProfiles)
            {
                if (profile == null) continue;

                bool declared = !profile.IsAnonymous;
                bool scoped = profile.AllowedServiceIds.Count > 0 && profile.AllowedHostPatterns.Count > 0;

                var status = !declared ? MolcaStatusKind.Idle
                    : !scoped ? MolcaStatusKind.Warning
                    : MolcaStatusKind.Ok;

                card.Body.Add(NetworkHubUi.ListRow(
                    profile.Id,
                    !declared ? "No provider — this profile never attaches a credential"
                        : !scoped ? "Declared but unscoped, so it is denied everywhere"
                        : $"{profile.ProviderKind} · {profile.AllowedServiceIds.Count} service(s), " +
                          $"{profile.AllowedHostPatterns.Count} host pattern(s)",
                    status,
                    "Whether this profile could supply a credential to anything.",
                    selected: false,
                    onClick: () => _session.Navigate(
                        new NetworkHubNavigationTarget(NetworkHubViews.Credentials, profile.Id))));
            }

            return card;
        }

        private VisualElement BuildLegacyStatus()
        {
            var card = NetworkHubUi.Card(
                "Legacy assets",
                "Scanning walks the whole project, so it runs when you ask.",
                MolcaStatusKind.None);

            var body = card.Body;
            var result = new VisualElement();

            body.Add(NetworkHubUi.Actions(MolcaButtons.Mini("Scan legacy networking", () =>
            {
                result.Clear();

                var plan = _session.LegacyPlan();
                if (!plan.HasWork)
                {
                    result.Add(NetworkHubUi.Note(
                        "Nothing left to migrate — the catalog already covers everything the scan found."));
                    return;
                }

                result.Add(NetworkHubUi.Note($"{plan.Steps.Count} step(s) remain. {plan.Report.Summarize()}."));

                var preview = new TextField { multiline = true, value = plan.Describe() };
                preview.AddToClassList("molca-network__report");
                preview.isReadOnly = true;
                result.Add(preview);

                result.Add(NetworkHubUi.Actions(
                    MolcaButtons.Primary("Apply migration", () => ApplyMigration(plan)),
                    MolcaButtons.Mini("Open migration guide",
                        () => NetworkHubUi.OpenDoc("NETWORKING_MIGRATION"))));
            })));

            body.Add(result);
            return card;
        }

        private void ApplyMigration(Molca.Editor.Networking.Migration.LegacyMigrationPlan plan)
        {
            if (!EditorUtility.DisplayDialog(
                    "Apply legacy networking migration?",
                    $"{plan.Steps.Count} step(s) will be applied to '{_session.Catalog.name}'.\n\n" +
                    "No legacy asset is modified or deleted, and the whole run is a single Undo step.",
                    "Apply", "Cancel"))
            {
                return;
            }

            var result = Molca.Editor.Networking.Migration.LegacyMigrationExecutor.ApplyTo(
                _session.Catalog, plan);

            UnityEngine.Debug.Log($"[Network] {result.Summarize()}");
            foreach (string failure in result.Failures)
                UnityEngine.Debug.LogWarning($"[Network] Migration step failed: {failure}");

            _session.Reload();
        }
    }
}
