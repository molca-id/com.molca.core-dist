using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;

namespace Molca.Editor.Networking.Authoring
{
    /// <summary>
    /// The per-field setters behind the Hub's authoring controls.
    /// </summary>
    /// <remarks>
    /// Grouped by the set of values an author reasons about together rather than one method per
    /// serialized field: each group is one Undo step, so reverting a timeout change does not also revert
    /// a retry change, and a control that edits one member of a group passes its siblings through
    /// unchanged.
    /// <para>
    /// Every setter validates before it writes and reports through <see cref="NetworkAuthoringResult"/>,
    /// so a Hub control, an MCP tool, and a test all get the same refusal for the same bad input. Nothing
    /// here writes a catalog field outside a <see cref="SerializedObject"/>.
    /// </para>
    /// </remarks>
    public sealed partial class NetworkCatalogEditingService
    {
        // Serialized field names not already named by the creation/refactor half of the service. Same
        // rationale: a field rename must break compilation here, not silently no-op a lookup.
        private const string FieldEnabledBuildTargets = "_enabledBuildTargets";
        private const string FieldEnabledBuildProfiles = "_enabledBuildProfiles";
        private const string FieldLabels = "_labels";
        private const string FieldNotes = "_notes";
        private const string FieldRequireSecureTransport = "_requireSecureTransport";
        private const string FieldOwnerNotes = "_ownerNotes";
        private const string FieldAllowedHostPatterns = "_allowedHostPatterns";
        private const string FieldDefaultHeaders = "_defaultHeaders";
        private const string FieldHealthEndpointId = "_healthEndpointId";
        private const string FieldSseOrigin = "_sseOrigin";
        private const string FieldWebSocketOrigin = "_webSocketOrigin";
        private const string FieldSocketIoOrigin = "_socketIoOrigin";
        private const string FieldSocketIoPath = "_socketIoPath";
        private const string FieldRegionLabel = "_regionLabel";
        private const string FieldEnabled = "_enabled";
        private const string FieldRetryMaxDelaySeconds = "_retryMaxDelaySeconds";
        private const string FieldRetryJitter = "_retryJitter";
        private const string FieldRetryRequiresIdempotence = "_retryRequiresIdempotence";
        private const string FieldHonorRetryAfter = "_honorRetryAfter";
        private const string FieldMaxQueueDepth = "_maxQueueDepth";
        private const string FieldCircuitFailureThreshold = "_circuitFailureThreshold";
        private const string FieldCircuitResetSeconds = "_circuitResetSeconds";
        private const string FieldRedirectMode = "_redirectMode";
        private const string FieldMaxRedirects = "_maxRedirects";
        private const string FieldValidateTlsCertificate = "_validateTlsCertificate";
        private const string FieldCacheMode = "_cacheMode";
        private const string FieldCacheTtlSeconds = "_cacheTtlSeconds";
        private const string FieldLogRequests = "_logRequests";
        private const string FieldCaptureBodies = "_captureBodies";
        private const string FieldMaxRequestBytes = "_maxRequestBytes";
        private const string FieldMaxResponseBytes = "_maxResponseBytes";
        private const string FieldProviderKey = "_providerKey";
        private const string FieldAudience = "_audience";
        private const string FieldScopes = "_scopes";
        private const string FieldHeaderName = "_headerName";
        private const string FieldScheme = "_scheme";
        private const string FieldRefreshMode = "_refreshMode";
        private const string FieldUsableFromRequestConsole = "_usableFromRequestConsole";
        private const string FieldExpectedResponseType = "_expectedResponseType";
        private const string FieldResponseTypeName = "_responseTypeName";
        private const string FieldRequiresIdempotencyKey = "_requiresIdempotencyKey";

        // HttpHeader is a plain [Serializable] with public fields, so no leading underscore.
        private const string FieldHeaderKey = "key";
        private const string FieldHeaderValue = "value";
        private const string FieldHeaderEnabled = "isEnabled";

        // ---- Environments

        /// <summary>
        /// Sets an environment's human-readable name.
        /// </summary>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="displayName">The new name. Empty falls back to the ID.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Safe to change at any time — nothing references an environment by display name. The stable ID
        /// is the referenced key, and changing that is <see cref="RenameEnvironmentId"/>.
        /// </remarks>
        public NetworkAuthoringResult SetEnvironmentDisplayName(string environmentId, string displayName)
        {
            if (!TryFindEntity(FieldEnvironments, environmentId, "environment",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            element.FindPropertyRelative(FieldDisplayName).stringValue =
                string.IsNullOrWhiteSpace(displayName) ? environmentId : displayName.Trim();

            Apply(serialized, "Rename Network Environment");
            return NetworkAuthoringResult.Ok($"Renamed environment '{environmentId}'.", environmentId);
        }

        /// <summary>
        /// Sets an environment's safety posture.
        /// </summary>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="classification">The new classification.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Promoting an environment to <see cref="NetworkEnvironmentClassification.Production"/> enforces
        /// production safety: TLS validation can no longer be relaxed for it, unencrypted origins are
        /// refused, and mutating console sends need per-send confirmation. The result says so, because the
        /// change silently tightens rules elsewhere in the catalog.
        /// </remarks>
        public NetworkAuthoringResult SetEnvironmentClassification(
            string environmentId,
            NetworkEnvironmentClassification classification)
        {
            if (!TryFindEntity(FieldEnvironments, environmentId, "environment",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            element.FindPropertyRelative(FieldClassification).enumValueIndex = (int)classification;

            Apply(serialized, "Set Network Environment Classification");

            string note = classification == NetworkEnvironmentClassification.Production
                ? " Production safety now applies: TLS validation cannot be relaxed here and unencrypted " +
                  "origins are refused."
                : string.Empty;

            return NetworkAuthoringResult.Ok(
                $"Environment '{environmentId}' is now {classification}.{note}", environmentId);
        }

        /// <summary>
        /// Sets the policy profile applied to every service in an environment.
        /// </summary>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="policyProfileId">
        /// An existing policy profile ID, or empty to inherit the catalog default.
        /// </param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetEnvironmentPolicyProfile(
            string environmentId,
            string policyProfileId)
        {
            if (!TryFindEntity(FieldEnvironments, environmentId, "environment",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            if (!string.IsNullOrEmpty(policyProfileId) && _catalog.FindPolicyProfile(policyProfileId) == null)
                return NetworkAuthoringResult.Fail($"No policy profile '{policyProfileId}' in this catalog.");

            element.FindPropertyRelative(FieldPolicyProfileId).stringValue = policyProfileId ?? string.Empty;

            Apply(serialized, "Set Network Environment Policy");

            return NetworkAuthoringResult.Ok(
                string.IsNullOrEmpty(policyProfileId)
                    ? $"Environment '{environmentId}' now inherits the catalog default policy."
                    : $"Environment '{environmentId}' now applies policy '{policyProfileId}'.",
                environmentId);
        }

        /// <summary>
        /// Sets whether every origin bound to an environment must be encrypted.
        /// </summary>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="require">Whether <c>https</c>/<c>wss</c> is required.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Writing <c>false</c> on a Production environment is accepted but has no effect: the profile
        /// forces the requirement on for Production regardless of the authored value. The result says so
        /// rather than reporting a success that changes nothing observable.
        /// </remarks>
        public NetworkAuthoringResult SetEnvironmentRequireSecureTransport(
            string environmentId,
            bool require)
        {
            var environment = _catalog.FindEnvironment(environmentId);
            if (environment == null)
                return NetworkAuthoringResult.Fail($"No environment '{environmentId}' in this catalog.");

            if (!TryFindEntity(FieldEnvironments, environmentId, "environment",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            element.FindPropertyRelative(FieldRequireSecureTransport).boolValue = require;

            Apply(serialized, "Set Network Environment Transport Requirement");

            bool forced = !require &&
                          environment.Classification == NetworkEnvironmentClassification.Production;

            return NetworkAuthoringResult.Ok(
                forced
                    ? $"Stored, but '{environmentId}' is Production, so encrypted transport stays required."
                    : require
                        ? $"Environment '{environmentId}' now requires encrypted transport."
                        : $"Environment '{environmentId}' no longer requires encrypted transport.",
                environmentId);
        }

        /// <summary>
        /// Sets an environment's authoring notes.
        /// </summary>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="notes">Free text, or empty to clear.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetEnvironmentNotes(string environmentId, string notes)
        {
            if (!TryFindEntity(FieldEnvironments, environmentId, "environment",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            element.FindPropertyRelative(FieldNotes).stringValue = notes ?? string.Empty;

            Apply(serialized, "Set Network Environment Notes");
            return NetworkAuthoringResult.Ok($"Updated notes on '{environmentId}'.", environmentId);
        }

        /// <summary>
        /// Replaces the build targets an environment is selectable for.
        /// </summary>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="buildTargets">Target names, or an empty list meaning "any target".</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Empty means any target, which is the permissive value — so clearing the list is reported
        /// explicitly rather than as a neutral update.
        /// </remarks>
        public NetworkAuthoringResult SetEnvironmentBuildTargets(
            string environmentId,
            IReadOnlyList<string> buildTargets)
        {
            if (!TryFindEntity(FieldEnvironments, environmentId, "environment",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            var cleaned = CleanStrings(buildTargets);
            WriteStringList(element.FindPropertyRelative(FieldEnabledBuildTargets), cleaned);

            Apply(serialized, "Set Network Environment Build Targets");

            return NetworkAuthoringResult.Ok(
                cleaned.Count == 0
                    ? $"Environment '{environmentId}' is now selectable for any build target."
                    : $"Environment '{environmentId}' is now limited to {cleaned.Count} build target(s).",
                environmentId);
        }

        /// <summary>
        /// Replaces the build profiles an environment is selectable for.
        /// </summary>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="buildProfiles">Profile names, or an empty list meaning "any profile".</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetEnvironmentBuildProfiles(
            string environmentId,
            IReadOnlyList<string> buildProfiles)
        {
            if (!TryFindEntity(FieldEnvironments, environmentId, "environment",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            var cleaned = CleanStrings(buildProfiles);
            WriteStringList(element.FindPropertyRelative(FieldEnabledBuildProfiles), cleaned);

            Apply(serialized, "Set Network Environment Build Profiles");

            return NetworkAuthoringResult.Ok(
                cleaned.Count == 0
                    ? $"Environment '{environmentId}' is now selectable for any build profile."
                    : $"Environment '{environmentId}' is now limited to {cleaned.Count} build profile(s).",
                environmentId);
        }

        /// <summary>
        /// Replaces an environment's free-form labels.
        /// </summary>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="labels">The labels.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetEnvironmentLabels(
            string environmentId,
            IReadOnlyList<string> labels)
        {
            if (!TryFindEntity(FieldEnvironments, environmentId, "environment",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            var cleaned = CleanStrings(labels);
            WriteStringList(element.FindPropertyRelative(FieldLabels), cleaned);

            Apply(serialized, "Set Network Environment Labels");
            return NetworkAuthoringResult.Ok(
                $"Environment '{environmentId}' now carries {cleaned.Count} label(s).", environmentId);
        }

        // ---- Services

        /// <summary>
        /// Sets a service's human-readable name.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="displayName">The new name. Empty falls back to the ID.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetServiceDisplayName(string serviceId, string displayName)
        {
            if (!TryFindEntity(FieldServices, serviceId, "service",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            element.FindPropertyRelative(FieldDisplayName).stringValue =
                string.IsNullOrWhiteSpace(displayName) ? serviceId : displayName.Trim();

            Apply(serialized, "Rename Network Service");
            return NetworkAuthoringResult.Ok($"Renamed service '{serviceId}'.", serviceId);
        }

        /// <summary>
        /// Sets the protocols a service declares.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="protocols">The protocol flags. Must name at least one.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Declaring a protocol does not supply an origin for it. A binding missing the origin for a newly
        /// declared protocol becomes a validation finding, which the result points at — the alternative,
        /// silently reusing the HTTP origin, is how a WebSocket ends up pointed at a REST host.
        /// </remarks>
        public NetworkAuthoringResult SetServiceProtocols(string serviceId, NetworkProtocols protocols)
        {
            if (protocols == NetworkProtocols.None)
                return NetworkAuthoringResult.Fail("A service must declare at least one protocol.");

            if (!TryFindEntity(FieldServices, serviceId, "service",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            element.FindPropertyRelative(FieldProtocols).intValue = (int)protocols;

            Apply(serialized, "Set Network Service Protocols");
            return NetworkAuthoringResult.Ok(
                $"Service '{serviceId}' now declares {protocols}. Each binding needs an origin per " +
                "declared protocol.",
                serviceId);
        }

        /// <summary>
        /// Sets a service's policy profile override.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="policyProfileId">
        /// An existing policy profile ID, or empty to inherit the environment's profile.
        /// </param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetServicePolicyProfile(string serviceId, string policyProfileId)
        {
            if (!TryFindEntity(FieldServices, serviceId, "service",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            if (!string.IsNullOrEmpty(policyProfileId) && _catalog.FindPolicyProfile(policyProfileId) == null)
                return NetworkAuthoringResult.Fail($"No policy profile '{policyProfileId}' in this catalog.");

            element.FindPropertyRelative(FieldPolicyProfileId).stringValue = policyProfileId ?? string.Empty;

            Apply(serialized, "Set Network Service Policy");

            return NetworkAuthoringResult.Ok(
                string.IsNullOrEmpty(policyProfileId)
                    ? $"Service '{serviceId}' now inherits its environment's policy."
                    : $"Service '{serviceId}' now applies policy '{policyProfileId}'.",
                serviceId);
        }

        /// <summary>
        /// Sets the credential profile a service sends with.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="credentialProfileId">
        /// An existing credential profile ID, or empty to send anonymously.
        /// </param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Naming a profile is not enough for a credential to attach: the profile's own scope must also
        /// name this service. Where it does not, the result says so — that mismatch sends requests out
        /// anonymously and is very hard to diagnose from the failing call.
        /// </remarks>
        public NetworkAuthoringResult SetServiceCredentialProfile(
            string serviceId,
            string credentialProfileId)
        {
            if (!TryFindEntity(FieldServices, serviceId, "service",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            NetworkCredentialProfile profile = null;
            if (!string.IsNullOrEmpty(credentialProfileId))
            {
                profile = _catalog.FindCredentialProfile(credentialProfileId);
                if (profile == null)
                {
                    return NetworkAuthoringResult.Fail(
                        $"No credential profile '{credentialProfileId}' in this catalog.");
                }
            }

            element.FindPropertyRelative(FieldCredentialProfileId).stringValue =
                credentialProfileId ?? string.Empty;

            Apply(serialized, "Set Network Service Credential");

            if (profile == null)
                return NetworkAuthoringResult.Ok($"Service '{serviceId}' now sends anonymously.", serviceId);

            string scopeNote = profile.AllowsService(serviceId)
                ? string.Empty
                : $" Note: '{credentialProfileId}' does not list '{serviceId}' in its allowed services, so " +
                  "requests still go out anonymous until the scope is widened.";

            return NetworkAuthoringResult.Ok(
                $"Service '{serviceId}' now uses credential '{credentialProfileId}'.{scopeNote}", serviceId);
        }

        /// <summary>
        /// Sets a service's ownership notes.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="ownerNotes">Free text, or empty to clear.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetServiceOwnerNotes(string serviceId, string ownerNotes)
        {
            if (!TryFindEntity(FieldServices, serviceId, "service",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            element.FindPropertyRelative(FieldOwnerNotes).stringValue = ownerNotes ?? string.Empty;

            Apply(serialized, "Set Network Service Owner Notes");
            return NetworkAuthoringResult.Ok($"Updated owner notes on '{serviceId}'.", serviceId);
        }

        /// <summary>
        /// Replaces a service's allowed host patterns.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="patterns">
        /// Host patterns — an exact host, or a single leading <c>*.</c> covering at least two labels. An
        /// empty list derives the allow-list from the bound origins instead.
        /// </param>
        /// <returns>The outcome. Nothing is written when any pattern is malformed.</returns>
        /// <remarks>
        /// Validated as a set before anything is written, so a typo in the third pattern cannot leave the
        /// first two applied. An empty list never means "any host": with nothing authored and nothing
        /// bound, nothing is allowed.
        /// </remarks>
        public NetworkAuthoringResult SetServiceAllowedHostPatterns(
            string serviceId,
            IReadOnlyList<string> patterns)
        {
            if (!TryFindEntity(FieldServices, serviceId, "service",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            var normalized = new List<string>();
            foreach (string pattern in CleanStrings(patterns))
            {
                if (!NetworkHostRule.TryNormalizePattern(pattern, out string clean, out string patternError))
                    return NetworkAuthoringResult.Fail($"'{pattern}' is not a valid host pattern: {patternError}");

                normalized.Add(clean);
            }

            WriteStringList(element.FindPropertyRelative(FieldAllowedHostPatterns), normalized);

            Apply(serialized, "Set Network Service Allowed Hosts");

            return NetworkAuthoringResult.Ok(
                normalized.Count == 0
                    ? $"Service '{serviceId}' now derives its allowed hosts from its bound origins."
                    : $"Service '{serviceId}' now allows {normalized.Count} host pattern(s).",
                serviceId);
        }

        /// <summary>
        /// Replaces a service's default headers.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="headers">The headers to send on every request to this service.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Refuses a header whose name is one a credential profile owns — <c>Authorization</c> and
        /// friends. A credential arrives through a credential profile, scoped per host and revalidated
        /// across redirects; a hand-authored auth header has none of that and would be committed to the
        /// asset in plain text.
        /// </remarks>
        public NetworkAuthoringResult SetServiceDefaultHeaders(
            string serviceId,
            IReadOnlyList<HttpHeader> headers)
        {
            if (!TryFindEntity(FieldServices, serviceId, "service",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            var cleaned = new List<HttpHeader>();
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    if (header == null || string.IsNullOrWhiteSpace(header.key))
                        continue;

                    string key = header.key.Trim();

                    if (IsCredentialHeader(key))
                    {
                        return NetworkAuthoringResult.Fail(
                            $"'{key}' is a credential header. Author it on a credential profile, which " +
                            "scopes the value per host and revalidates it across redirects, rather than " +
                            "committing it to this asset.");
                    }

                    cleaned.Add(new HttpHeader(key, header.value ?? string.Empty)
                    {
                        isEnabled = header.isEnabled,
                    });
                }
            }

            var list = element.FindPropertyRelative(FieldDefaultHeaders);
            list.ClearArray();

            for (int i = 0; i < cleaned.Count; i++)
            {
                list.InsertArrayElementAtIndex(i);
                var entry = list.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative(FieldHeaderKey).stringValue = cleaned[i].key;
                entry.FindPropertyRelative(FieldHeaderValue).stringValue = cleaned[i].value;
                entry.FindPropertyRelative(FieldHeaderEnabled).boolValue = cleaned[i].isEnabled;
            }

            Apply(serialized, "Set Network Service Default Headers");
            return NetworkAuthoringResult.Ok(
                $"Service '{serviceId}' now sends {cleaned.Count} default header(s).", serviceId);
        }

        /// <summary>
        /// Sets the endpoint used to health-check a service.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="endpointId">An existing endpoint ID, or empty for none.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetServiceHealthEndpoint(string serviceId, string endpointId)
        {
            if (!TryFindEntity(FieldServices, serviceId, "service",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            if (!string.IsNullOrEmpty(endpointId))
            {
                var index = new NetworkCatalogIndex(_catalog);
                if (!index.Endpoints.ContainsKey(endpointId))
                    return NetworkAuthoringResult.Fail($"No endpoint '{endpointId}' in this catalog.");
            }

            element.FindPropertyRelative(FieldHealthEndpointId).stringValue = endpointId ?? string.Empty;

            Apply(serialized, "Set Network Service Health Endpoint");

            return NetworkAuthoringResult.Ok(
                string.IsNullOrEmpty(endpointId)
                    ? $"Service '{serviceId}' has no health endpoint."
                    : $"Service '{serviceId}' health-checks through '{endpointId}'.",
                serviceId);
        }

        // ---- Bindings

        /// <summary>
        /// Sets one protocol's origin on a service's binding for an environment.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="protocol">
        /// The single protocol whose origin to write. Flag combinations are refused — one call writes one
        /// origin, so a failure cannot leave some protocols updated and others not.
        /// </param>
        /// <param name="origin">The absolute origin, or empty to clear it.</param>
        /// <returns>The outcome. The binding is created when absent.</returns>
        /// <remarks>
        /// WebSocket-family schemes are accepted only for the WebSocket and Socket.IO protocols, and SSE
        /// stays HTTP-family: an <c>https</c> SSE origin is correct, a <c>wss</c> one is not.
        /// </remarks>
        public NetworkAuthoringResult SetBindingOrigin(
            string serviceId,
            string environmentId,
            NetworkProtocols protocol,
            string origin)
        {
            if (!TryResolveOriginField(protocol, out string originField, out bool allowWebSocket, out string protocolError))
                return NetworkAuthoringResult.Fail(protocolError);

            if (_catalog.FindService(serviceId) == null)
                return NetworkAuthoringResult.Fail($"No service '{serviceId}' in this catalog.");

            if (_catalog.FindEnvironment(environmentId) == null)
                return NetworkAuthoringResult.Fail($"No environment '{environmentId}' in this catalog.");

            string normalized = string.Empty;
            if (!string.IsNullOrWhiteSpace(origin) &&
                !NetworkOrigin.TryNormalize(origin, allowWebSocket, out normalized, out string originError))
            {
                return NetworkAuthoringResult.Fail(originError);
            }

            if (!TryFindBinding(serviceId, environmentId, out var serialized, out var binding, out bool created,
                    out string bindingError))
            {
                return NetworkAuthoringResult.Fail(bindingError);
            }

            binding.FindPropertyRelative(originField).stringValue = normalized ?? string.Empty;

            Apply(serialized, created ? "Add Network Service Binding" : "Edit Network Service Binding");

            string verb = created ? "Bound" : "Updated";
            return NetworkAuthoringResult.Ok(
                $"{verb} {protocol} for service '{serviceId}' in '{environmentId}'.", environmentId);
        }

        /// <summary>
        /// Sets the Socket.IO handshake path on a service's binding for an environment.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="path">The handshake path, for example <c>/socket.io</c>, or empty for the default.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetBindingSocketIoPath(
            string serviceId,
            string environmentId,
            string path)
        {
            if (!TryFindBinding(serviceId, environmentId, out var serialized, out var binding, out bool created,
                    out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            binding.FindPropertyRelative(FieldSocketIoPath).stringValue = path?.Trim() ?? string.Empty;

            Apply(serialized, created ? "Add Network Service Binding" : "Edit Network Service Binding");
            return NetworkAuthoringResult.Ok(
                $"Set the Socket.IO path for '{serviceId}' in '{environmentId}'.", environmentId);
        }

        /// <summary>
        /// Sets the region label on a service's binding for an environment.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="regionLabel">Free-text region, or empty to clear.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetBindingRegionLabel(
            string serviceId,
            string environmentId,
            string regionLabel)
        {
            if (!TryFindBinding(serviceId, environmentId, out var serialized, out var binding, out bool created,
                    out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            binding.FindPropertyRelative(FieldRegionLabel).stringValue = regionLabel?.Trim() ?? string.Empty;

            Apply(serialized, created ? "Add Network Service Binding" : "Edit Network Service Binding");
            return NetworkAuthoringResult.Ok(
                $"Set the region label for '{serviceId}' in '{environmentId}'.", environmentId);
        }

        /// <summary>
        /// Enables or disables a service's binding for an environment.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="environmentId">An existing environment ID.</param>
        /// <param name="enabled">Whether the binding is usable.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Disabling keeps the authored origin but makes the route unresolvable, which is the difference
        /// between "we know the address and it is off here" and "we never had one".
        /// </remarks>
        public NetworkAuthoringResult SetBindingEnabled(
            string serviceId,
            string environmentId,
            bool enabled)
        {
            var service = _catalog.FindService(serviceId);
            if (service == null)
                return NetworkAuthoringResult.Fail($"No service '{serviceId}' in this catalog.");

            if (service.FindBinding(environmentId) == null)
            {
                return NetworkAuthoringResult.Fail(
                    $"Service '{serviceId}' has no binding in '{environmentId}' to enable or disable.");
            }

            if (!TryFindBinding(serviceId, environmentId, out var serialized, out var binding, out _,
                    out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            binding.FindPropertyRelative(FieldEnabled).boolValue = enabled;

            Apply(serialized, "Set Network Service Binding Enabled");

            return NetworkAuthoringResult.Ok(
                enabled
                    ? $"Enabled '{serviceId}' in '{environmentId}'."
                    : $"Disabled '{serviceId}' in '{environmentId}'. The origin is kept, but the route no " +
                      "longer resolves there.",
                environmentId);
        }

        /// <summary>
        /// Copies every origin on a service's binding from one environment to another.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="fromEnvironmentId">The environment to copy from.</param>
        /// <param name="toEnvironmentId">The environment to copy to, created when absent.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Always an explicit action, never an implicit fallback between environments (plan §7.6) — silent
        /// fallback is how a staging build ends up talking to production. Copying is what makes the absence
        /// of fallback tolerable to author.
        /// <para>
        /// Every origin moves together in one Undo step, so a copied binding cannot end up with the HTTP
        /// origin of one environment and the socket origin of another. The region label is deliberately not
        /// copied: it describes where the source is, and carrying it over would mislabel the target.
        /// </para>
        /// </remarks>
        public NetworkAuthoringResult CopyBinding(
            string serviceId,
            string fromEnvironmentId,
            string toEnvironmentId)
        {
            if (string.Equals(fromEnvironmentId, toEnvironmentId, StringComparison.Ordinal))
                return NetworkAuthoringResult.Fail("Source and target environment are the same.");

            var service = _catalog.FindService(serviceId);
            if (service == null)
                return NetworkAuthoringResult.Fail($"No service '{serviceId}' in this catalog.");

            var source = service.FindBinding(fromEnvironmentId);
            if (source == null)
            {
                return NetworkAuthoringResult.Fail(
                    $"Service '{serviceId}' has no binding in '{fromEnvironmentId}' to copy.");
            }

            if (_catalog.FindEnvironment(toEnvironmentId) == null)
                return NetworkAuthoringResult.Fail($"No environment '{toEnvironmentId}' in this catalog.");

            if (!TryFindBinding(serviceId, toEnvironmentId, out var serialized, out var target, out bool created,
                    out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            target.FindPropertyRelative(FieldHttpOrigin).stringValue = source.HttpOrigin ?? string.Empty;
            target.FindPropertyRelative(FieldSseOrigin).stringValue = source.SseOrigin ?? string.Empty;
            target.FindPropertyRelative(FieldWebSocketOrigin).stringValue =
                source.WebSocketOrigin ?? string.Empty;
            target.FindPropertyRelative(FieldSocketIoOrigin).stringValue =
                source.SocketIoOrigin ?? string.Empty;
            target.FindPropertyRelative(FieldSocketIoPath).stringValue = source.SocketIoPath ?? string.Empty;

            Apply(serialized, "Copy Network Service Binding");

            string verb = created ? "Copied" : "Overwrote";
            return NetworkAuthoringResult.Ok(
                $"{verb} the '{fromEnvironmentId}' origins of '{serviceId}' into '{toEnvironmentId}'.",
                toEnvironmentId);
        }

        /// <summary>
        /// Removes a service's binding for an environment entirely.
        /// </summary>
        /// <param name="serviceId">An existing service ID.</param>
        /// <param name="environmentId">The environment to unbind.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Distinct from disabling: after this the service has no address here at all, and a request
        /// reports route resolution rather than a disabled binding. Nothing falls back to another
        /// environment's origin.
        /// </remarks>
        public NetworkAuthoringResult RemoveBinding(string serviceId, string environmentId)
        {
            var service = _catalog.FindService(serviceId);
            if (service == null)
                return NetworkAuthoringResult.Fail($"No service '{serviceId}' in this catalog.");

            if (service.FindBinding(environmentId) == null)
                return NetworkAuthoringResult.Fail($"Service '{serviceId}' has no binding in '{environmentId}'.");

            if (!TryFindEntity(FieldServices, serviceId, "service",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            RemoveElementById(element.FindPropertyRelative(FieldBindings), FieldEnvironmentId, environmentId);

            Apply(serialized, "Remove Network Service Binding");
            return NetworkAuthoringResult.Ok(
                $"Removed the '{environmentId}' binding from '{serviceId}'. Requests there now report a " +
                "route-resolution error.",
                environmentId);
        }

        // ---- Policy profiles

        /// <summary>
        /// Sets a policy profile's human-readable name.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="displayName">The new name. Empty falls back to the ID.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetPolicyDisplayName(string policyProfileId, string displayName)
        {
            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldDisplayName).stringValue =
                string.IsNullOrWhiteSpace(displayName) ? policyProfileId : displayName.Trim();

            Apply(serialized, "Rename Network Policy Profile");
            return NetworkAuthoringResult.Ok($"Renamed policy profile '{policyProfileId}'.", policyProfileId);
        }

        /// <summary>
        /// Sets a policy profile's retry shaping — the values beyond whether it retries at all.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="maxDelaySeconds">Ceiling the doubling backoff cannot exceed.</param>
        /// <param name="jitter">Whether backoff is spread with full jitter.</param>
        /// <param name="requiresIdempotence">Whether only idempotent calls may be retried.</param>
        /// <param name="honorRetryAfter">Whether a server's <c>Retry-After</c> overrides the backoff.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Separate from <see cref="SetPolicyRetry"/> so the two controls an author reaches for most —
        /// on/off and attempt count — stay one revert away from their previous value.
        /// </remarks>
        public NetworkAuthoringResult SetPolicyRetryShaping(
            string policyProfileId,
            float maxDelaySeconds,
            bool jitter,
            bool requiresIdempotence,
            bool honorRetryAfter)
        {
            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldRetryMaxDelaySeconds).floatValue = Mathf.Max(0f, maxDelaySeconds);
            element.FindPropertyRelative(FieldRetryJitter).boolValue = jitter;
            element.FindPropertyRelative(FieldRetryRequiresIdempotence).boolValue = requiresIdempotence;
            element.FindPropertyRelative(FieldHonorRetryAfter).boolValue = honorRetryAfter;

            Apply(serialized, "Set Network Policy Retry Shaping");

            string note = requiresIdempotence
                ? string.Empty
                : " Retries are no longer limited to idempotent calls, so a failed mutation may be sent twice.";

            return NetworkAuthoringResult.Ok(
                $"Updated retry shaping on '{policyProfileId}'.{note}", policyProfileId);
        }

        /// <summary>
        /// Sets a policy profile's queue depth.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="maxQueueDepth">
        /// Requests allowed to wait for a concurrency slot. Clamped to the field's 0–4096 range; 0 means
        /// unbounded.
        /// </param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetPolicyQueueDepth(string policyProfileId, int maxQueueDepth)
        {
            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldMaxQueueDepth).intValue = Mathf.Clamp(maxQueueDepth, 0, 4096);

            Apply(serialized, "Set Network Policy Queue Depth");
            return NetworkAuthoringResult.Ok(
                maxQueueDepth <= 0
                    ? $"Policy '{policyProfileId}' has an unbounded queue."
                    : $"Policy '{policyProfileId}' queues at most {maxQueueDepth} request(s).",
                policyProfileId);
        }

        /// <summary>
        /// Sets a policy profile's circuit breaker.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="failureThreshold">
        /// Consecutive failures before the route fails fast. Clamped to the field's 0–100 range; 0
        /// disables the breaker.
        /// </param>
        /// <param name="resetSeconds">How long the breaker stays open before probing again.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetPolicyCircuitBreaker(
            string policyProfileId,
            int failureThreshold,
            float resetSeconds)
        {
            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldCircuitFailureThreshold).intValue =
                Mathf.Clamp(failureThreshold, 0, 100);
            element.FindPropertyRelative(FieldCircuitResetSeconds).floatValue = Mathf.Max(0f, resetSeconds);

            Apply(serialized, "Set Network Policy Circuit Breaker");

            return NetworkAuthoringResult.Ok(
                failureThreshold <= 0
                    ? $"Policy '{policyProfileId}' has no circuit breaker."
                    : $"Policy '{policyProfileId}' opens after {failureThreshold} consecutive failure(s) and " +
                      $"retries after {resetSeconds}s.",
                policyProfileId);
        }

        /// <summary>
        /// Sets a policy profile's transport-safety rules.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="redirectMode">Which redirects are followed.</param>
        /// <param name="maxRedirects">Redirect hops allowed. Clamped to the field's 0–10 range.</param>
        /// <param name="requireSecureTransport">Whether the route must be encrypted.</param>
        /// <param name="validateTlsCertificate">Whether the server certificate is validated.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// These four are the security-restricted fields: the resolver lets a lower layer tighten one but
        /// never weaken it. Writing a weaker value here is therefore accepted but may not take effect —
        /// disabling TLS validation is reported as such, because a Production environment clamps it back
        /// on and an author who does not know that will chase the wrong thing.
        /// </remarks>
        public NetworkAuthoringResult SetPolicyTransportSafety(
            string policyProfileId,
            NetworkRedirectMode redirectMode,
            int maxRedirects,
            bool requireSecureTransport,
            bool validateTlsCertificate)
        {
            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldRedirectMode).enumValueIndex = (int)redirectMode;
            element.FindPropertyRelative(FieldMaxRedirects).intValue = Mathf.Clamp(maxRedirects, 0, 10);
            element.FindPropertyRelative(FieldRequireSecureTransport).boolValue = requireSecureTransport;
            element.FindPropertyRelative(FieldValidateTlsCertificate).boolValue = validateTlsCertificate;

            Apply(serialized, "Set Network Policy Transport Safety");

            string note = validateTlsCertificate
                ? string.Empty
                : " TLS validation is off in this profile; any Production environment clamps it back on.";

            return NetworkAuthoringResult.Ok(
                $"Updated transport safety on '{policyProfileId}'.{note}", policyProfileId);
        }

        /// <summary>
        /// Sets a policy profile's response caching.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="cacheMode">Which responses may be cached.</param>
        /// <param name="ttlSeconds">How long a cached response stays fresh.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetPolicyCache(
            string policyProfileId,
            NetworkCacheMode cacheMode,
            float ttlSeconds)
        {
            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldCacheMode).enumValueIndex = (int)cacheMode;
            element.FindPropertyRelative(FieldCacheTtlSeconds).floatValue = Mathf.Max(0f, ttlSeconds);

            Apply(serialized, "Set Network Policy Cache");

            return NetworkAuthoringResult.Ok(
                cacheMode == NetworkCacheMode.Disabled
                    ? $"Policy '{policyProfileId}' does not cache."
                    : $"Policy '{policyProfileId}' caches {cacheMode} for {ttlSeconds}s.",
                policyProfileId);
        }

        /// <summary>
        /// Sets a policy profile's diagnostics.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="logRequests">Whether requests are logged.</param>
        /// <param name="captureBodies">Whether request/response bodies are captured.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Enabling body capture is reported as a retention change. Bodies are redacted, but capturing
        /// them still retains more than not capturing them.
        /// </remarks>
        public NetworkAuthoringResult SetPolicyDiagnostics(
            string policyProfileId,
            bool logRequests,
            bool captureBodies)
        {
            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldLogRequests).boolValue = logRequests;
            element.FindPropertyRelative(FieldCaptureBodies).boolValue = captureBodies;

            Apply(serialized, "Set Network Policy Diagnostics");

            string note = captureBodies
                ? " Bodies are now captured; they are redacted, but this retains more than before."
                : string.Empty;

            return NetworkAuthoringResult.Ok(
                $"Updated diagnostics on '{policyProfileId}'.{note}", policyProfileId);
        }

        /// <summary>
        /// Sets a policy profile's payload size limits.
        /// </summary>
        /// <param name="policyProfileId">An existing policy profile ID.</param>
        /// <param name="maxRequestBytes">Largest request body allowed; 0 means unbounded.</param>
        /// <param name="maxResponseBytes">Largest response body accepted; 0 means unbounded.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetPolicyLimits(
            string policyProfileId,
            long maxRequestBytes,
            long maxResponseBytes)
        {
            if (!TryFindPolicyElement(policyProfileId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldMaxRequestBytes).longValue = Math.Max(0L, maxRequestBytes);
            element.FindPropertyRelative(FieldMaxResponseBytes).longValue = Math.Max(0L, maxResponseBytes);

            Apply(serialized, "Set Network Policy Limits");
            return NetworkAuthoringResult.Ok($"Updated size limits on '{policyProfileId}'.", policyProfileId);
        }

        /// <summary>
        /// Removes a policy profile and clears every reference to it.
        /// </summary>
        /// <param name="policyProfileId">The profile to delete.</param>
        /// <returns>The outcome, listing each reference that was cleared.</returns>
        /// <remarks>
        /// References are cleared rather than left dangling: an environment, service, or endpoint pointing
        /// at a deleted profile would resolve to the library defaults anyway, so leaving the name behind
        /// would only misreport where a value came from. Applied as one Undo step across the catalog and
        /// every endpoint collection.
        /// </remarks>
        public NetworkAuthoringResult DeletePolicyProfile(string policyProfileId)
        {
            if (_catalog.FindPolicyProfile(policyProfileId) == null)
                return NetworkAuthoringResult.Fail($"No policy profile '{policyProfileId}' in this catalog.");

            var affected = new List<string>();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Delete Network Policy Profile");

            var serialized = new SerializedObject(_catalog);

            var catalogDefault = serialized.FindProperty(FieldDefaultPolicyProfileId);
            if (catalogDefault.stringValue == policyProfileId)
            {
                catalogDefault.stringValue = string.Empty;
                affected.Add("catalog default policy");
            }

            ClearPolicyReferences(
                serialized.FindProperty(FieldEnvironments), policyProfileId, "environment", affected);
            ClearPolicyReferences(
                serialized.FindProperty(FieldServices), policyProfileId, "service", affected);

            RemoveElementById(serialized.FindProperty(FieldPolicyProfiles), FieldId, policyProfileId);
            Apply(serialized, "Delete Network Policy Profile");

            foreach (var collection in _catalog.EndpointCollections)
            {
                if (collection == null) continue;

                var collectionSerialized = new SerializedObject(collection);
                var endpoints = collectionSerialized.FindProperty(FieldEndpoints);
                bool changed = false;

                for (int e = 0; e < endpoints.arraySize; e++)
                {
                    var endpoint = endpoints.GetArrayElementAtIndex(e);
                    var reference = endpoint.FindPropertyRelative(FieldPolicyProfileId);
                    if (reference.stringValue != policyProfileId) continue;

                    reference.stringValue = string.Empty;
                    affected.Add($"endpoint '{endpoint.FindPropertyRelative(FieldId).stringValue}'");
                    changed = true;
                }

                if (changed)
                    Apply(collectionSerialized, "Delete Network Policy Profile");
            }

            Undo.CollapseUndoOperations(undoGroup);

            return NetworkAuthoringResult.Ok(
                $"Deleted policy profile '{policyProfileId}' and cleared {affected.Count} reference(s).",
                policyProfileId,
                affected);
        }

        /// <summary>Clears a policy reference on every element of a catalog list.</summary>
        private static void ClearPolicyReferences(
            SerializedProperty list,
            string policyProfileId,
            string label,
            List<string> affected)
        {
            for (int i = 0; i < list.arraySize; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                var reference = element.FindPropertyRelative(FieldPolicyProfileId);
                if (reference.stringValue != policyProfileId) continue;

                reference.stringValue = string.Empty;
                affected.Add($"{label} '{element.FindPropertyRelative(FieldId).stringValue}'");
            }
        }

        // ---- Credential profiles
        //
        // Metadata only, throughout. No setter here accepts, derives, or stores a credential value; the
        // provider kind and key name where the value comes from at send time.

        /// <summary>
        /// Sets a credential profile's human-readable name.
        /// </summary>
        /// <param name="credentialProfileId">An existing credential profile ID.</param>
        /// <param name="displayName">The new name. Empty falls back to the ID.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetCredentialDisplayName(
            string credentialProfileId,
            string displayName)
        {
            if (!TryFindEntity(FieldCredentialProfiles, credentialProfileId, "credential profile",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            element.FindPropertyRelative(FieldDisplayName).stringValue =
                string.IsNullOrWhiteSpace(displayName) ? credentialProfileId : displayName.Trim();

            Apply(serialized, "Rename Network Credential Profile");
            return NetworkAuthoringResult.Ok(
                $"Renamed credential profile '{credentialProfileId}'.", credentialProfileId);
        }

        /// <summary>
        /// Sets which provider supplies a credential, and the key it is looked up by.
        /// </summary>
        /// <param name="credentialProfileId">An existing credential profile ID.</param>
        /// <param name="providerKind">The provider that supplies the value at send time.</param>
        /// <param name="providerKey">
        /// The lookup key handed to the provider — an environment variable name, for example. A key names
        /// a secret; it is not one, and the value it names never enters this asset.
        /// </param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Rejects a key that looks like a credential value rather than a name for one. It is a shallow
        /// check and not a guarantee, but the failure it prevents — a real token committed to a shared
        /// asset — is expensive enough to be worth catching at the obvious cases.
        /// </remarks>
        public NetworkAuthoringResult SetCredentialProvider(
            string credentialProfileId,
            NetworkCredentialProviderKind providerKind,
            string providerKey)
        {
            if (!TryFindEntity(FieldCredentialProfiles, credentialProfileId, "credential profile",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            string key = providerKey?.Trim() ?? string.Empty;

            if (LooksLikeSecretValue(key))
            {
                return NetworkAuthoringResult.Fail(
                    "That looks like a credential value rather than a lookup key. Store the value in the " +
                    "provider — an environment variable, for example — and name it here.");
            }

            element.FindPropertyRelative(FieldProviderKind).enumValueIndex = (int)providerKind;
            element.FindPropertyRelative(FieldProviderKey).stringValue = key;

            Apply(serialized, "Set Network Credential Provider");

            return NetworkAuthoringResult.Ok(
                providerKind == NetworkCredentialProviderKind.None
                    ? $"Profile '{credentialProfileId}' has no provider, so it never attaches a credential."
                    : $"Profile '{credentialProfileId}' resolves through {providerKind}.",
                credentialProfileId);
        }

        /// <summary>
        /// Sets a credential profile's audience and scopes.
        /// </summary>
        /// <param name="credentialProfileId">An existing credential profile ID.</param>
        /// <param name="audience">The token audience, or empty for none.</param>
        /// <param name="scopes">The scopes requested from the provider.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetCredentialAudience(
            string credentialProfileId,
            string audience,
            IReadOnlyList<string> scopes)
        {
            if (!TryFindEntity(FieldCredentialProfiles, credentialProfileId, "credential profile",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            element.FindPropertyRelative(FieldAudience).stringValue = audience?.Trim() ?? string.Empty;
            WriteStringList(element.FindPropertyRelative(FieldScopes), CleanStrings(scopes));

            Apply(serialized, "Set Network Credential Audience");
            return NetworkAuthoringResult.Ok(
                $"Updated the audience and scopes on '{credentialProfileId}'.", credentialProfileId);
        }

        /// <summary>
        /// Sets how a credential is attached to a request, and when it is refreshed.
        /// </summary>
        /// <param name="credentialProfileId">An existing credential profile ID.</param>
        /// <param name="headerName">The header the value is written to.</param>
        /// <param name="scheme">
        /// The prefix placed before the value, for example <c>"Bearer "</c>. Empty sends the raw value.
        /// </param>
        /// <param name="refreshMode">When the provider is asked for a fresh value.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetCredentialTransport(
            string credentialProfileId,
            string headerName,
            string scheme,
            NetworkCredentialRefreshMode refreshMode)
        {
            if (!TryFindEntity(FieldCredentialProfiles, credentialProfileId, "credential profile",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            string header = headerName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(header))
                return NetworkAuthoringResult.Fail("A credential needs a header name to be attached to.");

            // The scheme is a prefix, so its trailing space is meaningful — "Bearer " must not become
            // "Bearer". Only the leading side is trimmed.
            element.FindPropertyRelative(FieldHeaderName).stringValue = header;
            element.FindPropertyRelative(FieldScheme).stringValue = scheme?.TrimStart() ?? string.Empty;
            element.FindPropertyRelative(FieldRefreshMode).enumValueIndex = (int)refreshMode;

            Apply(serialized, "Set Network Credential Transport");
            return NetworkAuthoringResult.Ok(
                $"Profile '{credentialProfileId}' attaches to '{header}' and refreshes {refreshMode}.",
                credentialProfileId);
        }

        /// <summary>
        /// Replaces the services allowed to use a credential profile.
        /// </summary>
        /// <param name="credentialProfileId">An existing credential profile ID.</param>
        /// <param name="serviceIds">The allowed service IDs.</param>
        /// <returns>The outcome. Nothing is written when a named service does not exist.</returns>
        /// <remarks>
        /// An empty list denies everything — it never means "any service". The result says so, because an
        /// unscoped profile fails by sending requests out anonymously, which looks like a server-side
        /// authorization problem from the call site.
        /// </remarks>
        public NetworkAuthoringResult SetCredentialAllowedServices(
            string credentialProfileId,
            IReadOnlyList<string> serviceIds)
        {
            if (!TryFindEntity(FieldCredentialProfiles, credentialProfileId, "credential profile",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            var cleaned = CleanStrings(serviceIds);
            foreach (string serviceId in cleaned)
            {
                if (_catalog.FindService(serviceId) == null)
                    return NetworkAuthoringResult.Fail($"No service '{serviceId}' in this catalog.");
            }

            WriteStringList(element.FindPropertyRelative(FieldAllowedServiceIds), cleaned);

            Apply(serialized, "Set Network Credential Allowed Services");

            return NetworkAuthoringResult.Ok(
                cleaned.Count == 0
                    ? $"Profile '{credentialProfileId}' now names no service, so it is denied everywhere."
                    : $"Profile '{credentialProfileId}' may be used by {cleaned.Count} service(s).",
                credentialProfileId);
        }

        /// <summary>
        /// Replaces the hosts a credential may be sent to.
        /// </summary>
        /// <param name="credentialProfileId">An existing credential profile ID.</param>
        /// <param name="patterns">
        /// Host patterns — an exact host, or a single leading <c>*.</c> covering at least two labels.
        /// </param>
        /// <returns>The outcome. Nothing is written when any pattern is malformed.</returns>
        /// <remarks>
        /// This list is what stops a token following a redirect off-domain: it is checked again after
        /// every hop, against the host the request is actually about to reach. An empty list denies
        /// everything.
        /// </remarks>
        public NetworkAuthoringResult SetCredentialAllowedHosts(
            string credentialProfileId,
            IReadOnlyList<string> patterns)
        {
            if (!TryFindEntity(FieldCredentialProfiles, credentialProfileId, "credential profile",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            var normalized = new List<string>();
            foreach (string pattern in CleanStrings(patterns))
            {
                if (!NetworkHostRule.TryNormalizePattern(pattern, out string clean, out string patternError))
                    return NetworkAuthoringResult.Fail($"'{pattern}' is not a valid host pattern: {patternError}");

                normalized.Add(clean);
            }

            WriteStringList(element.FindPropertyRelative(FieldAllowedHostPatterns), normalized);

            Apply(serialized, "Set Network Credential Allowed Hosts");

            return NetworkAuthoringResult.Ok(
                normalized.Count == 0
                    ? $"Profile '{credentialProfileId}' now names no host, so it is denied everywhere."
                    : $"Profile '{credentialProfileId}' may reach {normalized.Count} host pattern(s).",
                credentialProfileId);
        }

        /// <summary>
        /// Sets whether the request console may send with a credential profile.
        /// </summary>
        /// <param name="credentialProfileId">An existing credential profile ID.</param>
        /// <param name="usable">Whether editor-initiated sends may use it.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Opt-in per profile. The console runs in the editor against whatever environment is previewed,
        /// so a production credential is usable there only when someone said so deliberately.
        /// </remarks>
        public NetworkAuthoringResult SetCredentialConsoleUse(string credentialProfileId, bool usable)
        {
            if (!TryFindEntity(FieldCredentialProfiles, credentialProfileId, "credential profile",
                    out var serialized, out var element, out string error))
            {
                return NetworkAuthoringResult.Fail(error);
            }

            element.FindPropertyRelative(FieldUsableFromRequestConsole).boolValue = usable;

            Apply(serialized, "Set Network Credential Console Use");

            return NetworkAuthoringResult.Ok(
                usable
                    ? $"The request console may now send with '{credentialProfileId}'."
                    : $"The request console may no longer send with '{credentialProfileId}'.",
                credentialProfileId);
        }

        /// <summary>
        /// Removes a credential profile and clears every service that named it.
        /// </summary>
        /// <param name="credentialProfileId">The profile to delete.</param>
        /// <returns>The outcome, listing each service that now sends anonymously.</returns>
        /// <remarks>
        /// References are cleared rather than left dangling. A service naming a deleted profile sends
        /// anonymously either way; clearing it makes that visible at the service instead of only at the
        /// failing request.
        /// </remarks>
        public NetworkAuthoringResult DeleteCredentialProfile(string credentialProfileId)
        {
            if (_catalog.FindCredentialProfile(credentialProfileId) == null)
            {
                return NetworkAuthoringResult.Fail(
                    $"No credential profile '{credentialProfileId}' in this catalog.");
            }

            var affected = new List<string>();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Delete Network Credential Profile");

            var serialized = new SerializedObject(_catalog);

            var services = serialized.FindProperty(FieldServices);
            for (int s = 0; s < services.arraySize; s++)
            {
                var service = services.GetArrayElementAtIndex(s);
                var reference = service.FindPropertyRelative(FieldCredentialProfileId);
                if (reference.stringValue != credentialProfileId) continue;

                reference.stringValue = string.Empty;
                affected.Add($"service '{service.FindPropertyRelative(FieldId).stringValue}' now anonymous");
            }

            RemoveElementById(serialized.FindProperty(FieldCredentialProfiles), FieldId, credentialProfileId);

            Apply(serialized, "Delete Network Credential Profile");
            Undo.CollapseUndoOperations(undoGroup);

            return NetworkAuthoringResult.Ok(
                $"Deleted credential profile '{credentialProfileId}' and cleared {affected.Count} reference(s).",
                credentialProfileId,
                affected);
        }

        // ---- Catalog-level

        /// <summary>
        /// Sets a catalog's display name for one of its collections' metadata.
        /// </summary>
        /// <param name="collection">The collection to edit.</param>
        /// <param name="displayName">The new name. Empty falls back to the collection ID.</param>
        /// <param name="serviceId">
        /// Default service for endpoints that name none, or empty to require each endpoint to name its own.
        /// </param>
        /// <param name="description">Free text, or empty to clear.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetCollectionMetadata(
            NetworkEndpointCollection collection,
            string displayName,
            string serviceId,
            string description)
        {
            if (collection == null)
                return NetworkAuthoringResult.Fail("No endpoint collection was supplied.");

            if (!string.IsNullOrEmpty(serviceId) && _catalog.FindService(serviceId) == null)
                return NetworkAuthoringResult.Fail($"No service '{serviceId}' in this catalog.");

            var serialized = new SerializedObject(collection);

            serialized.FindProperty(FieldDisplayName).stringValue =
                string.IsNullOrWhiteSpace(displayName) ? collection.CollectionId : displayName.Trim();
            serialized.FindProperty(FieldServiceId).stringValue = serviceId ?? string.Empty;
            serialized.FindProperty(FieldDescription).stringValue = description ?? string.Empty;

            Apply(serialized, "Set Network Collection Metadata");
            return NetworkAuthoringResult.Ok(
                $"Updated collection '{collection.CollectionId}'.", collection.CollectionId);
        }

        // ---- Endpoints

        /// <summary>
        /// Sets an endpoint's display name.
        /// </summary>
        /// <param name="collection">The collection holding the endpoint.</param>
        /// <param name="endpointId">An existing endpoint ID.</param>
        /// <param name="displayName">The new name. Empty falls back to the ID.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetEndpointDisplayName(
            NetworkEndpointCollection collection,
            string endpointId,
            string displayName)
        {
            if (!TryFindEndpoint(collection, endpointId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldDisplayName).stringValue =
                string.IsNullOrWhiteSpace(displayName) ? endpointId : displayName.Trim();

            Apply(serialized, "Rename Network Endpoint");
            return NetworkAuthoringResult.Ok($"Renamed endpoint '{endpointId}'.", endpointId);
        }

        /// <summary>
        /// Sets what an endpoint addresses: its service, method, and relative path.
        /// </summary>
        /// <param name="collection">The collection holding the endpoint.</param>
        /// <param name="endpointId">An existing endpoint ID.</param>
        /// <param name="serviceId">
        /// Owning service, or empty to inherit the collection's default.
        /// </param>
        /// <param name="method">HTTP method.</param>
        /// <param name="relativePath">
        /// Path relative to the service origin. Never absolute — the origin comes from the service's
        /// binding for whichever environment the call targets, which is what makes one template usable
        /// everywhere.
        /// </param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetEndpointRoute(
            NetworkEndpointCollection collection,
            string endpointId,
            string serviceId,
            HttpMethod method,
            string relativePath)
        {
            if (!TryFindEndpoint(collection, endpointId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            if (!string.IsNullOrEmpty(serviceId) && _catalog.FindService(serviceId) == null)
                return NetworkAuthoringResult.Fail($"No service '{serviceId}' in this catalog.");

            if (!NetworkOrigin.TryJoin("https://validation.invalid", relativePath, out _, out string pathError))
                return NetworkAuthoringResult.Fail(pathError);

            element.FindPropertyRelative(FieldServiceId).stringValue = serviceId ?? string.Empty;
            element.FindPropertyRelative(FieldMethod).enumValueIndex = (int)method;
            element.FindPropertyRelative(FieldRelativePath).stringValue = relativePath ?? string.Empty;

            Apply(serialized, "Set Network Endpoint Route");
            return NetworkAuthoringResult.Ok(
                $"Endpoint '{endpointId}' now addresses {method} {relativePath}.", endpointId);
        }

        /// <summary>
        /// Sets an endpoint's policy override.
        /// </summary>
        /// <param name="collection">The collection holding the endpoint.</param>
        /// <param name="endpointId">An existing endpoint ID.</param>
        /// <param name="policyProfileId">
        /// An existing policy profile ID, or empty to inherit the service's policy.
        /// </param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetEndpointPolicyProfile(
            NetworkEndpointCollection collection,
            string endpointId,
            string policyProfileId)
        {
            if (!TryFindEndpoint(collection, endpointId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            if (!string.IsNullOrEmpty(policyProfileId) && _catalog.FindPolicyProfile(policyProfileId) == null)
                return NetworkAuthoringResult.Fail($"No policy profile '{policyProfileId}' in this catalog.");

            element.FindPropertyRelative(FieldPolicyProfileId).stringValue = policyProfileId ?? string.Empty;

            Apply(serialized, "Set Network Endpoint Policy");

            return NetworkAuthoringResult.Ok(
                string.IsNullOrEmpty(policyProfileId)
                    ? $"Endpoint '{endpointId}' now inherits its service's policy."
                    : $"Endpoint '{endpointId}' now applies policy '{policyProfileId}'.",
                endpointId);
        }

        /// <summary>
        /// Sets an endpoint's mutation classification and idempotency requirement.
        /// </summary>
        /// <param name="collection">The collection holding the endpoint.</param>
        /// <param name="endpointId">An existing endpoint ID.</param>
        /// <param name="mutationClass">What calling this endpoint does.</param>
        /// <param name="requiresIdempotencyKey">Whether a caller must supply an idempotency key.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// Not cosmetic: the classification drives retry eligibility and the request console's production
        /// confirmation. Marking a destructive call <c>Safe</c> makes it retryable and removes that
        /// confirmation, so widening it is reported.
        /// </remarks>
        public NetworkAuthoringResult SetEndpointSafety(
            NetworkEndpointCollection collection,
            string endpointId,
            NetworkMutationClass mutationClass,
            bool requiresIdempotencyKey)
        {
            if (!TryFindEndpoint(collection, endpointId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldMutationClass).enumValueIndex = (int)mutationClass;
            element.FindPropertyRelative(FieldRequiresIdempotencyKey).boolValue = requiresIdempotencyKey;

            Apply(serialized, "Set Network Endpoint Safety");

            string note = mutationClass == NetworkMutationClass.Safe
                ? " Marked safe, so it becomes retryable and the console stops asking for confirmation in " +
                  "production."
                : string.Empty;

            return NetworkAuthoringResult.Ok(
                $"Endpoint '{endpointId}' is now {mutationClass}.{note}", endpointId);
        }

        /// <summary>
        /// Sets an endpoint's documentation — description and tags.
        /// </summary>
        /// <param name="collection">The collection holding the endpoint.</param>
        /// <param name="endpointId">An existing endpoint ID.</param>
        /// <param name="description">Free text, or empty to clear.</param>
        /// <param name="tags">Grouping tags.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetEndpointDocumentation(
            NetworkEndpointCollection collection,
            string endpointId,
            string description,
            IReadOnlyList<string> tags)
        {
            if (!TryFindEndpoint(collection, endpointId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldDescription).stringValue = description ?? string.Empty;
            WriteStringList(element.FindPropertyRelative(FieldTags), CleanStrings(tags));

            Apply(serialized, "Set Network Endpoint Documentation");
            return NetworkAuthoringResult.Ok($"Updated documentation on '{endpointId}'.", endpointId);
        }

        /// <summary>
        /// Sets an endpoint's request body and expected response.
        /// </summary>
        /// <param name="collection">The collection holding the endpoint.</param>
        /// <param name="endpointId">An existing endpoint ID.</param>
        /// <param name="bodyType">How the request body is encoded.</param>
        /// <param name="requestBodyExample">
        /// An authoring example. This asset is committed, so it must never hold real credentials or
        /// customer data.
        /// </param>
        /// <param name="expectedResponseType">How the response is decoded.</param>
        /// <param name="responseTypeName">The response model's type name, or empty.</param>
        /// <returns>The outcome.</returns>
        public NetworkAuthoringResult SetEndpointBody(
            NetworkEndpointCollection collection,
            string endpointId,
            BodyType bodyType,
            string requestBodyExample,
            ResponseType expectedResponseType,
            string responseTypeName)
        {
            if (!TryFindEndpoint(collection, endpointId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            element.FindPropertyRelative(FieldBodyType).enumValueIndex = (int)bodyType;
            element.FindPropertyRelative(FieldRequestBodyExample).stringValue =
                requestBodyExample ?? string.Empty;
            element.FindPropertyRelative(FieldExpectedResponseType).enumValueIndex = (int)expectedResponseType;
            element.FindPropertyRelative(FieldResponseTypeName).stringValue =
                responseTypeName?.Trim() ?? string.Empty;

            Apply(serialized, "Set Network Endpoint Body");
            return NetworkAuthoringResult.Ok($"Updated the body and response on '{endpointId}'.", endpointId);
        }

        /// <summary>
        /// Which of an endpoint's three parameter lists a call addresses.
        /// </summary>
        public enum EndpointParameterKind
        {
            /// <summary>Parameters substituted into the relative path.</summary>
            Path,

            /// <summary>Parameters appended as a query string.</summary>
            Query,

            /// <summary>Parameters sent as request headers.</summary>
            Header,
        }

        /// <summary>
        /// Replaces one of an endpoint's parameter lists.
        /// </summary>
        /// <param name="collection">The collection holding the endpoint.</param>
        /// <param name="endpointId">An existing endpoint ID.</param>
        /// <param name="kind">Which list to replace.</param>
        /// <param name="parameters">The parameters.</param>
        /// <returns>The outcome. Nothing is written when a parameter is unnamed.</returns>
        /// <remarks>
        /// A parameter marked sensitive is stored without its default value. A default for an API-key
        /// parameter is exactly the kind of thing that turns out to be a real key someone pasted in, and
        /// this asset is committed.
        /// </remarks>
        public NetworkAuthoringResult SetEndpointParameters(
            NetworkEndpointCollection collection,
            string endpointId,
            EndpointParameterKind kind,
            IReadOnlyList<NetworkEndpointImport.Parameter> parameters)
        {
            if (!TryFindEndpoint(collection, endpointId, out var serialized, out var element, out string error))
                return NetworkAuthoringResult.Fail(error);

            var cleaned = new List<NetworkEndpointImport.Parameter>();
            if (parameters != null)
            {
                foreach (var parameter in parameters)
                {
                    if (parameter == null) continue;

                    if (string.IsNullOrWhiteSpace(parameter.Name))
                        return NetworkAuthoringResult.Fail("A parameter needs a name.");

                    cleaned.Add(parameter);
                }
            }

            string field = kind switch
            {
                EndpointParameterKind.Path => FieldPathParameters,
                EndpointParameterKind.Query => FieldQueryParameters,
                _ => FieldHeaderParameters,
            };

            WriteParameters(element.FindPropertyRelative(field), cleaned);

            Apply(serialized, "Set Network Endpoint Parameters");
            return NetworkAuthoringResult.Ok(
                $"Endpoint '{endpointId}' now declares {cleaned.Count} {kind} parameter(s).", endpointId);
        }

        /// <summary>
        /// Renames an endpoint's stable ID and re-points every reference to it.
        /// </summary>
        /// <param name="collection">The collection holding the endpoint.</param>
        /// <param name="oldId">The current endpoint ID.</param>
        /// <param name="newId">The replacement ID.</param>
        /// <returns>The outcome, listing each reference that was rewritten.</returns>
        /// <remarks>
        /// Endpoint IDs are unique catalog-wide, not per collection, so uniqueness is checked across every
        /// collection. Applied as one Undo step spanning the catalog and the owning collection.
        /// </remarks>
        public NetworkAuthoringResult RenameEndpointId(
            NetworkEndpointCollection collection,
            string oldId,
            string newId)
        {
            if (collection == null)
                return NetworkAuthoringResult.Fail("No endpoint collection was supplied.");

            if (collection.FindEndpoint(oldId) == null)
                return NetworkAuthoringResult.Fail($"No endpoint '{oldId}' in '{collection.DisplayName}'.");

            if (!NetworkIds.IsValid(newId, out string idError))
                return NetworkAuthoringResult.Fail(idError);

            var index = new NetworkCatalogIndex(_catalog);
            if (index.Endpoints.ContainsKey(newId))
                return NetworkAuthoringResult.Fail($"An endpoint '{newId}' already exists in this catalog.");

            var affected = new List<string>();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Rename Network Endpoint ID");

            var collectionSerialized = new SerializedObject(collection);
            var element = FindElementById(collectionSerialized.FindProperty(FieldEndpoints), FieldId, oldId);

            if (element == null)
            {
                return NetworkAuthoringResult.Fail(
                    $"Could not resolve endpoint '{oldId}' in the serialized collection.");
            }

            element.FindPropertyRelative(FieldId).stringValue = newId;
            Apply(collectionSerialized, "Rename Network Endpoint ID");

            // A service's health endpoint is the one place outside a collection that names an endpoint.
            var serialized = new SerializedObject(_catalog);
            var services = serialized.FindProperty(FieldServices);
            bool changed = false;

            for (int s = 0; s < services.arraySize; s++)
            {
                var service = services.GetArrayElementAtIndex(s);
                var health = service.FindPropertyRelative(FieldHealthEndpointId);
                if (health.stringValue != oldId) continue;

                health.stringValue = newId;
                affected.Add($"health endpoint on service '{service.FindPropertyRelative(FieldId).stringValue}'");
                changed = true;
            }

            if (changed)
                Apply(serialized, "Rename Network Endpoint ID");

            Undo.CollapseUndoOperations(undoGroup);

            return NetworkAuthoringResult.Ok(
                $"Renamed endpoint '{oldId}' to '{newId}' and updated {affected.Count} reference(s).",
                newId,
                affected);
        }

        /// <summary>
        /// Removes an endpoint from a collection.
        /// </summary>
        /// <param name="collection">The collection holding the endpoint.</param>
        /// <param name="endpointId">The endpoint to delete.</param>
        /// <returns>The outcome, listing each reference left pointing at nothing.</returns>
        /// <remarks>
        /// A service that health-checked through this endpoint has that reference cleared, so the
        /// deletion cannot leave a service pointing at an endpoint that no longer exists.
        /// </remarks>
        public NetworkAuthoringResult DeleteEndpoint(
            NetworkEndpointCollection collection,
            string endpointId)
        {
            if (collection == null)
                return NetworkAuthoringResult.Fail("No endpoint collection was supplied.");

            if (collection.FindEndpoint(endpointId) == null)
                return NetworkAuthoringResult.Fail($"No endpoint '{endpointId}' in '{collection.DisplayName}'.");

            var affected = new List<string>();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Delete Network Endpoint");

            var collectionSerialized = new SerializedObject(collection);
            RemoveElementById(collectionSerialized.FindProperty(FieldEndpoints), FieldId, endpointId);
            Apply(collectionSerialized, "Delete Network Endpoint");

            var serialized = new SerializedObject(_catalog);
            var services = serialized.FindProperty(FieldServices);
            bool changed = false;

            for (int s = 0; s < services.arraySize; s++)
            {
                var service = services.GetArrayElementAtIndex(s);
                var health = service.FindPropertyRelative(FieldHealthEndpointId);
                if (health.stringValue != endpointId) continue;

                health.stringValue = string.Empty;
                affected.Add($"health endpoint on service '{service.FindPropertyRelative(FieldId).stringValue}'");
                changed = true;
            }

            if (changed)
                Apply(serialized, "Delete Network Endpoint");

            Undo.CollapseUndoOperations(undoGroup);

            return NetworkAuthoringResult.Ok(
                $"Deleted endpoint '{endpointId}' and cleared {affected.Count} reference(s).",
                endpointId,
                affected);
        }

        // ---- Shared resolution and write helpers

        /// <summary>
        /// Resolves a catalog list element by ID, with a reason when it is absent.
        /// </summary>
        /// <param name="listField">The catalog's serialized list field.</param>
        /// <param name="id">The entity ID to find.</param>
        /// <param name="label">The entity kind, for the failure message.</param>
        /// <param name="serialized">The catalog's serialized object on success.</param>
        /// <param name="element">The matching element on success.</param>
        /// <param name="error">The reason on failure.</param>
        /// <returns><c>false</c> when the entity is absent.</returns>
        private bool TryFindEntity(
            string listField,
            string id,
            string label,
            out SerializedObject serialized,
            out SerializedProperty element,
            out string error)
        {
            serialized = null;
            element = null;

            if (string.IsNullOrEmpty(id))
            {
                error = $"No {label} was named.";
                return false;
            }

            serialized = new SerializedObject(_catalog);
            element = FindElementById(serialized.FindProperty(listField), FieldId, id);

            if (element == null)
            {
                serialized = null;
                error = $"No {label} '{id}' in this catalog.";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Resolves a service's binding for an environment, creating it when absent.
        /// </summary>
        /// <param name="serviceId">The service to bind.</param>
        /// <param name="environmentId">The environment to bind it in.</param>
        /// <param name="serialized">The catalog's serialized object on success.</param>
        /// <param name="binding">The binding element on success.</param>
        /// <param name="created">Whether the binding was newly appended.</param>
        /// <param name="error">The reason on failure.</param>
        /// <returns><c>false</c> when the service or environment is absent.</returns>
        private bool TryFindBinding(
            string serviceId,
            string environmentId,
            out SerializedObject serialized,
            out SerializedProperty binding,
            out bool created,
            out string error)
        {
            binding = null;
            created = false;

            if (!TryFindEntity(FieldServices, serviceId, "service", out serialized, out var service, out error))
                return false;

            if (_catalog.FindEnvironment(environmentId) == null)
            {
                serialized = null;
                error = $"No environment '{environmentId}' in this catalog.";
                return false;
            }

            var bindings = service.FindPropertyRelative(FieldBindings);
            binding = FindElementById(bindings, FieldEnvironmentId, environmentId);

            created = binding == null;
            if (created)
            {
                binding = AppendElement(bindings);
                binding.FindPropertyRelative(FieldEnvironmentId).stringValue = environmentId;

                // A newly appended element inherits the previous one's values, so every origin is cleared
                // rather than silently copied from whichever environment happened to be last.
                binding.FindPropertyRelative(FieldHttpOrigin).stringValue = string.Empty;
                binding.FindPropertyRelative(FieldSseOrigin).stringValue = string.Empty;
                binding.FindPropertyRelative(FieldWebSocketOrigin).stringValue = string.Empty;
                binding.FindPropertyRelative(FieldSocketIoOrigin).stringValue = string.Empty;
                binding.FindPropertyRelative(FieldSocketIoPath).stringValue = string.Empty;
                binding.FindPropertyRelative(FieldRegionLabel).stringValue = string.Empty;
                binding.FindPropertyRelative(FieldEnabled).boolValue = true;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Resolves an endpoint element inside a collection.
        /// </summary>
        /// <param name="collection">The collection to search.</param>
        /// <param name="endpointId">The endpoint ID to find.</param>
        /// <param name="serialized">The collection's serialized object on success.</param>
        /// <param name="element">The matching element on success.</param>
        /// <param name="error">The reason on failure.</param>
        /// <returns><c>false</c> when the collection or endpoint is absent.</returns>
        private static bool TryFindEndpoint(
            NetworkEndpointCollection collection,
            string endpointId,
            out SerializedObject serialized,
            out SerializedProperty element,
            out string error)
        {
            serialized = null;
            element = null;

            if (collection == null)
            {
                error = "No endpoint collection was supplied.";
                return false;
            }

            if (collection.FindEndpoint(endpointId) == null)
            {
                error = $"No endpoint '{endpointId}' in '{collection.DisplayName}'.";
                return false;
            }

            serialized = new SerializedObject(collection);
            element = FindElementById(serialized.FindProperty(FieldEndpoints), FieldId, endpointId);

            if (element == null)
            {
                serialized = null;
                error = $"Could not resolve endpoint '{endpointId}' in the serialized collection.";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Maps a single protocol to the binding field holding its origin.
        /// </summary>
        /// <param name="protocol">The protocol. Must be exactly one flag.</param>
        /// <param name="originField">The serialized field name on success.</param>
        /// <param name="allowWebSocketSchemes">Whether <c>ws</c>/<c>wss</c> are valid for it.</param>
        /// <param name="error">The reason on failure.</param>
        /// <returns><c>false</c> when the protocol is None or a flag combination.</returns>
        private static bool TryResolveOriginField(
            NetworkProtocols protocol,
            out string originField,
            out bool allowWebSocketSchemes,
            out string error)
        {
            switch (protocol)
            {
                case NetworkProtocols.Http:
                    originField = FieldHttpOrigin;
                    allowWebSocketSchemes = false;
                    error = null;
                    return true;

                // SSE is delivered over HTTP, so a wss origin is a mistake rather than an alternative.
                case NetworkProtocols.ServerSentEvents:
                    originField = FieldSseOrigin;
                    allowWebSocketSchemes = false;
                    error = null;
                    return true;

                case NetworkProtocols.WebSocket:
                    originField = FieldWebSocketOrigin;
                    allowWebSocketSchemes = true;
                    error = null;
                    return true;

                case NetworkProtocols.SocketIO:
                    originField = FieldSocketIoOrigin;
                    allowWebSocketSchemes = true;
                    error = null;
                    return true;

                default:
                    originField = null;
                    allowWebSocketSchemes = false;
                    error = protocol == NetworkProtocols.None
                        ? "Name the protocol whose origin to set."
                        : $"'{protocol}' names more than one protocol. Set one origin per call.";
                    return false;
            }
        }

        /// <summary>Trims, drops empties, and de-duplicates an authored string list.</summary>
        /// <param name="values">The authored values, possibly <c>null</c>.</param>
        /// <returns>A cleaned list, never <c>null</c>.</returns>
        /// <remarks>
        /// Duplicates are dropped because every list this serves is a set — two identical host patterns or
        /// scopes mean the same thing as one, and keeping both makes a read-out look like a mistake.
        /// </remarks>
        private static List<string> CleanStrings(IReadOnlyList<string> values)
        {
            var cleaned = new List<string>();
            if (values == null) return cleaned;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;

                string trimmed = value.Trim();
                if (seen.Add(trimmed))
                    cleaned.Add(trimmed);
            }
            return cleaned;
        }

        /// <summary>Replaces a serialized string list with a cleaned set of values.</summary>
        private static void WriteStringList(SerializedProperty list, List<string> values)
        {
            list.ClearArray();
            for (int i = 0; i < values.Count; i++)
            {
                list.InsertArrayElementAtIndex(i);
                list.GetArrayElementAtIndex(i).stringValue = values[i];
            }
        }

        /// <summary>Header names a credential profile owns, which a service must not author by hand.</summary>
        private static readonly string[] CredentialHeaderNames =
        {
            "authorization", "proxy-authorization", "cookie", "x-api-key", "api-key",
        };

        /// <summary>Whether a header name is one a credential profile is responsible for.</summary>
        private static bool IsCredentialHeader(string name)
        {
            foreach (string reserved in CredentialHeaderNames)
            {
                if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a provider key looks like a secret value rather than a name for one.
        /// </summary>
        /// <remarks>
        /// Shallow and deliberately so: it catches the shapes people actually paste — a bearer token, a
        /// JWT, a long high-entropy string — without trying to be a general secret scanner. A false
        /// negative costs nothing here, since the field is a lookup key either way.
        /// </remarks>
        private static bool LooksLikeSecretValue(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("eyJ", StringComparison.Ordinal))
            {
                return true;
            }

            // A lookup key is a name someone typed; anything this long without a separator is a value.
            return key.Length >= 40 &&
                   key.IndexOf(' ') < 0 &&
                   key.IndexOf('_') < 0 &&
                   key.IndexOf('-') < 0 &&
                   key.IndexOf('.') < 0;
        }
    }
}
