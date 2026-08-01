using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Validation;
using Molca.Networking.Configuration;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub.Views
{
    /// <summary>
    /// Authors service definitions and — the point of the view — the per-environment binding grid.
    /// </summary>
    /// <remarks>
    /// The binding grid is the central multi-environment authoring surface (plan §7.6). A missing binding
    /// is shown as missing, never filled in from a neighbouring environment: silent fallback is how a
    /// staging build ends up talking to production. Copying a binding across is an explicit action.
    /// </remarks>
    internal sealed class NetworkServicesView : VisualElement
    {
        private readonly NetworkHubSession _session;
        private readonly NetworkHubUi.Split _split;

        /// <summary>Builds the view.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkServicesView(NetworkHubSession session)
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
            get => _session.SelectionFor(NetworkHubViews.Services);
            set
            {
                _session.SetSelection(NetworkHubViews.Services, value);
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
            header.Add(new Label($"{_session.Catalog.Services.Count} service(s)"));
            header.Add(MolcaButtons.Mini("Add", AddService));
            _split.Master.Add(header);

            var list = new ScrollView();
            list.style.flexGrow = 1;
            _split.Master.Add(list);

            if (_session.Catalog.Services.Count == 0)
            {
                list.Add(NetworkHubUi.Note("No services yet. A service is a logical backend — identity, " +
                                           "content, telemetry — not a URL."));
                return;
            }

            string preview = _session.PreviewEnvironmentId;

            foreach (var service in _session.Catalog.Services)
            {
                if (service == null) continue;

                var badges = new List<VisualElement> { NetworkHubUi.Badge(service.Protocols.ToString()) };

                if (!string.IsNullOrEmpty(service.CredentialProfileId))
                    badges.Add(NetworkHubUi.Badge("auth"));

                if (!string.IsNullOrEmpty(service.PolicyProfileId))
                    badges.Add(NetworkHubUi.Badge("policy"));

                var binding = service.FindBinding(preview);
                badges.Add(binding == null
                    ? NetworkHubUi.Badge("unbound", MolcaStatusKind.Idle)
                    : NetworkHubUi.Badge(preview, MolcaStatusKind.Ok));

                list.Add(NetworkHubUi.ListRow(
                    service.DisplayName,
                    service.Id,
                    NetworkHubUi.StatusOf(_session.Validation, NetworkValidationEntityKind.Service, service.Id),
                    "Validation status for this service.",
                    string.Equals(service.Id, Selected, StringComparison.Ordinal),
                    () => Selected = service.Id,
                    badges.ToArray()));
            }
        }

        private void BuildDetail()
        {
            _split.Detail.Clear();

            var service = _session.Catalog.FindService(Selected);
            if (service == null)
            {
                _split.Detail.Add(NetworkHubUi.Note("Select a service."));
                return;
            }

            _split.Detail.Add(BuildIdentity(service));
            _split.Detail.Add(BuildBindingGrid(service));
            _split.Detail.Add(BuildResolvedPreview(service));
            _split.Detail.Add(BuildSafety(service));
            _split.Detail.Add(BuildFindings(service));
        }

        private VisualElement BuildIdentity(NetworkServiceDefinition service)
        {
            var card = NetworkHubUi.Card(service.DisplayName, service.Id);

            card.Body.Add(NetworkHubUi.Field("Stable ID", service.Id,
                "Named by endpoints, credential scopes, and every route key. Changing it is a refactor."));
            card.Body.Add(NetworkHubUi.Field("Protocols", service.Protocols.ToString(),
                "A binding must supply an origin for each declared protocol."));
            card.Body.Add(NetworkHubUi.Field("Policy profile",
                string.IsNullOrEmpty(service.PolicyProfileId) ? null : service.PolicyProfileId,
                "Empty inherits the environment's profile, then the catalog default, then library defaults."));
            card.Body.Add(NetworkHubUi.Field("Credential profile",
                string.IsNullOrEmpty(service.CredentialProfileId) ? null : service.CredentialProfileId,
                "Empty means this service sends anonymously."));

            if (service.DefaultHeaders.Count > 0)
            {
                card.Body.Add(NetworkHubUi.Heading("Default headers"));
                foreach (var header in service.DefaultHeaders)
                {
                    if (header == null) continue;
                    // These are non-secret by contract; a credential arrives through the credential
                    // profile, per host, not through a header authored on the service.
                    card.Body.Add(NetworkHubUi.Field(header.key, header.value));
                }
            }

            if (!string.IsNullOrEmpty(service.OwnerNotes))
                card.Body.Add(NetworkHubUi.Field("Owner notes", service.OwnerNotes));

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Rename ID…", () => RenameId(service.Id)),
                MolcaButtons.Mini("Delete…", () => Delete(service.Id))));

            return card;
        }

        /// <summary>
        /// The binding grid: one row per environment, bound or explicitly not.
        /// </summary>
        private VisualElement BuildBindingGrid(NetworkServiceDefinition service)
        {
            var card = NetworkHubUi.Card(
                "Bindings",
                "One origin per environment. A missing binding is a decision, not a gap to fill in.",
                MolcaStatusKind.None);

            if (_session.Catalog.Environments.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note("Add an environment first."));
                return card;
            }

            foreach (var environment in _session.Catalog.Environments)
            {
                if (environment == null) continue;
                card.Body.Add(BuildBindingRow(service, environment));
            }

            return card;
        }

        private VisualElement BuildBindingRow(
            NetworkServiceDefinition service,
            NetworkEnvironmentProfile environment)
        {
            var binding = service.FindBinding(environment.Id);

            var row = new VisualElement();
            row.AddToClassList("molca-network__binding-row");

            var status = binding == null ? MolcaStatusKind.Idle
                : !binding.Enabled ? MolcaStatusKind.Warning
                : string.IsNullOrWhiteSpace(binding.HttpOrigin) ? MolcaStatusKind.Error
                : MolcaStatusKind.Ok;

            row.Add(NetworkHubUi.Dot(status, binding == null
                ? "Not bound here. A request to this route reports a route-resolution error."
                : !binding.Enabled ? "Bound but disabled."
                : "Bound."));

            var name = new Label(environment.Id);
            name.AddToClassList("molca-network__binding-environment");
            row.Add(name);

            var field = new TextField { value = binding?.HttpOrigin ?? string.Empty };
            field.AddToClassList("molca-network__binding-origin");
            field.tooltip = "Absolute HTTP origin, e.g. https://api.example.com/v1. Empty leaves it unset.";
            // Committed on blur/Enter rather than per keystroke: every keystroke would be its own Undo
            // entry and its own re-validation, and half-typed origins would flash as errors.
            field.RegisterCallback<BlurEvent>(_ => CommitBinding(service.Id, environment.Id, field.value));
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == UnityEngine.KeyCode.Return || evt.keyCode == UnityEngine.KeyCode.KeypadEnter)
                    CommitBinding(service.Id, environment.Id, field.value);
            });
            row.Add(field);

            row.Add(MolcaButtons.Mini("Copy to…", () => CopyBinding(service, environment)));

            return row;
        }

        private void CommitBinding(string serviceId, string environmentId, string origin)
        {
            var service = _session.Catalog.FindService(serviceId);
            string current = service?.FindBinding(environmentId)?.HttpOrigin ?? string.Empty;

            if (string.Equals(current, origin?.Trim() ?? string.Empty, StringComparison.Ordinal))
                return;

            Report(_session.Editing.SetHttpBinding(serviceId, environmentId, origin));
            _session.Reload();
        }

        /// <summary>
        /// Copies one environment's origin to another, as an explicit action.
        /// </summary>
        /// <remarks>
        /// The plan is explicit that copying a binding is a user action and never an implicit fallback
        /// (§7.6). Offering it here is what makes the absence of fallback tolerable to author.
        /// </remarks>
        private void CopyBinding(NetworkServiceDefinition service, NetworkEnvironmentProfile source)
        {
            var binding = service.FindBinding(source.Id);
            if (binding == null || string.IsNullOrWhiteSpace(binding.HttpOrigin))
            {
                UnityEngine.Debug.LogWarning(
                    $"[Network] '{service.Id}' has no origin in '{source.Id}' to copy.");
                return;
            }

            var menu = new GenericMenu();
            foreach (var target in _session.Catalog.Environments)
            {
                if (target == null || string.Equals(target.Id, source.Id, StringComparison.Ordinal))
                    continue;

                string targetId = target.Id;
                menu.AddItem(new GUIContent(targetId), false, () =>
                {
                    Report(_session.Editing.SetHttpBinding(service.Id, targetId, binding.HttpOrigin));
                    _session.Reload();
                });
            }

            if (menu.GetItemCount() == 0)
                menu.AddDisabledItem(new GUIContent("No other environment"));

            menu.ShowAsContext();
        }

        /// <summary>
        /// What this service resolves to under the preview environment, through the same resolver the
        /// runtime uses.
        /// </summary>
        /// <remarks>
        /// Deliberately the production resolver rather than a preview-only reimplementation. A second
        /// resolver is exactly how a preview and a live request drift apart (plan §14).
        /// </remarks>
        private VisualElement BuildResolvedPreview(NetworkServiceDefinition service)
        {
            string environmentId = _session.PreviewEnvironmentId;

            var card = NetworkHubUi.Card(
                "Resolved preview",
                string.IsNullOrEmpty(environmentId) ? null : $"Under '{environmentId}'",
                MolcaStatusKind.None);

            if (string.IsNullOrEmpty(environmentId) || _session.Effective == null)
            {
                card.Body.Add(NetworkHubUi.Note("Choose a preview environment in the toolbar."));
                return card;
            }

            var route = _session.Effective.Resolve(
                new Molca.Networking.Routing.NetworkRouteKey(environmentId, service.Id));

            card.SetStatus(
                route.Resolves ? MolcaStatusKind.Ok : MolcaStatusKind.Error,
                route.Resolves ? "Resolves" : route.FailureCategory.ToString());

            if (!route.Resolves)
            {
                card.Body.Add(NetworkHubUi.Note(route.FailureReason));
                return card;
            }

            card.Body.Add(NetworkHubUi.Field("Origin", route.Origin));
            card.Body.Add(NetworkHubUi.Field("Resolved URI", route.ResolvedUri));
            card.Body.Add(NetworkHubUi.Field(
                "Credential",
                string.IsNullOrEmpty(route.CredentialProfileId)
                    ? "anonymous"
                    : route.CredentialAppliesToHost
                        ? route.CredentialProfileId
                        : $"{route.CredentialProfileId} — withheld, out of scope for this host",
                "The profile name only. No credential value is read here."));

            card.Body.Add(NetworkHubUi.Actions(MolcaButtons.Mini(
                "Inspect policy",
                () => _session.Navigate(new NetworkHubNavigationTarget(
                    NetworkHubViews.Policies, service.PolicyProfileId, environmentId)))));

            return card;
        }

        private VisualElement BuildSafety(NetworkServiceDefinition service)
        {
            var hosts = service.ResolveAllowedHosts();
            bool derived = service.AllowedHostPatterns.Count == 0;

            var card = NetworkHubUi.Card(
                "Allowed hosts",
                derived ? "Derived from the bound origins" : "Authored",
                hosts.Count == 0 ? MolcaStatusKind.Warning : MolcaStatusKind.Ok,
                hosts.Count == 0 ? "Nothing allowed" : $"{hosts.Count} pattern(s)");

            if (hosts.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "No hosts are allowed, because nothing is bound and no patterns are authored. This " +
                    "never means 'any host' — if nothing is bound, nothing is allowed."));
                return card;
            }

            foreach (string host in hosts)
                card.Body.Add(NetworkHubUi.Field(derived ? "from binding" : "pattern", host));

            card.Body.Add(NetworkHubUi.Note(
                "Redirect targets are revalidated against this list, and a credential travels across a " +
                "redirect only when its own scope also names the new host."));

            return card;
        }

        private VisualElement BuildFindings(NetworkServiceDefinition service)
        {
            var findings = NetworkHubUi.FindingsFor(
                _session.Validation, NetworkValidationEntityKind.Service, service.Id);

            findings.AddRange(NetworkHubUi.FindingsFor(
                _session.Validation, NetworkValidationEntityKind.Binding, service.Id));

            var card = NetworkHubUi.Card(
                "Validation",
                null,
                findings.Count == 0 ? MolcaStatusKind.Ok : NetworkHubUi.StatusOf(findings[0].Severity),
                findings.Count == 0 ? "Clear" : $"{findings.Count} finding(s)");

            if (findings.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note("Nothing reported for this service."));
                return card;
            }

            foreach (var finding in findings)
                card.Body.Add(NetworkHubUi.FindingRow(finding));

            return card;
        }

        // ---- Authoring actions

        private void AddService()
        {
            string id = NetworkIds.MakeUnique(
                "service", candidate => _session.Catalog.FindService(candidate) != null);

            var result = _session.Editing.CreateService(id);
            Report(result);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Services, result.ResultId);

            _session.Reload();
        }

        private void RenameId(string oldId)
        {
            string newId = NetworkHubPrompt.ForId(
                "Rename service ID",
                $"'{oldId}' is named by endpoints, credential scopes, and every route key that targets it. " +
                "Renaming rewrites those references in one Undo step.",
                oldId);

            if (string.IsNullOrEmpty(newId) || string.Equals(newId, oldId, StringComparison.Ordinal))
                return;

            var result = _session.Editing.RenameServiceId(oldId, newId);
            Report(result);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Services, newId);

            _session.Reload();
        }

        private void Delete(string serviceId)
        {
            if (!EditorUtility.DisplayDialog(
                    "Delete service?",
                    $"'{serviceId}' and its bindings will be removed. Endpoints that name it will report a " +
                    "validation error until they are re-pointed.\n\nThis is one Undo step.",
                    "Delete", "Cancel"))
            {
                return;
            }

            var result = _session.Editing.DeleteService(serviceId);
            Report(result);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Services, string.Empty);

            _session.Reload();
        }

        private static void Report(NetworkAuthoringResult result)
        {
            if (result.Success)
                UnityEngine.Debug.Log($"[Network] {result.Message}");
            else
                UnityEngine.Debug.LogWarning($"[Network] {result.Message}");
        }
    }
}
