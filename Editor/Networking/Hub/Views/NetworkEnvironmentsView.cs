using System;
using Molca.Editor.UI.Components;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Validation;
using Molca.Networking.Configuration;
using UnityEditor;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub.Views
{
    /// <summary>
    /// Authors environment profiles: identity, safety posture, build-target gating, and which services are
    /// bound in each.
    /// </summary>
    /// <remarks>
    /// An environment is identity plus safety posture, not an address — origins live on each service's
    /// binding. That is what lets one endpoint template be used across every environment, so this view
    /// shows bindings as a read-out and sends editing to the service that owns them.
    /// </remarks>
    internal sealed class NetworkEnvironmentsView : VisualElement
    {
        private readonly NetworkHubSession _session;
        private readonly NetworkHubUi.Split _split;

        /// <summary>Builds the view.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkEnvironmentsView(NetworkHubSession session)
        {
            _session = session;
            AddToClassList("molca-network__view");
            style.flexGrow = 1;

            _split = new NetworkHubUi.Split();
            Add(_split);

            Rebuild();
        }

        private string Selected
        {
            get => _session.SelectionFor(NetworkHubViews.Environments);
            set
            {
                _session.SetSelection(NetworkHubViews.Environments, value);
                Rebuild();
            }
        }

        private void Rebuild()
        {
            BuildMaster();
            BuildDetail();
        }

        private void BuildMaster()
        {
            _split.Master.Clear();

            var header = new VisualElement();
            header.AddToClassList("molca-network__master-header");
            header.Add(new Label($"{_session.Catalog.Environments.Count} environment(s)"));
            header.Add(MolcaButtons.Mini("Add", AddEnvironment));
            _split.Master.Add(header);

            var list = new ScrollView();
            list.style.flexGrow = 1;
            _split.Master.Add(list);

            if (_session.Catalog.Environments.Count == 0)
            {
                list.Add(NetworkHubUi.Note("No environments yet. Add one to start binding services."));
                return;
            }

            foreach (var environment in _session.Catalog.Environments)
            {
                if (environment == null) continue;

                bool isDefault = string.Equals(
                    environment.Id, _session.Catalog.DefaultEnvironmentId, StringComparison.Ordinal);

                var badges = new System.Collections.Generic.List<VisualElement>
                {
                    NetworkHubUi.Badge(environment.Classification.ToString()),
                };

                if (isDefault)
                    badges.Add(NetworkHubUi.Badge("default", MolcaStatusKind.Ok));

                if (environment.EnabledBuildTargets.Count > 0)
                    badges.Add(NetworkHubUi.Badge($"{environment.EnabledBuildTargets.Count} target(s)"));

                list.Add(NetworkHubUi.ListRow(
                    environment.DisplayName,
                    environment.Id,
                    NetworkHubUi.StatusOf(_session.Validation, NetworkValidationEntityKind.Environment, environment.Id),
                    "Validation status for this environment.",
                    string.Equals(environment.Id, Selected, StringComparison.Ordinal),
                    () => Selected = environment.Id,
                    badges.ToArray()));
            }
        }

        private void BuildDetail()
        {
            _split.Detail.Clear();

            var environment = _session.Catalog.FindEnvironment(Selected);
            if (environment == null)
            {
                _split.Detail.Add(NetworkHubUi.Note("Select an environment."));
                return;
            }

            _split.Detail.Add(BuildIdentity(environment));
            _split.Detail.Add(BuildSafety(environment));
            _split.Detail.Add(BuildBindings(environment));
            _split.Detail.Add(BuildFindings(environment));
        }

        private VisualElement BuildIdentity(NetworkEnvironmentProfile environment)
        {
            bool isDefault = string.Equals(
                environment.Id, _session.Catalog.DefaultEnvironmentId, StringComparison.Ordinal);

            string id = environment.Id;
            var card = NetworkHubUi.Card(environment.DisplayName, id);

            card.Body.Add(NetworkHubFields.EditText(
                "Display name",
                environment.DisplayName,
                value => _session.Apply(_session.Editing.SetEnvironmentDisplayName(id, value)),
                "Shown throughout the workspace. Nothing references an environment by display name, so this " +
                "is safe to change at any time."));

            // The ID stays a read-out with a refactor action beside it: service bindings name it, so an
            // inline text field would let a keystroke silently unbind every service.
            card.Body.Add(NetworkHubUi.Field("Stable ID", id,
                "Referenced by every service binding. Changing it is a refactor, not an edit — use Rename ID."));

            card.Body.Add(NetworkHubFields.EditEnum(
                "Classification",
                environment.Classification,
                value => _session.Apply(_session.Editing.SetEnvironmentClassification(id, value)),
                "Production enforces safety rules that cannot be relaxed per profile."));

            card.Body.Add(NetworkHubUi.Field("Runtime default", isDefault ? "Yes" : "No"));

            card.Body.Add(NetworkHubFields.EditReference(
                "Policy override",
                environment.PolicyProfileId,
                _session.PolicyProfileIds(),
                value => _session.Apply(_session.Editing.SetEnvironmentPolicyProfile(id, value)),
                NetworkHubFields.InheritLabel,
                "Applied to every service in this environment unless the service or endpoint overrides it."));

            card.Body.Add(NetworkHubFields.EditTextArea(
                "Notes",
                environment.Notes,
                value => _session.Apply(_session.Editing.SetEnvironmentNotes(id, value))));

            var actions = NetworkHubUi.Actions(
                isDefault ? null : MolcaButtons.Mini("Make default", () => MakeDefault(id)),
                MolcaButtons.Mini("Preview under this", () => _session.PreviewEnvironmentId = id),
                MolcaButtons.Mini("Rename ID…", () => RenameId(id)),
                MolcaButtons.Mini("Delete…", () => Delete(id)));

            card.Body.Add(actions);
            return card;
        }

        private VisualElement BuildSafety(NetworkEnvironmentProfile environment)
        {
            string id = environment.Id;
            bool forcedByProduction =
                environment.Classification == NetworkEnvironmentClassification.Production;

            var card = NetworkHubUi.Card(
                "Safety",
                null,
                environment.IsProductionSafetyEnforced ? MolcaStatusKind.Warning : MolcaStatusKind.None,
                environment.IsProductionSafetyEnforced ? "Production rules apply" : null);

            var secureTransport = NetworkHubFields.EditToggle(
                "Requires encrypted transport",
                environment.RequireSecureTransport,
                value => _session.Apply(
                    _session.Editing.SetEnvironmentRequireSecureTransport(id, value)),
                forcedByProduction
                    ? "Forced on for Production regardless of the authored value, so this control is disabled."
                    : "Every origin bound to this environment must use https or wss.");

            // Disabled rather than hidden on Production: the value still applies if the classification is
            // lowered later, and a control that vanishes reads as "this setting does not exist".
            secureTransport.SetEnabled(!forcedByProduction);
            card.Body.Add(secureTransport);

            card.Body.Add(NetworkHubUi.Field(
                "Production safety",
                environment.IsProductionSafetyEnforced ? "Enforced" : "Not enforced",
                "Mutating console sends need per-send confirmation, TLS validation cannot be relaxed, and " +
                "unencrypted origins are refused."));

            card.Body.Add(NetworkHubFields.EditStringList(
                "Build targets",
                environment.EnabledBuildTargets,
                values => _session.Apply(_session.Editing.SetEnvironmentBuildTargets(id, values)),
                "target",
                "Selectable for any build target.",
                "A build target name, for example StandaloneWindows64 or Android."));

            card.Body.Add(NetworkHubFields.EditStringList(
                "Build profiles",
                environment.EnabledBuildProfiles,
                values => _session.Apply(_session.Editing.SetEnvironmentBuildProfiles(id, values)),
                "profile",
                "Selectable for any build profile."));

            card.Body.Add(NetworkHubFields.EditStringList(
                "Labels",
                environment.Labels,
                values => _session.Apply(_session.Editing.SetEnvironmentLabels(id, values)),
                "label",
                "No labels."));

            return card;
        }

        private VisualElement BuildBindings(NetworkEnvironmentProfile environment)
        {
            var card = NetworkHubUi.Card(
                "Services here",
                "Bindings are authored on the service. This is the read-out.",
                MolcaStatusKind.None);

            int bound = 0;
            foreach (var service in _session.Catalog.Services)
            {
                if (service == null) continue;

                var binding = service.FindBinding(environment.Id);
                if (binding == null) continue;

                bound++;

                var status = !binding.Enabled ? MolcaStatusKind.Warning
                    : string.IsNullOrWhiteSpace(binding.HttpOrigin) ? MolcaStatusKind.Error
                    : MolcaStatusKind.Ok;

                card.Body.Add(NetworkHubUi.ListRow(
                    service.Id,
                    !binding.Enabled ? "disabled here"
                        : string.IsNullOrWhiteSpace(binding.HttpOrigin) ? "no HTTP origin"
                        : binding.HttpOrigin,
                    status,
                    "Binding state in this environment.",
                    selected: false,
                    onClick: () => _session.Navigate(
                        NetworkHubNavigationTarget.Service(service.Id, environment.Id))));
            }

            int missing = _session.Catalog.Services.Count - bound;
            if (missing > 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    $"{missing} service(s) have no binding here. That is meaningful: a request to one of " +
                    "them in this environment reports a route-resolution error rather than falling back " +
                    "to another environment's origin."));
            }

            if (bound == 0 && missing == 0)
                card.Body.Add(NetworkHubUi.Note("This catalog has no services yet."));

            return card;
        }

        private VisualElement BuildFindings(NetworkEnvironmentProfile environment)
        {
            var findings = NetworkHubUi.FindingsFor(
                _session.Validation, NetworkValidationEntityKind.Environment, environment.Id);

            var card = NetworkHubUi.Card(
                "Validation",
                null,
                findings.Count == 0 ? MolcaStatusKind.Ok : NetworkHubUi.StatusOf(findings[0].Severity),
                findings.Count == 0 ? "Clear" : $"{findings.Count} finding(s)");

            if (findings.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note("Nothing reported for this environment."));
                return card;
            }

            foreach (var finding in findings)
                card.Body.Add(NetworkHubUi.FindingRow(finding));

            return card;
        }

        // ---- Authoring actions

        private void AddEnvironment()
        {
            string id = NetworkIds.MakeUnique(
                "environment", candidate => _session.Catalog.FindEnvironment(candidate) != null);

            var result = _session.Editing.CreateEnvironment(id);

            // Selection is recorded before the reload, because the reload is what rebuilds the views that
            // read it.
            if (result.Success)
                _session.SetSelection(NetworkHubViews.Environments, result.ResultId);

            _session.Apply(result);
        }

        private void MakeDefault(string environmentId) =>
            _session.Apply(_session.Editing.SetDefaultEnvironment(environmentId));

        /// <summary>
        /// Renames an environment's stable ID, rewriting every reference to it.
        /// </summary>
        /// <remarks>
        /// Confirmed rather than inline-edited. An ID is a primary key that service bindings name; treating
        /// it as an editable text field would let a keystroke silently unbind every service.
        /// </remarks>
        private void RenameId(string oldId)
        {
            string newId = NetworkHubPrompt.ForId(
                "Rename environment ID",
                $"'{oldId}' is referenced by every service binding that names it. Renaming rewrites those " +
                "references in one Undo step.",
                oldId);

            if (string.IsNullOrEmpty(newId) || string.Equals(newId, oldId, StringComparison.Ordinal))
                return;

            var result = _session.Editing.RenameEnvironmentId(oldId, newId);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Environments, newId);

            _session.Apply(result);
        }

        private void Delete(string environmentId)
        {
            if (!EditorUtility.DisplayDialog(
                    "Delete environment?",
                    $"'{environmentId}' and every service binding that names it will be removed.\n\n" +
                    "This is one Undo step.",
                    "Delete", "Cancel"))
            {
                return;
            }

            var result = _session.Editing.DeleteEnvironment(environmentId);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Environments, string.Empty);

            _session.Apply(result);
        }
    }
}
