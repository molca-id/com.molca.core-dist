using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Validation;
using Molca.Networking.Configuration;
using Molca.Networking.Routing;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub.Views
{
    /// <summary>
    /// Named policy profiles, and the effective-policy inspector that shows which layer supplied each
    /// value.
    /// </summary>
    /// <remarks>
    /// The inspector is the point of the view. A policy value that just says "30s" is unactionable; a
    /// value that says "30s, from the Service layer" tells you exactly which asset to edit. It resolves
    /// through the production <c>NetworkPolicyResolver</c>, so the precedence shown here is the
    /// precedence a request obeys.
    /// <para>
    /// Security-restricted fields resolve tighten-only. Where a layer tried to weaken one, the clamp is
    /// reported rather than silently applied — a preview that hid the clamp would be worse than none.
    /// </para>
    /// </remarks>
    internal sealed class NetworkPoliciesView : VisualElement
    {
        private readonly NetworkHubSession _session;
        private readonly NetworkHubUi.Split _split;

        /// <summary>Builds the view.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkPoliciesView(NetworkHubSession session)
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
            get => _session.SelectionFor(NetworkHubViews.Policies);
            set
            {
                _session.SetSelection(NetworkHubViews.Policies, value);
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
            header.Add(new Label($"{_session.Catalog.PolicyProfiles.Count} profile(s)"));
            header.Add(MolcaButtons.Mini("Add", AddProfile));
            _split.Master.Add(header);

            var list = new ScrollView();
            list.style.flexGrow = 1;
            _split.Master.Add(list);

            // The effective inspector is useful even with no profiles authored — it shows the library
            // defaults a route actually runs under — so it gets its own entry rather than being hidden
            // behind selecting a profile that may not exist.
            list.Add(NetworkHubUi.ListRow(
                "Effective policy",
                "What a route resolves to, layer by layer",
                MolcaStatusKind.None,
                "Resolved through the same resolver the runtime uses.",
                string.IsNullOrEmpty(Selected),
                () => Selected = string.Empty));

            if (_session.Catalog.PolicyProfiles.Count == 0)
            {
                list.Add(NetworkHubUi.Note(
                    "No policy profiles. Every route runs on the library defaults, which the effective " +
                    "inspector shows."));
                return;
            }

            foreach (var profile in _session.Catalog.PolicyProfiles)
            {
                if (profile == null) continue;

                bool isDefault = string.Equals(
                    profile.Id, _session.Catalog.DefaultPolicyProfileId, StringComparison.Ordinal);

                var badges = new List<VisualElement>();
                if (isDefault) badges.Add(NetworkHubUi.Badge("catalog default", MolcaStatusKind.Ok));
                if (!profile.ValidateTlsCertificate)
                    badges.Add(NetworkHubUi.Badge("TLS off", MolcaStatusKind.Error));

                list.Add(NetworkHubUi.ListRow(
                    profile.DisplayName,
                    profile.Id,
                    NetworkHubUi.StatusOf(
                        _session.Validation, NetworkValidationEntityKind.PolicyProfile, profile.Id),
                    "Validation status for this profile.",
                    string.Equals(profile.Id, Selected, StringComparison.Ordinal),
                    () => Selected = profile.Id,
                    badges.ToArray()));
            }
        }

        private void BuildDetail()
        {
            _split.Detail.Clear();

            var profile = _session.Catalog.FindPolicyProfile(Selected);
            if (profile == null)
            {
                BuildEffectiveInspector();
                return;
            }

            _split.Detail.Add(BuildAuthored(profile));
            _split.Detail.Add(BuildFindings(profile));
        }

        private VisualElement BuildAuthored(NetworkPolicyProfile profile)
        {
            var card = NetworkHubUi.Card(profile.DisplayName, profile.Id);

            card.Body.Add(NetworkHubUi.Heading("Timeouts"));
            card.Body.Add(NetworkHubUi.Field("Overall", $"{profile.OverallTimeoutSeconds}s",
                "Covers queueing, credential acquisition, retry backoff, and wire time."));
            card.Body.Add(NetworkHubUi.Field("Per attempt", $"{profile.AttemptTimeoutSeconds}s",
                "Clamped down to whatever is left of the overall budget."));

            card.Body.Add(NetworkHubUi.Heading("Retry"));
            card.Body.Add(NetworkHubUi.Field("Enabled", Yes(profile.RetryEnabled)));
            card.Body.Add(NetworkHubUi.Field("Max retries", profile.MaxRetries.ToString()));
            card.Body.Add(NetworkHubUi.Field("Backoff", $"{profile.RetryBaseDelaySeconds}s → {profile.RetryMaxDelaySeconds}s"));
            card.Body.Add(NetworkHubUi.Field("Full jitter", Yes(profile.RetryJitter),
                "Spreads retries so clients that failed together do not retry in lockstep."));
            card.Body.Add(NetworkHubUi.Field("Requires idempotence", Yes(profile.RetryRequiresIdempotence),
                "A mutating call is not retried merely because it failed."));
            card.Body.Add(NetworkHubUi.Field("Honors Retry-After", Yes(profile.HonorRetryAfter)));

            card.Body.Add(NetworkHubUi.Heading("Concurrency"));
            card.Body.Add(NetworkHubUi.Field("Max concurrent", Unbounded(profile.MaxConcurrentRequests)));
            card.Body.Add(NetworkHubUi.Field("Max queue depth", Unbounded(profile.MaxQueueDepth)));
            card.Body.Add(NetworkHubUi.Field("Circuit threshold", Unbounded(profile.CircuitFailureThreshold),
                "Consecutive failures before the route fails fast. 0 disables the breaker."));
            card.Body.Add(NetworkHubUi.Field("Circuit reset", $"{profile.CircuitResetSeconds}s"));

            card.Body.Add(NetworkHubUi.Heading("Transport safety"));
            card.Body.Add(NetworkHubUi.Field("Redirect mode", profile.RedirectMode.ToString()));
            card.Body.Add(NetworkHubUi.Field("Max redirects", profile.MaxRedirects.ToString()));
            card.Body.Add(NetworkHubUi.Field("Requires encryption", Yes(profile.RequireSecureTransport)));
            card.Body.Add(NetworkHubUi.Field("Validates TLS", Yes(profile.ValidateTlsCertificate),
                profile.ValidateTlsCertificate
                    ? null
                    : "A production environment overrides this back on. Certificate validation cannot be " +
                      "relaxed where production safety is enforced."));

            card.Body.Add(NetworkHubUi.Heading("Cache and diagnostics"));
            card.Body.Add(NetworkHubUi.Field("Cache mode", profile.CacheMode.ToString()));
            card.Body.Add(NetworkHubUi.Field("Cache TTL", $"{profile.CacheTtlSeconds}s"));
            card.Body.Add(NetworkHubUi.Field("Logs requests", Yes(profile.LogRequests)));
            card.Body.Add(NetworkHubUi.Field("Captures bodies", Yes(profile.CaptureBodies),
                "Bodies are redacted, but capturing them still retains more than not capturing them."));

            card.Body.Add(NetworkHubUi.Heading("Limits"));
            card.Body.Add(NetworkHubUi.Field("Max request bytes", Unbounded(profile.MaxRequestBytes)));
            card.Body.Add(NetworkHubUi.Field("Max response bytes", Unbounded(profile.MaxResponseBytes)));

            card.Body.Add(NetworkHubUi.Note(
                "Values are authored on the asset. Select 'Effective policy' to see which layer wins for a " +
                "given route."));

            return card;
        }

        /// <summary>
        /// The effective-policy inspector: pick a route, see every field's value and the layer that
        /// supplied it.
        /// </summary>
        private void BuildEffectiveInspector()
        {
            var card = NetworkHubUi.Card(
                "Effective policy",
                "Library default → Catalog → Environment → Service → Endpoint → Send override");

            string environmentId = _session.PreviewEnvironmentId;

            var services = new List<string>();
            foreach (var service in _session.Catalog.Services)
            {
                if (service != null && !string.IsNullOrEmpty(service.Id)) services.Add(service.Id);
            }

            if (string.IsNullOrEmpty(environmentId) || services.Count == 0)
            {
                card.Body.Add(NetworkHubUi.Note(
                    "Add an environment and a service, then choose a preview environment in the toolbar."));
                _split.Detail.Add(card);
                return;
            }

            string storedService = _session.SelectionFor(NetworkHubViews.Services);
            string serviceId = services.Contains(storedService) ? storedService : services[0];

            var picker = new PopupField<string>("Service", services, serviceId);
            picker.RegisterValueChangedCallback(evt =>
            {
                _session.SetSelection(NetworkHubViews.Services, evt.newValue);
                Rebuild();
            });
            card.Body.Add(picker);

            var route = _session.Effective.Resolve(new NetworkRouteKey(environmentId, serviceId));
            var policy = route.Policy;

            if (policy == null)
            {
                card.Body.Add(NetworkHubUi.Note("This route has no resolvable policy."));
                _split.Detail.Add(card);
                return;
            }

            if (!route.Resolves)
            {
                // The policy still resolves for an unroutable route, and it is still worth showing — an
                // author fixing the binding wants to know what the route will run under once it works.
                card.Body.Add(NetworkHubUi.Note(
                    $"This route does not resolve ({route.FailureReason}) but its inherited policy is " +
                    "still meaningful and is shown below."));
            }

            card.Body.Add(NetworkHubUi.Heading("Timeouts"));
            Effective(card, "Overall", policy.OverallTimeoutSeconds.Value + "s", policy.OverallTimeoutSeconds.Source);
            Effective(card, "Per attempt", policy.AttemptTimeoutSeconds.Value + "s", policy.AttemptTimeoutSeconds.Source);

            card.Body.Add(NetworkHubUi.Heading("Retry"));
            Effective(card, "Enabled", Yes(policy.RetryEnabled.Value), policy.RetryEnabled.Source);
            Effective(card, "Max retries", policy.MaxRetries.Value.ToString(), policy.MaxRetries.Source);
            Effective(card, "Base delay", policy.RetryBaseDelaySeconds.Value + "s", policy.RetryBaseDelaySeconds.Source);
            Effective(card, "Max delay", policy.RetryMaxDelaySeconds.Value + "s", policy.RetryMaxDelaySeconds.Source);
            Effective(card, "Jitter", Yes(policy.RetryJitter.Value), policy.RetryJitter.Source);
            Effective(card, "Requires idempotence", Yes(policy.RetryRequiresIdempotence.Value), policy.RetryRequiresIdempotence.Source);
            Effective(card, "Honors Retry-After", Yes(policy.HonorRetryAfter.Value), policy.HonorRetryAfter.Source);

            card.Body.Add(NetworkHubUi.Heading("Concurrency"));
            Effective(card, "Max concurrent", Unbounded(policy.MaxConcurrentRequests.Value), policy.MaxConcurrentRequests.Source);
            Effective(card, "Max queue depth", Unbounded(policy.MaxQueueDepth.Value), policy.MaxQueueDepth.Source);
            Effective(card, "Circuit threshold", Unbounded(policy.CircuitFailureThreshold.Value), policy.CircuitFailureThreshold.Source);
            Effective(card, "Circuit reset", policy.CircuitResetSeconds.Value + "s", policy.CircuitResetSeconds.Source);

            card.Body.Add(NetworkHubUi.Heading("Transport safety"));
            Effective(card, "Redirect mode", policy.RedirectMode.Value.ToString(), policy.RedirectMode.Source,
                "Security-restricted: a lower layer may tighten this but never weaken it.");
            Effective(card, "Max redirects", policy.MaxRedirects.Value.ToString(), policy.MaxRedirects.Source,
                "Security-restricted: the smallest authored value wins.");
            Effective(card, "Requires encryption", Yes(policy.RequireSecureTransport.Value), policy.RequireSecureTransport.Source,
                "Security-restricted: any layer requiring encryption wins.");
            Effective(card, "Validates TLS", Yes(policy.ValidateTlsCertificate.Value), policy.ValidateTlsCertificate.Source,
                "Security-restricted: production clamps this on regardless of what a profile authored.");

            card.Body.Add(NetworkHubUi.Heading("Cache and diagnostics"));
            Effective(card, "Cache mode", policy.CacheMode.Value.ToString(), policy.CacheMode.Source);
            Effective(card, "Cache TTL", policy.CacheTtlSeconds.Value + "s", policy.CacheTtlSeconds.Source);
            Effective(card, "Logs requests", Yes(policy.LogRequests.Value), policy.LogRequests.Source);
            Effective(card, "Captures bodies", Yes(policy.CaptureBodies.Value), policy.CaptureBodies.Source);

            card.Body.Add(NetworkHubUi.Heading("Limits"));
            Effective(card, "Max request bytes", Unbounded(policy.MaxRequestBytes.Value), policy.MaxRequestBytes.Source);
            Effective(card, "Max response bytes", Unbounded(policy.MaxResponseBytes.Value), policy.MaxResponseBytes.Source);

            _split.Detail.Add(card);

            if (policy.SecurityClamps.Count > 0)
                _split.Detail.Add(BuildClamps(policy));
        }

        private static void Effective(
            MolcaSectionCard card,
            string label,
            string value,
            NetworkConfigurationLayer layer,
            string note = null)
        {
            card.Body.Add(NetworkHubUi.EffectiveField(label, value, layer, note));
        }

        /// <summary>
        /// Where a security rule overruled a weaker authored value, and why.
        /// </summary>
        private static VisualElement BuildClamps(NetworkEffectivePolicy policy)
        {
            var card = NetworkHubUi.Card(
                "Security clamps",
                "A lower layer may tighten a security rule; it may never weaken one.",
                MolcaStatusKind.Warning,
                $"{policy.SecurityClamps.Count} applied");

            foreach (string clamp in policy.SecurityClamps)
                card.Body.Add(NetworkHubUi.Note(clamp));

            return card;
        }

        private VisualElement BuildFindings(NetworkPolicyProfile profile)
        {
            var findings = NetworkHubUi.FindingsFor(
                _session.Validation, NetworkValidationEntityKind.PolicyProfile, profile.Id);

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
                "policy", candidate => _session.Catalog.FindPolicyProfile(candidate) != null);

            var result = _session.Editing.CreatePolicyProfile(id);

            if (result.Success)
            {
                UnityEngine.Debug.Log($"[Network] {result.Message}");
                _session.SetSelection(NetworkHubViews.Policies, result.ResultId);
            }
            else
            {
                UnityEngine.Debug.LogWarning($"[Network] {result.Message}");
            }

            _session.Reload();
        }

        private static string Yes(bool value) => value ? "Yes" : "No";

        private static string Unbounded(long value) => value <= 0 ? "unbounded" : value.ToString();
    }
}
