using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Validation;
using Molca.Networking.Configuration;
using Molca.Networking.Routing;
using UnityEditor;
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

        /// <summary>
        /// The authored values of one profile, every one of them editable.
        /// </summary>
        /// <remarks>
        /// Grouped the way the editing service groups its setters, so one card section is one Undo step.
        /// Fields inside a group pass their siblings through unchanged, which is why a group setter takes
        /// every member rather than just the one that moved.
        /// </remarks>
        private VisualElement BuildAuthored(NetworkPolicyProfile profile)
        {
            string id = profile.Id;
            var editing = _session.Editing;
            var card = NetworkHubUi.Card(profile.DisplayName, id);

            card.Body.Add(NetworkHubFields.EditText(
                "Display name",
                profile.DisplayName,
                value => _session.Apply(editing.SetPolicyDisplayName(id, value))));

            card.Body.Add(NetworkHubUi.Field("Stable ID", id,
                "Named by every environment, service, and endpoint that overrides its policy."));

            card.Body.Add(NetworkHubUi.Heading("Timeouts"));
            card.Body.Add(NetworkHubFields.EditFloat(
                "Overall",
                profile.OverallTimeoutSeconds,
                value => _session.Apply(
                    editing.SetPolicyTimeouts(id, value, profile.AttemptTimeoutSeconds)),
                0f,
                "Seconds covering queueing, credential acquisition, retry backoff, and wire time."));
            card.Body.Add(NetworkHubFields.EditFloat(
                "Per attempt",
                profile.AttemptTimeoutSeconds,
                value => _session.Apply(
                    editing.SetPolicyTimeouts(id, profile.OverallTimeoutSeconds, value)),
                0f,
                "Seconds for one transport attempt. Clamped down to whatever is left of the overall budget."));

            card.Body.Add(NetworkHubUi.Heading("Retry"));
            card.Body.Add(NetworkHubFields.EditToggle(
                "Enabled",
                profile.RetryEnabled,
                value => _session.Apply(
                    editing.SetPolicyRetry(id, value, profile.MaxRetries, profile.RetryBaseDelaySeconds))));
            card.Body.Add(NetworkHubFields.EditInt(
                "Max retries",
                profile.MaxRetries,
                value => _session.Apply(
                    editing.SetPolicyRetry(id, profile.RetryEnabled, value, profile.RetryBaseDelaySeconds)),
                0, 10,
                "Attempts after the first, 0–10."));
            card.Body.Add(NetworkHubFields.EditFloat(
                "Base delay",
                profile.RetryBaseDelaySeconds,
                value => _session.Apply(
                    editing.SetPolicyRetry(id, profile.RetryEnabled, profile.MaxRetries, value)),
                0f,
                "First backoff delay in seconds; it doubles per attempt."));
            card.Body.Add(NetworkHubFields.EditFloat(
                "Max delay",
                profile.RetryMaxDelaySeconds,
                value => _session.Apply(editing.SetPolicyRetryShaping(
                    id, value, profile.RetryJitter, profile.RetryRequiresIdempotence,
                    profile.HonorRetryAfter)),
                0f,
                "Ceiling the doubling backoff cannot exceed."));
            card.Body.Add(NetworkHubFields.EditToggle(
                "Full jitter",
                profile.RetryJitter,
                value => _session.Apply(editing.SetPolicyRetryShaping(
                    id, profile.RetryMaxDelaySeconds, value, profile.RetryRequiresIdempotence,
                    profile.HonorRetryAfter)),
                "Spreads retries so clients that failed together do not retry in lockstep."));
            card.Body.Add(NetworkHubFields.EditToggle(
                "Requires idempotence",
                profile.RetryRequiresIdempotence,
                value => _session.Apply(editing.SetPolicyRetryShaping(
                    id, profile.RetryMaxDelaySeconds, profile.RetryJitter, value, profile.HonorRetryAfter)),
                "When on, a mutating call is not retried merely because it failed."));
            card.Body.Add(NetworkHubFields.EditToggle(
                "Honors Retry-After",
                profile.HonorRetryAfter,
                value => _session.Apply(editing.SetPolicyRetryShaping(
                    id, profile.RetryMaxDelaySeconds, profile.RetryJitter,
                    profile.RetryRequiresIdempotence, value)),
                "A server's Retry-After header overrides the computed backoff."));

            card.Body.Add(NetworkHubUi.Heading("Concurrency"));
            card.Body.Add(NetworkHubFields.EditInt(
                "Max concurrent",
                profile.MaxConcurrentRequests,
                value => _session.Apply(editing.SetPolicyConcurrency(id, value)),
                0, 64,
                "Simultaneous requests per route, 0–64. 0 means unbounded."));
            card.Body.Add(NetworkHubFields.EditInt(
                "Max queue depth",
                profile.MaxQueueDepth,
                value => _session.Apply(editing.SetPolicyQueueDepth(id, value)),
                0, 4096,
                "Requests allowed to wait for a slot, 0–4096. 0 means unbounded."));
            card.Body.Add(NetworkHubFields.EditInt(
                "Circuit threshold",
                profile.CircuitFailureThreshold,
                value => _session.Apply(
                    editing.SetPolicyCircuitBreaker(id, value, profile.CircuitResetSeconds)),
                0, 100,
                "Consecutive failures before the route fails fast. 0 disables the breaker."));
            card.Body.Add(NetworkHubFields.EditFloat(
                "Circuit reset",
                profile.CircuitResetSeconds,
                value => _session.Apply(
                    editing.SetPolicyCircuitBreaker(id, profile.CircuitFailureThreshold, value)),
                0f,
                "Seconds the breaker stays open before probing again."));

            // The four transport-safety fields resolve tighten-only: a lower layer may harden one but never
            // weaken it, so a value authored here is a ceiling rather than a guarantee.
            card.Body.Add(NetworkHubUi.Heading("Transport safety"));
            card.Body.Add(NetworkHubFields.EditEnum(
                "Redirect mode",
                profile.RedirectMode,
                value => _session.Apply(editing.SetPolicyTransportSafety(
                    id, value, profile.MaxRedirects, profile.RequireSecureTransport,
                    profile.ValidateTlsCertificate)),
                "Security-restricted: a lower layer may tighten this but never weaken it."));
            card.Body.Add(NetworkHubFields.EditInt(
                "Max redirects",
                profile.MaxRedirects,
                value => _session.Apply(editing.SetPolicyTransportSafety(
                    id, profile.RedirectMode, value, profile.RequireSecureTransport,
                    profile.ValidateTlsCertificate)),
                0, 10,
                "Security-restricted: the smallest authored value wins."));
            card.Body.Add(NetworkHubFields.EditToggle(
                "Requires encryption",
                profile.RequireSecureTransport,
                value => _session.Apply(editing.SetPolicyTransportSafety(
                    id, profile.RedirectMode, profile.MaxRedirects, value,
                    profile.ValidateTlsCertificate)),
                "Security-restricted: any layer requiring encryption wins."));
            card.Body.Add(NetworkHubFields.EditToggle(
                "Validates TLS",
                profile.ValidateTlsCertificate,
                value => _session.Apply(editing.SetPolicyTransportSafety(
                    id, profile.RedirectMode, profile.MaxRedirects, profile.RequireSecureTransport,
                    value)),
                profile.ValidateTlsCertificate
                    ? "Security-restricted: turning this off has no effect where production safety applies."
                    : "A production environment overrides this back on. Certificate validation cannot be " +
                      "relaxed where production safety is enforced."));

            card.Body.Add(NetworkHubUi.Heading("Cache and diagnostics"));
            card.Body.Add(NetworkHubFields.EditEnum(
                "Cache mode",
                profile.CacheMode,
                value => _session.Apply(editing.SetPolicyCache(id, value, profile.CacheTtlSeconds))));
            card.Body.Add(NetworkHubFields.EditFloat(
                "Cache TTL",
                profile.CacheTtlSeconds,
                value => _session.Apply(editing.SetPolicyCache(id, profile.CacheMode, value)),
                0f,
                "Seconds a cached response stays fresh."));
            card.Body.Add(NetworkHubFields.EditToggle(
                "Logs requests",
                profile.LogRequests,
                value => _session.Apply(
                    editing.SetPolicyDiagnostics(id, value, profile.CaptureBodies))));
            card.Body.Add(NetworkHubFields.EditToggle(
                "Captures bodies",
                profile.CaptureBodies,
                value => _session.Apply(
                    editing.SetPolicyDiagnostics(id, profile.LogRequests, value)),
                "Bodies are redacted, but capturing them still retains more than not capturing them."));

            card.Body.Add(NetworkHubUi.Heading("Limits"));
            card.Body.Add(NetworkHubFields.EditByteSize(
                "Max request bytes",
                profile.MaxRequestBytes,
                value => _session.Apply(editing.SetPolicyLimits(id, value, profile.MaxResponseBytes)),
                "Largest request body allowed. 0 means unbounded."));
            card.Body.Add(NetworkHubFields.EditByteSize(
                "Max response bytes",
                profile.MaxResponseBytes,
                value => _session.Apply(editing.SetPolicyLimits(id, profile.MaxRequestBytes, value)),
                "Largest response body accepted. 0 means unbounded."));

            bool isDefault = string.Equals(
                id, _session.Catalog.DefaultPolicyProfileId, StringComparison.Ordinal);

            card.Body.Add(NetworkHubUi.Actions(
                isDefault ? null : MolcaButtons.Mini("Make catalog default", () => MakeDefault(id)),
                MolcaButtons.Mini("Delete…", () => Delete(id))));

            card.Body.Add(NetworkHubUi.Note(
                "These are the values authored on this profile. Select 'Effective policy' to see which " +
                "layer wins for a given route."));

            return card;
        }

        private void MakeDefault(string policyProfileId) =>
            _session.Apply(_session.Editing.SetDefaultPolicyProfile(policyProfileId));

        private void Delete(string policyProfileId)
        {
            if (!EditorUtility.DisplayDialog(
                    "Delete policy profile?",
                    $"'{policyProfileId}' will be removed, and every environment, service, and endpoint " +
                    "that overrides its policy with it will fall back to the inherited value.\n\n" +
                    "This is one Undo step.",
                    "Delete", "Cancel"))
            {
                return;
            }

            var result = _session.Editing.DeletePolicyProfile(policyProfileId);

            if (result.Success)
                _session.SetSelection(NetworkHubViews.Policies, string.Empty);

            _session.Apply(result);
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
                _session.SetSelection(NetworkHubViews.Policies, result.ResultId);

            _session.Apply(result);
        }

        private static string Yes(bool value) => value ? "Yes" : "No";

        private static string Unbounded(long value) => value <= 0 ? "unbounded" : value.ToString();
    }
}
