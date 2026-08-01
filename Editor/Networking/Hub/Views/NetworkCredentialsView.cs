using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Validation;
using Molca.Networking.Configuration;
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

            card.Body.Add(NetworkHubUi.Field("Provider kind", profile.ProviderKind.ToString(),
                "Which registered INetworkCredentialProvider supplies the value at send time."));
            card.Body.Add(NetworkHubUi.Field("Provider key",
                string.IsNullOrEmpty(profile.ProviderKey) ? null : profile.ProviderKey,
                "The lookup key handed to the provider — an environment variable name, for example. " +
                "A key is not a secret; the value it names never enters this asset."));
            card.Body.Add(NetworkHubUi.Field("Audience",
                string.IsNullOrEmpty(profile.Audience) ? null : profile.Audience));
            card.Body.Add(NetworkHubUi.Field("Scopes",
                profile.Scopes.Count == 0 ? null : string.Join(", ", profile.Scopes)));

            card.Body.Add(NetworkHubUi.Heading("Transport"));
            card.Body.Add(NetworkHubUi.Field("Header", profile.HeaderName));
            card.Body.Add(NetworkHubUi.Field("Scheme prefix",
                string.IsNullOrEmpty(profile.Scheme) ? "(none)" : profile.Scheme));
            card.Body.Add(NetworkHubUi.Field("Refresh", profile.RefreshMode.ToString()));

            card.Body.Add(NetworkHubUi.Note(
                "No credential value is stored in this asset, read by this view, or written to preferences, " +
                "request history, or an exported diagnostic."));

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

            card.Body.Add(NetworkHubUi.Heading("Services"));
            if (profile.AllowedServiceIds.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "No service may use this profile, so it never attaches."));
            }
            else
            {
                foreach (string serviceId in profile.AllowedServiceIds)
                {
                    bool exists = _session.Catalog.FindService(serviceId) != null;
                    card.Body.Add(NetworkHubUi.ListRow(
                        serviceId,
                        exists ? null : "no such service in this catalog",
                        exists ? MolcaStatusKind.Ok : MolcaStatusKind.Warning,
                        exists ? "Scoped to this service." : "This scope entry matches nothing.",
                        selected: false,
                        onClick: () => _session.Navigate(NetworkHubNavigationTarget.Service(serviceId))));
                }
            }

            card.Body.Add(NetworkHubUi.Heading("Hosts"));
            if (profile.AllowedHostPatterns.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "No host may receive this credential, so it never attaches."));
            }
            else
            {
                foreach (string pattern in profile.AllowedHostPatterns)
                {
                    bool valid = NetworkHostRule.TryNormalizePattern(pattern, out _, out string error);
                    card.Body.Add(NetworkHubUi.Field(
                        valid ? "pattern" : "invalid",
                        valid ? pattern : $"{pattern} — {error}",
                        "An exact host, or a single leading '*.' covering at least two labels."));
                }

                card.Body.Add(NetworkHubUi.Note(
                    "Checked again after every redirect, against the host the request is actually about to " +
                    "reach. That is what stops a token following a 302 off-domain."));
            }

            return card;
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
            {
                UnityEngine.Debug.Log($"[Network] {result.Message}");
                _session.SetSelection(NetworkHubViews.Credentials, result.ResultId);
            }
            else
            {
                UnityEngine.Debug.LogWarning($"[Network] {result.Message}");
            }

            _session.Reload();
        }
    }
}
