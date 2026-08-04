using System;
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
    /// Credential profile metadata and provider readiness. Never a credential value.
    /// </summary>
    /// <remarks>
    /// The strictest rule in the workspace: this view must not reveal, serialize, copy, or retain a secret
    /// (plan §7.9). It reads a profile's <em>declaration</em> — which provider supplies the value, which
    /// services and hosts may use it — and nothing else. It never calls a credential provider, so there is
    /// no value in the process for it to leak in the first place.
    /// <para>
    /// The scope read-out is the useful part. An unscoped profile denies everything, which looks like a
    /// misconfiguration at runtime and is very hard to diagnose without seeing the scope written out.
    /// </para>
    /// </remarks>
    internal sealed class NetworkCredentialsView : VisualElement
    {
        private readonly NetworkHubSession _session;
        private readonly NetworkHubUi.Split _split;

        /// <summary>Builds the view.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkCredentialsView(NetworkHubSession session)
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
            get => _session.SelectionFor(NetworkHubViews.Credentials);
            set
            {
                _session.SetSelection(NetworkHubViews.Credentials, value);
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
            header.Add(new Label($"{_session.Catalog.CredentialProfiles.Count} profile(s)"));
            header.Add(MolcaButtons.Mini("Add", AddProfile));
            _split.Master.Add(header);

            var list = new ScrollView();
            list.style.flexGrow = 1;
            _split.Master.Add(list);

            if (_session.Catalog.CredentialProfiles.Count == 0)
            {
                list.Add(NetworkHubUi.Note(
                    "No credential profiles. Every service sends anonymously, which is the safe default."));
                return;
            }

            foreach (var profile in _session.Catalog.CredentialProfiles)
            {
                if (profile == null) continue;

                var badges = new List<VisualElement> { NetworkHubUi.Badge(profile.ProviderKind.ToString()) };

                if (profile.UsableFromRequestConsole)
                    badges.Add(NetworkHubUi.Badge("console"));

                list.Add(NetworkHubUi.ListRow(
                    profile.DisplayName,
                    profile.Id,
                    ReadinessOf(profile),
                    ReadinessTooltip(profile),
                    string.Equals(profile.Id, Selected, StringComparison.Ordinal),
                    () => Selected = profile.Id,
                    badges.ToArray()));
            }
        }

        /// <summary>
        /// Whether this profile could attach a credential to anything.
        /// </summary>
        /// <remarks>
        /// Deliberately not "is a token present" — answering that would require acquiring one, and this
        /// view never does. It reports the declaration, which is what an author can actually fix here.
        /// </remarks>
        private static MolcaStatusKind ReadinessOf(NetworkCredentialProfile profile)
        {
            if (profile.IsAnonymous) return MolcaStatusKind.Idle;

            bool scoped = profile.AllowedServiceIds.Count > 0 && profile.AllowedHostPatterns.Count > 0;
            return scoped ? MolcaStatusKind.Ok : MolcaStatusKind.Warning;
        }

        private static string ReadinessTooltip(NetworkCredentialProfile profile)
        {
            if (profile.IsAnonymous)
                return "No provider kind, so this profile never attaches a credential.";

            if (profile.AllowedServiceIds.Count == 0)
                return "No service is scoped, so this profile is denied everywhere.";

            if (profile.AllowedHostPatterns.Count == 0)
                return "No host pattern is scoped, so this profile is denied everywhere.";

            return "Declared and scoped.";
        }

        private void BuildDetail()
        {
            _split.Detail.Clear();

            var profile = _session.Catalog.FindCredentialProfile(Selected);
            if (profile == null)
            {
                _split.Detail.Add(NetworkHubUi.Note("Select a credential profile."));
                return;
            }

            _split.Detail.Add(BuildIdentity(profile));
            _split.Detail.Add(BuildScope(profile));
            _split.Detail.Add(BuildUsage(profile));
            _split.Detail.Add(BuildFindings(profile));
        }

        private VisualElement BuildIdentity(NetworkCredentialProfile profile)
        {
            var card = NetworkHubUi.Card(
                profile.DisplayName,
                profile.Id,
                ReadinessOf(profile),
                profile.IsAnonymous ? "No provider" : ReadinessTooltip(profile));

            string id = profile.Id;
            var editing = _session.Editing;

            card.Body.Add(NetworkHubFields.EditText(
                "Display name",
                profile.DisplayName,
                value => _session.Apply(editing.SetCredentialDisplayName(id, value))));

            card.Body.Add(NetworkHubUi.Field("Stable ID", id,
                "Named by every service that sends with this profile."));

            card.Body.Add(NetworkHubFields.EditEnum(
                "Provider kind",
                profile.ProviderKind,
                value => _session.Apply(editing.SetCredentialProvider(id, value, profile.ProviderKey)),
                "Which registered INetworkCredentialProvider supplies the value at send time."));

            // A key names a secret; it is not one. The editing service refuses a value that looks like a
            // token pasted in here, which is the mistake this field invites.
            card.Body.Add(NetworkHubFields.EditChoice(
                "Provider key",
                profile.ProviderKey,
                NetworkHubChoices.EnvironmentVariableNames(),
                value => _session.Apply(editing.SetCredentialProvider(id, profile.ProviderKind, value)),
                NetworkHubFields.NoneLabel,
                new NetworkHubFields.ChoiceCreation(
                    "New key…",
                    "Name a provider key",
                    "This machine exports no variable by that name. Expected when the key is set on a "
                    + "build agent or a teammate's machine — it is stored as written and reads as not "
                    + "found here until something exports it.",
                    "Key",
                    IsPlausibleProviderKey),
                "The lookup key handed to the provider, offered from this machine's environment. A key is " +
                "not a secret; the value it names never enters this asset."));

            card.Body.Add(NetworkHubFields.EditText(
                "Audience",
                profile.Audience,
                value => _session.Apply(editing.SetCredentialAudience(id, value, profile.Scopes)),
                "The token audience requested from the provider."));

            card.Body.Add(NetworkHubFields.EditStringList(
                "Scopes",
                profile.Scopes,
                values => _session.Apply(editing.SetCredentialAudience(id, profile.Audience, values)),
                "scope",
                "No scopes requested."));

            card.Body.Add(NetworkHubUi.Heading("Transport"));
            card.Body.Add(NetworkHubFields.EditChoice(
                "Header",
                profile.HeaderName,
                NetworkHubChoices.HeaderNames(_session.Catalog),
                value => _session.Apply(
                    editing.SetCredentialTransport(id, value, profile.Scheme, profile.RefreshMode)),
                "(default: Authorization)",
                new NetworkHubFields.ChoiceCreation(
                    "New header…",
                    "Name a header",
                    "A header name outside the conventional set. It must be a valid HTTP field name; the "
                    + "service writes the resolved value to it verbatim.",
                    "Header",
                    IsPlausibleHeaderName),
                "The header the resolved value is written to."));
            // Not trimmed: the trailing space in "Bearer " separates the scheme from the value, so trimming
            // it in the control would break the header before the service ever saw it.
            card.Body.Add(NetworkHubFields.EditText(
                "Scheme prefix",
                profile.Scheme,
                value => _session.Apply(
                    editing.SetCredentialTransport(id, profile.HeaderName, value, profile.RefreshMode)),
                "Placed before the value. The trailing space in 'Bearer ' is significant and is preserved.",
                "Bearer ",
                trim: false));
            card.Body.Add(NetworkHubFields.EditEnum(
                "Refresh",
                profile.RefreshMode,
                value => _session.Apply(
                    editing.SetCredentialTransport(id, profile.HeaderName, profile.Scheme, value))));

            card.Body.Add(NetworkHubFields.EditToggle(
                "Usable from console",
                profile.UsableFromRequestConsole,
                value => _session.Apply(editing.SetCredentialConsoleUse(id, value)),
                "Opt-in per profile. The request console sends against whichever environment is previewed, " +
                "so a production credential is usable there only when someone said so deliberately."));

            card.Body.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Delete…", () => Delete(id))));

            card.Body.Add(NetworkHubUi.Note(
                "No credential value is stored in this asset, read by this view, or written to preferences, " +
                "request history, or an exported diagnostic. Every field here names where a value comes " +
                "from, never the value."));

            return card;
        }

        /// <summary>
        /// The scope read-out — which services and hosts may use this profile.
        /// </summary>
        private VisualElement BuildScope(NetworkCredentialProfile profile)
        {
            bool denied = profile.AllowedServiceIds.Count == 0 || profile.AllowedHostPatterns.Count == 0;

            var card = NetworkHubUi.Card(
                "Scope",
                "Empty denies everything. It never means 'anywhere'.",
                denied ? MolcaStatusKind.Warning : MolcaStatusKind.Ok,
                denied ? "Denied everywhere" : "Scoped");

            string id = profile.Id;
            var editing = _session.Editing;

            // Services are picked from the catalog rather than typed: an entry naming no real service
            // matches nothing, and a scope that silently matches nothing sends requests out anonymous.
            card.Body.Add(NetworkHubUi.Heading("Services"));
            foreach (var service in _session.Catalog.Services)
            {
                if (service == null || string.IsNullOrEmpty(service.Id)) continue;

                string serviceId = service.Id;
                bool allowed = profile.AllowsService(serviceId);

                card.Body.Add(NetworkHubFields.EditToggle(
                    serviceId,
                    allowed,
                    next => ToggleAllowedService(profile, serviceId, next),
                    allowed
                        ? "This service may use the profile."
                        : "This service may not use the profile."));
            }

            if (_session.Catalog.Services.Count == 0)
                card.Body.Add(NetworkHubUi.Note("This catalog has no services to scope to yet."));
            else if (profile.AllowedServiceIds.Count == 0)
                card.Body.Add(NetworkHubUi.Note("No service may use this profile, so it never attaches."));

            // A scope entry naming a service that no longer exists stays visible: it matches nothing, and
            // hiding it would make an over-broad-looking scope read as correct.
            foreach (string serviceId in profile.AllowedServiceIds)
            {
                if (_session.Catalog.FindService(serviceId) != null) continue;

                string orphan = serviceId;
                var row = NetworkHubUi.ListRow(
                    orphan,
                    "no such service in this catalog",
                    MolcaStatusKind.Warning,
                    "This scope entry matches nothing.",
                    selected: false,
                    onClick: () => ToggleAllowedService(profile, orphan, false));
                card.Body.Add(row);
            }

            card.Body.Add(NetworkHubFields.EditStringList(
                "Hosts",
                profile.AllowedHostPatterns,
                values => _session.Apply(editing.SetCredentialAllowedHosts(id, values)),
                "pattern",
                "No host may receive this credential, so it never attaches.",
                "An exact host, or a single leading '*.' covering at least two labels."));

            foreach (string pattern in profile.AllowedHostPatterns)
            {
                if (NetworkHostRule.TryNormalizePattern(pattern, out _, out string error)) continue;

                card.Body.Add(NetworkHubUi.Field("invalid", $"{pattern} — {error}"));
            }

            if (profile.AllowedHostPatterns.Count > 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "Checked again after every redirect, against the host the request is actually about to " +
                    "reach. That is what stops a token following a 302 off-domain."));
            }

            return card;
        }

        /// <summary>
        /// Adds or removes one service from a profile's scope, committing the whole list.
        /// </summary>
        /// <remarks>
        /// The whole list is committed because the service validates it as a set — that is also what lets
        /// removing an orphaned entry work, since the remaining entries are all still valid.
        /// </remarks>
        private void ToggleAllowedService(NetworkCredentialProfile profile, string serviceId, bool allowed)
        {
            var next = new List<string>();
            foreach (string existing in profile.AllowedServiceIds)
            {
                if (string.Equals(existing, serviceId, StringComparison.Ordinal)) continue;

                // An orphaned entry elsewhere in the list would fail the service's existence check and
                // reject the whole edit, so it is dropped rather than carried through.
                if (_session.Catalog.FindService(existing) != null)
                    next.Add(existing);
            }

            if (allowed)
                next.Add(serviceId);

            _session.Apply(_session.Editing.SetCredentialAllowedServices(profile.Id, next));
        }

        /// <summary>Which services actually name this profile, and whether the scope agrees.</summary>
        private VisualElement BuildUsage(NetworkCredentialProfile profile)
        {
            var users = new List<NetworkServiceDefinition>();
            foreach (var service in _session.Catalog.Services)
            {
                if (service != null &&
                    string.Equals(service.CredentialProfileId, profile.Id, StringComparison.Ordinal))
                {
                    users.Add(service);
                }
            }

            var card = NetworkHubUi.Card(
                "Used by",
                null,
                users.Count == 0 ? MolcaStatusKind.Idle : MolcaStatusKind.None,
                users.Count == 0 ? "Unused" : $"{users.Count} service(s)");

            if (users.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "No service names this profile, so it is authored but inert. Set it on a service's " +
                    "credential profile field to use it."));
                return card;
            }

            foreach (var service in users)
            {
                bool inScope = profile.AllowsService(service.Id);
                card.Body.Add(NetworkHubUi.ListRow(
                    service.Id,
                    inScope ? null : "uses this profile, but the profile's scope excludes it",
                    inScope ? MolcaStatusKind.Ok : MolcaStatusKind.Error,
                    inScope
                        ? "This service may use the profile."
                        : "The service names the profile but is not in its allowed services, so requests " +
                          "go out anonymous.",
                    selected: false,
                    onClick: () => _session.Navigate(NetworkHubNavigationTarget.Service(service.Id))));
            }

            return card;
        }

        private VisualElement BuildFindings(NetworkCredentialProfile profile)
        {
            var findings = NetworkHubUi.FindingsFor(
                _session.Validation, NetworkValidationEntityKind.CredentialProfile, profile.Id);

            var card = NetworkHubUi.Card(
                "Validation",
                null,
                findings.Count == 0 ? MolcaStatusKind.Ok : NetworkHubUi.StatusOf(findings[0].Severity),
                findings.Count == 0 ? "Clear" : $"{findings.Count} finding(s)");

            if (findings.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note("Nothing reported for this profile."));
                return card;
            }

            foreach (var finding in findings)
                card.Body.Add(NetworkHubUi.FindingRow(finding));

            return card;
        }

        private void AddProfile()
        {
            string id = NetworkIds.MakeUnique(
                "credential", candidate => _session.Catalog.FindCredentialProfile(candidate) != null);

            var result = _session.Editing.CreateCredentialProfile(id);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Credentials, result.ResultId);

            _session.Apply(result);
        }

        private void Delete(string credentialProfileId)
        {
            if (!EditorUtility.DisplayDialog(
                    "Delete credential profile?",
                    $"'{credentialProfileId}' will be removed, and every service that names it will send " +
                    "anonymously.\n\nThis is one Undo step. No credential value is affected — this asset " +
                    "never held one.",
                    "Delete", "Cancel"))
            {
                return;
            }

            var result = _session.Editing.DeleteCredentialProfile(credentialProfileId);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Credentials, string.Empty);

            _session.Apply(result);
        }

        /// <summary>
        /// Rejects a provider key that could not name an environment variable.
        /// </summary>
        /// <param name="candidate">The proposed key.</param>
        /// <returns>Null when plausible, otherwise why not.</returns>
        /// <remarks>
        /// Shape only — that the variable is absent here is the whole reason the author reached for the
        /// create action, so its absence cannot also be the refusal.
        /// </remarks>
        private static string IsPlausibleProviderKey(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return "Enter a key.";

            string trimmed = candidate.Trim();
            if (trimmed.IndexOf('=') >= 0)
                return "An environment variable name cannot contain '='.";
            if (trimmed.IndexOf(' ') >= 0)
                return "An environment variable name cannot contain spaces.";

            return null;
        }

        /// <summary>
        /// Rejects text that is not a valid HTTP field name.
        /// </summary>
        /// <param name="candidate">The proposed header name.</param>
        /// <returns>Null when valid, otherwise why not.</returns>
        /// <remarks>
        /// The token grammar from RFC 7230 §3.2.6. Enforced rather than merely advised because an invalid
        /// field name does not fail here — it fails when the request is assembled, far from this field.
        /// </remarks>
        private static string IsPlausibleHeaderName(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return "Enter a header name.";

            const string Separators = "()<>@,;:\\\"/[]?={} \t";

            string trimmed = candidate.Trim();
            foreach (char c in trimmed)
            {
                if (c <= 31 || c >= 127 || Separators.IndexOf(c) >= 0)
                    return $"'{c}' cannot appear in a header name.";
            }

            return null;
        }
    }
}
