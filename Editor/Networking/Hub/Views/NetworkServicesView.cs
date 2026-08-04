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
            string id = service.Id;
            var card = NetworkHubUi.Card(service.DisplayName, id);

            card.Body.Add(NetworkHubFields.EditText(
                "Display name",
                service.DisplayName,
                value => _session.Apply(_session.Editing.SetServiceDisplayName(id, value)),
                "Shown throughout the workspace. Nothing references a service by display name."));

            // Read-out with a refactor action beside it: endpoints, credential scopes, and every route key
            // name this ID, so it is not an inline edit.
            card.Body.Add(NetworkHubUi.Field("Stable ID", id,
                "Named by endpoints, credential scopes, and every route key. Changing it is a refactor."));

            card.Body.Add(NetworkHubFields.EditFlags(
                "Protocols",
                service.Protocols,
                value => _session.Apply(_session.Editing.SetServiceProtocols(id, value)),
                "A binding must supply an origin for each declared protocol. Declaring one does not fill " +
                "its origin in — the binding grid below grows a field for it."));

            card.Body.Add(NetworkHubFields.EditReference(
                "Policy profile",
                service.PolicyProfileId,
                _session.PolicyProfileIds(),
                value => _session.Apply(_session.Editing.SetServicePolicyProfile(id, value)),
                NetworkHubFields.InheritLabel,
                "Empty inherits the environment's profile, then the catalog default, then library defaults."));

            card.Body.Add(NetworkHubFields.EditReference(
                "Credential profile",
                service.CredentialProfileId,
                _session.CredentialProfileIds(),
                value => _session.Apply(_session.Editing.SetServiceCredentialProfile(id, value)),
                NetworkHubFields.NoneLabel,
                "Empty means this service sends anonymously. The profile's own scope must also name this " +
                "service for a credential to attach."));

            card.Body.Add(NetworkHubFields.EditReference(
                "Health endpoint",
                service.HealthEndpointId,
                _session.EndpointIds(),
                value => _session.Apply(_session.Editing.SetServiceHealthEndpoint(id, value)),
                NetworkHubFields.NoneLabel,
                "The endpoint a health check calls."));

            // A credential arrives through the credential profile, per host, not through a header authored
            // here — the editing service refuses an auth header name for that reason.
            card.Body.Add(NetworkHubFields.EditHeaderList(
                "Default headers",
                service.DefaultHeaders,
                values => _session.Apply(_session.Editing.SetServiceDefaultHeaders(id, values)),
                "No default headers. These are non-secret by contract; a credential belongs on a credential " +
                "profile, which scopes it per host."));

            card.Body.Add(NetworkHubFields.EditTextArea(
                "Owner notes",
                service.OwnerNotes,
                value => _session.Apply(_session.Editing.SetServiceOwnerNotes(id, value))));

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Rename ID…", () => RenameId(id)),
                MolcaButtons.Mini("Delete…", () => Delete(id))));

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

        /// <summary>
        /// One environment's block: its state and actions, then an origin field per protocol the service
        /// declares.
        /// </summary>
        /// <remarks>
        /// A field appears per declared protocol rather than only for HTTP, so a service that speaks
        /// WebSocket can be bound here instead of in the Inspector. Nothing is inferred between them: a
        /// <c>wss</c> origin is never derived from the HTTP one, because a project whose sockets live on a
        /// different host would silently get the wrong address.
        /// </remarks>
        private VisualElement BuildBindingRow(
            NetworkServiceDefinition service,
            NetworkEnvironmentProfile environment)
        {
            string serviceId = service.Id;
            string environmentId = environment.Id;
            var binding = service.FindBinding(environmentId);

            var block = new VisualElement();

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

            var name = new Label(environmentId);
            name.AddToClassList("molca-network__binding-environment");
            row.Add(name);

            if (binding != null)
            {
                var enabled = new Toggle { value = binding.Enabled };
                enabled.tooltip = "Disabling keeps the origin but stops the route resolving here.";
                enabled.RegisterValueChangedCallback(evt => _session.Apply(
                    _session.Editing.SetBindingEnabled(serviceId, environmentId, evt.newValue)));
                row.Add(enabled);
            }

            row.Add(MolcaButtons.Mini("Copy to…", () => CopyBinding(service, environment)));

            if (binding != null)
                row.Add(MolcaButtons.Mini("Unbind…", () => Unbind(serviceId, environmentId)));

            block.Add(row);

            AppendOriginField(block, service, environmentId, NetworkProtocols.Http, "HTTP",
                binding?.HttpOrigin,
                "Absolute HTTP origin, e.g. https://api.example.com/v1. Empty leaves it unset.");

            // The authored value, not the resolving accessor: SSE falls back to the HTTP origin, and showing
            // that here would make an inherited origin look authored and duplicate it on the next commit.
            AppendOriginField(block, service, environmentId, NetworkProtocols.ServerSentEvents, "SSE",
                binding?.AuthoredSseOrigin,
                "Server-Sent Events are delivered over HTTP, so this is an https origin, not wss. Left " +
                "empty, it follows the HTTP origin above.");

            AppendOriginField(block, service, environmentId, NetworkProtocols.WebSocket, "WebSocket",
                binding?.WebSocketOrigin,
                "Absolute WebSocket origin, e.g. wss://stream.example.com.");

            AppendOriginField(block, service, environmentId, NetworkProtocols.SocketIO, "Socket.IO",
                binding?.SocketIoOrigin,
                "Absolute Socket.IO origin. The handshake path is set separately below.");

            if (service.Protocols.HasFlag(NetworkProtocols.SocketIO))
            {
                block.Add(NetworkHubFields.EditChoice(
                    "Socket.IO path",
                    binding?.AuthoredSocketIoPath,
                    NetworkHubChoices.SocketIoPaths(_session.Catalog),
                    value => _session.Apply(
                        _session.Editing.SetBindingSocketIoPath(serviceId, environmentId, value)),
                    "(client default)",
                    new NetworkHubFields.ChoiceCreation(
                        "New path…",
                        "Name a handshake path",
                        "A path this catalog does not use yet. It must be absolute — the client appends it "
                        + "to the origin without inserting a separator.",
                        "Path",
                        IsPlausibleSocketIoPath),
                    "Handshake path, offered from what this catalog already uses. Unset follows the "
                    + "client default."));
            }

            if (binding != null)
            {
                block.Add(NetworkHubFields.EditChoice(
                    "Region",
                    binding.RegionLabel,
                    NetworkHubChoices.RegionLabels(_session.Catalog),
                    value => _session.Apply(
                        _session.Editing.SetBindingRegionLabel(serviceId, environmentId, value)),
                    NetworkHubFields.NoneLabel,
                    new NetworkHubFields.ChoiceCreation(
                        "New region…",
                        "Name a region",
                        "A label this catalog does not use yet. Nothing validates it — it is a diagnostics "
                        + "label and never affects routing — so the only thing worth getting right is that "
                        + "it matches how the rest of the catalog spells the same region.",
                        "Region"),
                    "Region label, offered from what this catalog already uses. Diagnostics only; it "
                    + "never affects routing."));
            }

            return block;
        }

        /// <summary>
        /// Adds an origin field for one protocol, but only when the service declares it.
        /// </summary>
        /// <remarks>
        /// Gated on the declaration so the grid stays as small as the service is simple: an HTTP-only
        /// service shows one field, not four empty ones.
        /// </remarks>
        private void AppendOriginField(
            VisualElement block,
            NetworkServiceDefinition service,
            string environmentId,
            NetworkProtocols protocol,
            string label,
            string origin,
            string tooltip)
        {
            if (!service.Protocols.HasFlag(protocol))
                return;

            string serviceId = service.Id;

            block.Add(NetworkHubFields.EditText(
                label,
                origin,
                value => _session.Apply(
                    _session.Editing.SetBindingOrigin(serviceId, environmentId, protocol, value)),
                tooltip,
                "not bound"));
        }

        private void Unbind(string serviceId, string environmentId)
        {
            if (!EditorUtility.DisplayDialog(
                    "Remove binding?",
                    $"'{serviceId}' will have no address in '{environmentId}'. Requests there will report a " +
                    "route-resolution error rather than falling back to another environment.\n\n" +
                    "To keep the origin and just turn it off, use the enabled toggle instead.",
                    "Remove", "Cancel"))
            {
                return;
            }

            _session.Apply(_session.Editing.RemoveBinding(serviceId, environmentId));
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
            string serviceId = service.Id;
            string sourceId = source.Id;

            if (service.FindBinding(sourceId) == null)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Network] '{serviceId}' has no binding in '{sourceId}' to copy.");
                return;
            }

            var menu = new GenericMenu();
            foreach (var target in _session.Catalog.Environments)
            {
                if (target == null || string.Equals(target.Id, sourceId, StringComparison.Ordinal))
                    continue;

                string targetId = target.Id;
                menu.AddItem(new GUIContent(targetId), false, () => _session.Apply(
                    _session.Editing.CopyBinding(serviceId, sourceId, targetId)));
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

            // The same guard the endpoint preview needs. An authored service always has an ID, but one that
            // arrived from a legacy migration or a hand-edited asset need not, and a preview that throws on
            // it would hide the very fields that repair it.
            if (string.IsNullOrEmpty(service.Id))
            {
                card.SetStatus(MolcaStatusKind.Warning, "No service ID");
                card.Body.Add(NetworkHubUi.Note(
                    "This service has no ID, so nothing can route to it. Give it one above."));
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

            // The derived hosts are shown as a read-out, because they are not authored — they come from the
            // bound origins. Authoring a pattern is what takes over from the derivation, so the editable
            // list holds the authored patterns only.
            if (derived)
            {
                if (hosts.Count == 0)
                {
                    card.Body.Add(NetworkHubUi.Note(
                        "No hosts are allowed, because nothing is bound and no patterns are authored. This " +
                        "never means 'any host' — if nothing is bound, nothing is allowed."));
                }
                else
                {
                    foreach (string host in hosts)
                        card.Body.Add(NetworkHubUi.Field("from binding", host));
                }
            }

            string id = service.Id;
            card.Body.Add(NetworkHubFields.EditStringList(
                "Authored patterns",
                service.AllowedHostPatterns,
                values => _session.Apply(_session.Editing.SetServiceAllowedHostPatterns(id, values)),
                "pattern",
                "None authored, so the allow-list is derived from the bound origins above.",
                "An exact host, or a single leading '*.' covering at least two labels."));

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

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Services, result.ResultId);

            _session.Apply(result);
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

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Services, newId);

            _session.Apply(result);
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

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Services, string.Empty);

            _session.Apply(result);
        }

        /// <summary>
        /// Rejects a handshake path that is not absolute.
        /// </summary>
        /// <param name="candidate">The proposed path.</param>
        /// <returns>Null when valid, otherwise why not.</returns>
        /// <remarks>
        /// The leading slash is not cosmetic: the client concatenates origin and path without inserting a
        /// separator, so <c>socket.io/</c> produces a URL one character different from the one intended and
        /// fails at handshake time rather than here.
        /// </remarks>
        private static string IsPlausibleSocketIoPath(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return "Enter a path.";

            string trimmed = candidate.Trim();
            if (trimmed[0] != '/')
                return "A handshake path must start with '/'.";
            if (trimmed.IndexOf(' ') >= 0)
                return "A path cannot contain spaces.";

            return null;
        }
    }
}
