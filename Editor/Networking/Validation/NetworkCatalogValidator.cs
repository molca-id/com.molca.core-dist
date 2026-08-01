using System;
using System.Collections.Generic;
using Molca.Networking.Configuration;

namespace Molca.Editor.Networking.Validation
{
    /// <summary>
    /// The single validator for a <see cref="NetworkCatalog"/>. Hub Diagnostics, Doctor, the build
    /// gate, MCP tools, and tests all call this — there is no second set of networking rules
    /// anywhere.
    /// </summary>
    /// <remarks>
    /// Pure and deterministic: no Unity asset mutation, no I/O, no dependency on a Hub window, and the
    /// same findings in the same order for the same catalog. That is what makes it usable from batch
    /// mode and assertable in tests.
    /// </remarks>
    public static class NetworkCatalogValidator
    {
        // Finding codes are API — Doctor, MCP, and tests match on them. Add, never rename.
        /// <summary>No catalog asset was found in the project.</summary>
        public const string CodeCatalogMissing = "network.catalog.missing";
        /// <summary>The catalog's serialized schema predates this framework version.</summary>
        public const string CodeSchemaMigrationRequired = "network.catalog.schema-migration-required";
        /// <summary>The catalog names no default environment.</summary>
        public const string CodeDefaultEnvironmentMissing = "network.catalog.default-environment-missing";
        /// <summary>The catalog's default environment ID matches no environment.</summary>
        public const string CodeDefaultEnvironmentUnknown = "network.catalog.default-environment-unknown";
        /// <summary>A referenced policy profile does not exist.</summary>
        public const string CodePolicyProfileUnknown = "network.policy.unknown-reference";
        /// <summary>A referenced credential profile does not exist.</summary>
        public const string CodeCredentialProfileUnknown = "network.credential.unknown-reference";
        /// <summary>The legacy global-auth transition flag is enabled.</summary>
        public const string CodeLegacyAuthFlagEnabled = "network.catalog.legacy-auth-flag-enabled";
        /// <summary>An identifier does not satisfy the kebab-case rules.</summary>
        public const string CodeIdInvalid = "network.id.invalid";
        /// <summary>An identifier appears more than once within its kind.</summary>
        public const string CodeIdDuplicate = "network.id.duplicate";
        /// <summary>An author-created identifier uses the framework-reserved prefix.</summary>
        public const string CodeIdReserved = "network.id.reserved-prefix";
        /// <summary>A service declares no protocol.</summary>
        public const string CodeServiceNoProtocol = "network.service.no-protocol";
        /// <summary>A service has no binding for an environment.</summary>
        public const string CodeBindingMissing = "network.service.binding-missing";
        /// <summary>A binding names an environment that does not exist.</summary>
        public const string CodeBindingUnknownEnvironment = "network.service.binding-unknown-environment";
        /// <summary>A service has two bindings for the same environment.</summary>
        public const string CodeBindingDuplicate = "network.service.binding-duplicate";
        /// <summary>A binding is authored but disabled.</summary>
        public const string CodeBindingDisabled = "network.service.binding-disabled";
        /// <summary>A binding supplies no origin for a protocol the service declares.</summary>
        public const string CodeOriginMissing = "network.binding.origin-missing";
        /// <summary>An authored origin is not a usable absolute URI.</summary>
        public const string CodeOriginInvalid = "network.binding.origin-invalid";
        /// <summary>An origin uses an unencrypted scheme where the environment requires encryption.</summary>
        public const string CodeOriginInsecure = "network.binding.origin-insecure";
        /// <summary>An origin's host is outside the service's own allowed-host rules.</summary>
        public const string CodeOriginHostNotAllowed = "network.binding.origin-host-not-allowed";
        /// <summary>An allowed-host pattern is malformed or too broad.</summary>
        public const string CodeHostPatternInvalid = "network.host-pattern.invalid";
        /// <summary>A credential profile's scope permits nothing, so it can never attach.</summary>
        public const string CodeCredentialScopeEmpty = "network.credential.scope-empty";
        /// <summary>A service uses a credential whose scope excludes that service.</summary>
        public const string CodeCredentialServiceNotScoped = "network.credential.service-not-scoped";
        /// <summary>A bound origin's host is outside the credential's host scope.</summary>
        public const string CodeCredentialHostNotScoped = "network.credential.host-not-scoped";
        /// <summary>A production service has no usable credential source.</summary>
        public const string CodeCredentialProductionSourceMissing = "network.credential.production-source-missing";
        /// <summary>A serialized field looks like it contains a secret.</summary>
        public const string CodeSecretSuspected = "network.secret.suspected-in-asset";
        /// <summary>An endpoint names a service that does not exist.</summary>
        public const string CodeEndpointUnknownService = "network.endpoint.unknown-service";
        /// <summary>An endpoint's relative path is absolute or would escape the service origin.</summary>
        public const string CodeEndpointPathInvalid = "network.endpoint.path-invalid";
        /// <summary>An endpoint's declared path parameters do not match its path placeholders.</summary>
        public const string CodeEndpointParameterMismatch = "network.endpoint.parameter-mismatch";
        /// <summary>An endpoint needs a protocol its service does not declare.</summary>
        public const string CodeEndpointProtocolUnsupported = "network.endpoint.protocol-unsupported";
        /// <summary>An endpoint collection is referenced but is <c>null</c> or unregistered.</summary>
        public const string CodeCollectionReferenceBroken = "network.collection.reference-broken";
        /// <summary>A policy profile's timeout or retry values contradict each other.</summary>
        public const string CodePolicyInconsistent = "network.policy.inconsistent";
        /// <summary>A policy disables TLS validation.</summary>
        public const string CodePolicyTlsDisabled = "network.policy.tls-validation-disabled";

        /// <summary>
        /// Validates a catalog.
        /// </summary>
        /// <param name="catalog">The catalog to validate. <c>null</c> yields a single missing-catalog finding.</param>
        /// <returns>A report; never <c>null</c>.</returns>
        public static NetworkValidationReport Validate(NetworkCatalog catalog)
        {
            var findings = new List<NetworkValidationFinding>();

            if (catalog == null)
            {
                findings.Add(new NetworkValidationFinding(
                    NetworkValidationSeverity.Error,
                    CodeCatalogMissing,
                    NetworkErrorCategory.Configuration,
                    NetworkValidationEntityKind.Catalog,
                    null,
                    "No network catalog was found in this project.",
                    "Create one from the Network workspace, or run the legacy networking scan."));

                return new NetworkValidationReport(null, findings);
            }

            var index = new NetworkCatalogIndex(catalog);

            ValidateCatalog(catalog, index, findings);
            ValidateDuplicates(catalog, index, findings);
            ValidateEnvironments(catalog, index, findings);
            ValidatePolicyProfiles(catalog, findings);
            ValidateCredentialProfiles(catalog, index, findings);
            ValidateServices(catalog, index, findings);
            ValidateEndpoints(catalog, index, findings);

            return new NetworkValidationReport(catalog, findings);
        }

        // ---- Catalog level

        private static void ValidateCatalog(
            NetworkCatalog catalog,
            NetworkCatalogIndex index,
            List<NetworkValidationFinding> findings)
        {
            if (catalog.RequiresSchemaMigration)
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodeSchemaMigrationRequired,
                    NetworkErrorCategory.Configuration,
                    NetworkValidationEntityKind.Catalog,
                    catalog.name,
                    $"Catalog schema is version {catalog.SchemaVersion}; this framework authors version {NetworkCatalog.CurrentSchemaVersion}.",
                    "Run the catalog schema migration from the Network workspace.",
                    catalog));
            }

            if (string.IsNullOrEmpty(catalog.DefaultEnvironmentId))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodeDefaultEnvironmentMissing,
                    NetworkErrorCategory.Configuration,
                    NetworkValidationEntityKind.Catalog,
                    catalog.name,
                    "The catalog names no default environment, so call sites that do not name one cannot resolve.",
                    "Set a default environment on the catalog.",
                    catalog));
            }
            else if (!index.Environments.ContainsKey(catalog.DefaultEnvironmentId))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodeDefaultEnvironmentUnknown,
                    NetworkErrorCategory.Configuration,
                    NetworkValidationEntityKind.Catalog,
                    catalog.name,
                    $"Default environment '{catalog.DefaultEnvironmentId}' matches no environment in this catalog.",
                    "Point the default at an existing environment, or add the missing one.",
                    catalog));
            }

            if (!string.IsNullOrEmpty(catalog.DefaultPolicyProfileId) &&
                !index.PolicyProfiles.ContainsKey(catalog.DefaultPolicyProfileId))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodePolicyProfileUnknown,
                    NetworkErrorCategory.Configuration,
                    NetworkValidationEntityKind.Catalog,
                    catalog.name,
                    $"Default policy profile '{catalog.DefaultPolicyProfileId}' does not exist.",
                    "Create the profile or clear the reference to fall back to the library defaults.",
                    catalog));
            }

            if (catalog.AllowLegacyGlobalAuthOnExternalUrls)
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Warning,
                    CodeLegacyAuthFlagEnabled,
                    NetworkErrorCategory.SecurityPolicy,
                    NetworkValidationEntityKind.Catalog,
                    catalog.name,
                    "Legacy global authentication is still applied to unrelated full URLs. Credentials can reach hosts that were never approved.",
                    "Migrate the remaining full-URL call sites to routes, then clear this transition flag.",
                    catalog));
            }
        }

        private static void ValidateDuplicates(
            NetworkCatalog catalog,
            NetworkCatalogIndex index,
            List<NetworkValidationFinding> findings)
        {
            AddDuplicates(index.DuplicateEnvironmentIds, NetworkValidationEntityKind.Environment, "environment");
            AddDuplicates(index.DuplicateServiceIds, NetworkValidationEntityKind.Service, "service");
            AddDuplicates(index.DuplicatePolicyProfileIds, NetworkValidationEntityKind.PolicyProfile, "policy profile");
            AddDuplicates(index.DuplicateCredentialProfileIds, NetworkValidationEntityKind.CredentialProfile, "credential profile");
            AddDuplicates(index.DuplicateCollectionIds, NetworkValidationEntityKind.EndpointCollection, "endpoint collection");
            AddDuplicates(index.DuplicateEndpointIds, NetworkValidationEntityKind.Endpoint, "endpoint");

            void AddDuplicates(IReadOnlyList<string> ids, NetworkValidationEntityKind kind, string label)
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeIdDuplicate,
                        NetworkErrorCategory.Configuration,
                        kind,
                        ids[i],
                        $"More than one {label} uses the ID '{ids[i]}'. Only the first is reachable.",
                        "Give each entry a unique ID.",
                        catalog));
                }
            }
        }

        // ---- Environments

        private static void ValidateEnvironments(
            NetworkCatalog catalog,
            NetworkCatalogIndex index,
            List<NetworkValidationFinding> findings)
        {
            var environments = catalog.Environments;
            if (environments == null) return;

            for (int i = 0; i < environments.Count; i++)
            {
                var environment = environments[i];
                if (environment == null) continue;

                ValidateId(
                    environment.Id, NetworkValidationEntityKind.Environment,
                    $"environment #{i}", catalog, findings);

                if (!string.IsNullOrEmpty(environment.PolicyProfileId) &&
                    !index.PolicyProfiles.ContainsKey(environment.PolicyProfileId))
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodePolicyProfileUnknown,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.Environment,
                        environment.Id,
                        $"Environment '{environment.Id}' references policy profile '{environment.PolicyProfileId}', which does not exist.",
                        "Create the profile or clear the override.",
                        catalog,
                        environment.Id));
                }
            }
        }

        // ---- Policy profiles

        private static void ValidatePolicyProfiles(NetworkCatalog catalog, List<NetworkValidationFinding> findings)
        {
            var profiles = catalog.PolicyProfiles;
            if (profiles == null) return;

            for (int i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                if (profile == null) continue;

                ValidateId(
                    profile.Id, NetworkValidationEntityKind.PolicyProfile,
                    $"policy profile #{i}", catalog, findings);

                // An attempt budget larger than the whole-send budget can never be reached, so the
                // authored attempt timeout silently does nothing.
                if (profile.OverallTimeoutSeconds > 0f &&
                    profile.AttemptTimeoutSeconds > profile.OverallTimeoutSeconds)
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodePolicyInconsistent,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.PolicyProfile,
                        profile.Id,
                        $"Attempt timeout ({profile.AttemptTimeoutSeconds}s) exceeds the overall timeout ({profile.OverallTimeoutSeconds}s), so it can never elapse.",
                        "Lower the attempt timeout below the overall timeout.",
                        catalog));
                }

                if (profile.RetryEnabled && profile.MaxRetries > 0 && profile.RetryBaseDelaySeconds <= 0f)
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Warning,
                        CodePolicyInconsistent,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.PolicyProfile,
                        profile.Id,
                        "Retry is enabled with a zero base delay, so failed attempts repeat immediately with no backoff.",
                        "Set a base delay, or disable retry.",
                        catalog));
                }

                if (profile.CircuitFailureThreshold > 0 && profile.CircuitResetSeconds <= 0f)
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodePolicyInconsistent,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.PolicyProfile,
                        profile.Id,
                        "The circuit breaker has a failure threshold but a zero reset window, so it would never close again.",
                        "Set a reset window, or clear the failure threshold.",
                        catalog));
                }

                if (profile.CacheMode == NetworkCacheMode.FixedTtl && profile.CacheTtlSeconds <= 0f)
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Warning,
                        CodePolicyInconsistent,
                        NetworkErrorCategory.Cache,
                        NetworkValidationEntityKind.PolicyProfile,
                        profile.Id,
                        "Fixed-TTL caching is selected with a zero TTL, so nothing is ever served from cache.",
                        "Set a TTL, or switch the cache mode.",
                        catalog));
                }

                if (!profile.ValidateTlsCertificate)
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Warning,
                        CodePolicyTlsDisabled,
                        NetworkErrorCategory.SecurityPolicy,
                        NetworkValidationEntityKind.PolicyProfile,
                        profile.Id,
                        $"Policy '{profile.Id}' disables TLS certificate validation. Production environments override this, but any other environment using it is exposed.",
                        "Limit this profile to local development, or re-enable validation.",
                        catalog));
                }
            }
        }

        // ---- Credential profiles

        private static void ValidateCredentialProfiles(
            NetworkCatalog catalog,
            NetworkCatalogIndex index,
            List<NetworkValidationFinding> findings)
        {
            var profiles = catalog.CredentialProfiles;
            if (profiles == null) return;

            for (int i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                if (profile == null) continue;

                ValidateId(
                    profile.Id, NetworkValidationEntityKind.CredentialProfile,
                    $"credential profile #{i}", catalog, findings);

                if (profile.IsAnonymous)
                    continue;

                // Scope lists deny when empty, so an unauthored scope is a profile that can never
                // attach — almost always an unfinished edit rather than an intention.
                if (profile.AllowedServiceIds.Count == 0 || profile.AllowedHostPatterns.Count == 0)
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeCredentialScopeEmpty,
                        NetworkErrorCategory.SecurityPolicy,
                        NetworkValidationEntityKind.CredentialProfile,
                        profile.Id,
                        $"Credential '{profile.Id}' has no allowed {(profile.AllowedServiceIds.Count == 0 ? "services" : "hosts")}, so it will never be attached to a request.",
                        "Name the services and hosts this credential may reach.",
                        catalog));
                }

                for (int h = 0; h < profile.AllowedHostPatterns.Count; h++)
                {
                    if (!NetworkHostRule.TryNormalizePattern(profile.AllowedHostPatterns[h], out _, out string error))
                    {
                        findings.Add(Finding(
                            NetworkValidationSeverity.Error,
                            CodeHostPatternInvalid,
                            NetworkErrorCategory.SecurityPolicy,
                            NetworkValidationEntityKind.CredentialProfile,
                            profile.Id,
                            $"Credential '{profile.Id}' host pattern is unusable: {error}",
                            "Use a bare host, or a '*.domain.tld' wildcard covering at least two labels.",
                            catalog));
                    }
                }

                for (int s = 0; s < profile.AllowedServiceIds.Count; s++)
                {
                    string serviceId = profile.AllowedServiceIds[s];
                    if (!string.IsNullOrEmpty(serviceId) && !index.Services.ContainsKey(serviceId))
                    {
                        findings.Add(Finding(
                            NetworkValidationSeverity.Warning,
                            CodeEndpointUnknownService,
                            NetworkErrorCategory.Configuration,
                            NetworkValidationEntityKind.CredentialProfile,
                            profile.Id,
                            $"Credential '{profile.Id}' is scoped to service '{serviceId}', which does not exist.",
                            "Remove the stale entry, or add the service.",
                            catalog));
                    }
                }

                ScanForSuspectedSecret(
                    profile.ProviderKey, "provider key",
                    NetworkValidationEntityKind.CredentialProfile, profile.Id, catalog, findings);
            }
        }

        // ---- Services and bindings

        private static void ValidateServices(
            NetworkCatalog catalog,
            NetworkCatalogIndex index,
            List<NetworkValidationFinding> findings)
        {
            var services = catalog.Services;
            if (services == null) return;

            for (int i = 0; i < services.Count; i++)
            {
                var service = services[i];
                if (service == null) continue;

                ValidateId(service.Id, NetworkValidationEntityKind.Service, $"service #{i}", catalog, findings);

                if (service.Protocols == NetworkProtocols.None)
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeServiceNoProtocol,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.Service,
                        service.Id,
                        $"Service '{service.Id}' declares no protocol, so no request to it can resolve.",
                        "Declare at least one protocol.",
                        catalog));
                }

                ValidateServiceReferences(catalog, index, service, findings);
                ValidateServiceHostPatterns(catalog, service, findings);
                ValidateServiceBindings(catalog, index, service, findings);
            }
        }

        private static void ValidateServiceReferences(
            NetworkCatalog catalog,
            NetworkCatalogIndex index,
            NetworkServiceDefinition service,
            List<NetworkValidationFinding> findings)
        {
            if (!string.IsNullOrEmpty(service.PolicyProfileId) &&
                !index.PolicyProfiles.ContainsKey(service.PolicyProfileId))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodePolicyProfileUnknown,
                    NetworkErrorCategory.Configuration,
                    NetworkValidationEntityKind.Service,
                    service.Id,
                    $"Service '{service.Id}' references policy profile '{service.PolicyProfileId}', which does not exist.",
                    "Create the profile or clear the override.",
                    catalog));
            }

            if (!string.IsNullOrEmpty(service.CredentialProfileId))
            {
                if (!index.CredentialProfiles.TryGetValue(service.CredentialProfileId, out var credential))
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeCredentialProfileUnknown,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.Service,
                        service.Id,
                        $"Service '{service.Id}' references credential profile '{service.CredentialProfileId}', which does not exist.",
                        "Create the profile, or make the service anonymous.",
                        catalog));
                }
                else if (!credential.IsAnonymous && !credential.AllowsService(service.Id))
                {
                    // The service asks for the credential, but the credential's own scope excludes it.
                    // At runtime the request goes out anonymous, which is confusing to debug from the
                    // call site — so it is an error here, not a warning.
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeCredentialServiceNotScoped,
                        NetworkErrorCategory.SecurityPolicy,
                        NetworkValidationEntityKind.Service,
                        service.Id,
                        $"Service '{service.Id}' uses credential '{credential.Id}', but that credential's allowed services do not include it. Requests would be sent anonymously.",
                        $"Add '{service.Id}' to the credential's allowed services.",
                        catalog));
                }
            }

            var collections = service.EndpointCollections;
            if (collections == null) return;

            for (int c = 0; c < collections.Count; c++)
            {
                var collection = collections[c];
                if (collection == null)
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Warning,
                        CodeCollectionReferenceBroken,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.Service,
                        service.Id,
                        $"Service '{service.Id}' references an endpoint collection that is missing or was deleted.",
                        "Remove the empty reference, or restore the asset.",
                        catalog));
                    continue;
                }

                // A collection the service uses but the catalog does not list is invisible to
                // Diagnostics, search, and endpoint-ID uniqueness checks.
                if (!string.IsNullOrEmpty(collection.CollectionId) &&
                    !index.Collections.ContainsKey(collection.CollectionId))
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Warning,
                        CodeCollectionReferenceBroken,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.EndpointCollection,
                        collection.CollectionId,
                        $"Collection '{collection.CollectionId}' is used by service '{service.Id}' but is not registered on the catalog.",
                        "Add the collection to the catalog's endpoint collections.",
                        collection));
                }
            }
        }

        private static void ValidateServiceHostPatterns(
            NetworkCatalog catalog,
            NetworkServiceDefinition service,
            List<NetworkValidationFinding> findings)
        {
            var patterns = service.AllowedHostPatterns;
            if (patterns == null) return;

            for (int i = 0; i < patterns.Count; i++)
            {
                if (!NetworkHostRule.TryNormalizePattern(patterns[i], out _, out string error))
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeHostPatternInvalid,
                        NetworkErrorCategory.SecurityPolicy,
                        NetworkValidationEntityKind.Service,
                        service.Id,
                        $"Service '{service.Id}' allowed-host pattern is unusable: {error}",
                        "Use a bare host, or a '*.domain.tld' wildcard covering at least two labels.",
                        catalog));
                }
            }
        }

        private static void ValidateServiceBindings(
            NetworkCatalog catalog,
            NetworkCatalogIndex index,
            NetworkServiceDefinition service,
            List<NetworkValidationFinding> findings)
        {
            var seenEnvironments = new HashSet<string>(StringComparer.Ordinal);
            var bindings = service.Bindings;

            if (bindings != null)
            {
                for (int b = 0; b < bindings.Count; b++)
                {
                    var binding = bindings[b];
                    if (binding == null) continue;

                    if (string.IsNullOrEmpty(binding.EnvironmentId))
                    {
                        findings.Add(Finding(
                            NetworkValidationSeverity.Error,
                            CodeBindingUnknownEnvironment,
                            NetworkErrorCategory.Configuration,
                            NetworkValidationEntityKind.Binding,
                            service.Id,
                            $"Service '{service.Id}' has a binding with no environment ID.",
                            "Select the environment this binding applies to.",
                            catalog));
                        continue;
                    }

                    if (!seenEnvironments.Add(binding.EnvironmentId))
                    {
                        findings.Add(Finding(
                            NetworkValidationSeverity.Error,
                            CodeBindingDuplicate,
                            NetworkErrorCategory.Configuration,
                            NetworkValidationEntityKind.Binding,
                            service.Id,
                            $"Service '{service.Id}' has more than one binding for environment '{binding.EnvironmentId}'. Only the first resolves.",
                            "Delete the duplicate binding.",
                            catalog,
                            binding.EnvironmentId));
                        continue;
                    }

                    if (!index.Environments.TryGetValue(binding.EnvironmentId, out var environment))
                    {
                        findings.Add(Finding(
                            NetworkValidationSeverity.Error,
                            CodeBindingUnknownEnvironment,
                            NetworkErrorCategory.Configuration,
                            NetworkValidationEntityKind.Binding,
                            service.Id,
                            $"Service '{service.Id}' is bound to environment '{binding.EnvironmentId}', which does not exist.",
                            "Remove the binding, or add the environment.",
                            catalog,
                            binding.EnvironmentId));
                        continue;
                    }

                    if (!binding.Enabled)
                    {
                        findings.Add(Finding(
                            NetworkValidationSeverity.Info,
                            CodeBindingDisabled,
                            NetworkErrorCategory.RouteResolution,
                            NetworkValidationEntityKind.Binding,
                            service.Id,
                            $"Service '{service.Id}' is disabled in environment '{binding.EnvironmentId}'. Requests to this route fail with a configuration error rather than falling back.",
                            null,
                            catalog,
                            binding.EnvironmentId));
                    }

                    ValidateBindingOrigins(catalog, service, binding, environment, findings);
                }
            }

            // Report the holes in the matrix. A service legitimately may not exist everywhere, so this
            // is a warning the author can accept — but it is never silent.
            foreach (var environment in index.Environments.Values)
            {
                if (seenEnvironments.Contains(environment.Id))
                    continue;

                findings.Add(Finding(
                    NetworkValidationSeverity.Warning,
                    CodeBindingMissing,
                    NetworkErrorCategory.RouteResolution,
                    NetworkValidationEntityKind.Service,
                    service.Id,
                    $"Service '{service.Id}' has no binding for environment '{environment.Id}'. That route cannot resolve.",
                    "Add a binding, or accept that this service is absent from that environment.",
                    catalog,
                    environment.Id));
            }
        }

        private static void ValidateBindingOrigins(
            NetworkCatalog catalog,
            NetworkServiceDefinition service,
            NetworkServiceBinding binding,
            NetworkEnvironmentProfile environment,
            List<NetworkValidationFinding> findings)
        {
            var allowedHosts = service.ResolveAllowedHosts();
            bool hostsAuthored = service.AllowedHostPatterns != null && service.AllowedHostPatterns.Count > 0;

            foreach (NetworkProtocols protocol in AllProtocols)
            {
                if (!service.Supports(protocol))
                    continue;

                string origin = binding.OriginFor(protocol);

                if (string.IsNullOrWhiteSpace(origin))
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeOriginMissing,
                        NetworkErrorCategory.RouteResolution,
                        NetworkValidationEntityKind.Binding,
                        service.Id,
                        $"Service '{service.Id}' declares {protocol} but supplies no {protocol} origin for environment '{binding.EnvironmentId}'.",
                        $"Author the {protocol} origin, or stop declaring that protocol.",
                        catalog,
                        binding.EnvironmentId));
                    continue;
                }

                bool wsFamily = protocol == NetworkProtocols.WebSocket;
                if (!NetworkOrigin.TryNormalize(origin, wsFamily, out string normalized, out string error))
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeOriginInvalid,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.Binding,
                        service.Id,
                        $"Service '{service.Id}' {protocol} origin for '{binding.EnvironmentId}' is unusable: {error}",
                        null,
                        catalog,
                        binding.EnvironmentId));
                    continue;
                }

                var uri = new Uri(normalized);

                if (environment.RequireSecureTransport && !NetworkOrigin.IsSecureScheme(uri.Scheme))
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeOriginInsecure,
                        NetworkErrorCategory.SecurityPolicy,
                        NetworkValidationEntityKind.Binding,
                        service.Id,
                        $"Environment '{environment.Id}' requires an encrypted scheme, but service '{service.Id}' uses '{uri.Scheme}' for {protocol}.",
                        wsFamily ? "Use wss://." : "Use https://.",
                        catalog,
                        binding.EnvironmentId));
                }

                if (hostsAuthored && !NetworkHostRule.MatchesAny(allowedHosts, uri.Host))
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeOriginHostNotAllowed,
                        NetworkErrorCategory.SecurityPolicy,
                        NetworkValidationEntityKind.Binding,
                        service.Id,
                        $"Host '{uri.Host}' is bound for {protocol} in '{binding.EnvironmentId}' but is not covered by service '{service.Id}' allowed hosts.",
                        $"Add '{uri.Host}' to the service's allowed hosts, or correct the origin.",
                        catalog,
                        binding.EnvironmentId));
                }

                ValidateCredentialHostScope(catalog, service, binding, environment, uri.Host, findings);
            }
        }

        private static void ValidateCredentialHostScope(
            NetworkCatalog catalog,
            NetworkServiceDefinition service,
            NetworkServiceBinding binding,
            NetworkEnvironmentProfile environment,
            string host,
            List<NetworkValidationFinding> findings)
        {
            if (string.IsNullOrEmpty(service.CredentialProfileId))
                return;

            var credential = catalog.FindCredentialProfile(service.CredentialProfileId);
            if (credential == null || credential.IsAnonymous)
                return;

            if (!credential.AllowsHost(host))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodeCredentialHostNotScoped,
                    NetworkErrorCategory.SecurityPolicy,
                    NetworkValidationEntityKind.Binding,
                    service.Id,
                    $"Credential '{credential.Id}' is not scoped to host '{host}', bound for service '{service.Id}' in '{binding.EnvironmentId}'. Requests there would go out anonymously.",
                    $"Add '{host}' to the credential's allowed hosts.",
                    catalog,
                    binding.EnvironmentId));
            }

            if (environment.IsProductionSafetyEnforced &&
                catalog.RequireProductionCredentialSource &&
                credential.ProviderKind == NetworkCredentialProviderKind.EditorSecureStorage)
            {
                // Editor secure storage does not exist in a player, so a production build would ship
                // with no way to obtain this credential.
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodeCredentialProductionSourceMissing,
                    NetworkErrorCategory.Authentication,
                    NetworkValidationEntityKind.CredentialProfile,
                    credential.Id,
                    $"Credential '{credential.Id}' is sourced from editor secure storage but is used by service '{service.Id}' in production environment '{environment.Id}'. A player build has no such source.",
                    "Use a runtime credential source for production, or bind a different credential there.",
                    catalog,
                    environment.Id));
            }
        }

        // ---- Endpoints

        private static void ValidateEndpoints(
            NetworkCatalog catalog,
            NetworkCatalogIndex index,
            List<NetworkValidationFinding> findings)
        {
            var collections = catalog.EndpointCollections;
            if (collections == null) return;

            for (int c = 0; c < collections.Count; c++)
            {
                var collection = collections[c];
                if (collection == null)
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Warning,
                        CodeCollectionReferenceBroken,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.Catalog,
                        catalog.name,
                        $"Endpoint collection #{c} on the catalog is missing or was deleted.",
                        "Remove the empty reference, or restore the asset.",
                        catalog));
                    continue;
                }

                ValidateId(
                    collection.CollectionId, NetworkValidationEntityKind.EndpointCollection,
                    $"collection '{collection.name}'", collection, findings);

                var endpoints = collection.Endpoints;
                if (endpoints == null) continue;

                for (int e = 0; e < endpoints.Count; e++)
                {
                    var endpoint = endpoints[e];
                    if (endpoint == null) continue;

                    ValidateEndpoint(catalog, index, collection, endpoint, e, findings);
                }
            }
        }

        private static void ValidateEndpoint(
            NetworkCatalog catalog,
            NetworkCatalogIndex index,
            NetworkEndpointCollection collection,
            NetworkEndpointDefinition endpoint,
            int position,
            List<NetworkValidationFinding> findings)
        {
            ValidateId(
                endpoint.Id, NetworkValidationEntityKind.Endpoint,
                $"endpoint #{position} in '{collection.DisplayName}'", collection, findings);

            string serviceId = collection.ResolveServiceId(endpoint);

            if (string.IsNullOrEmpty(serviceId) || !index.Services.TryGetValue(serviceId, out var service))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodeEndpointUnknownService,
                    NetworkErrorCategory.Configuration,
                    NetworkValidationEntityKind.Endpoint,
                    endpoint.Id,
                    string.IsNullOrEmpty(serviceId)
                        ? $"Endpoint '{endpoint.Id}' names no service, and its collection supplies no default."
                        : $"Endpoint '{endpoint.Id}' names service '{serviceId}', which does not exist.",
                    "Set the service on the endpoint or on its collection.",
                    collection));
                return;
            }

            if (!service.Supports(endpoint.RequiredProtocol))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodeEndpointProtocolUnsupported,
                    NetworkErrorCategory.Configuration,
                    NetworkValidationEntityKind.Endpoint,
                    endpoint.Id,
                    $"Endpoint '{endpoint.Id}' is a {endpoint.Kind} endpoint, but service '{serviceId}' does not declare {endpoint.RequiredProtocol}.",
                    $"Declare {endpoint.RequiredProtocol} on the service, or change the endpoint kind.",
                    collection));
            }

            if (!string.IsNullOrEmpty(endpoint.PolicyProfileId) &&
                !index.PolicyProfiles.ContainsKey(endpoint.PolicyProfileId))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodePolicyProfileUnknown,
                    NetworkErrorCategory.Configuration,
                    NetworkValidationEntityKind.Endpoint,
                    endpoint.Id,
                    $"Endpoint '{endpoint.Id}' references policy profile '{endpoint.PolicyProfileId}', which does not exist.",
                    "Create the profile or clear the override.",
                    collection));
            }

            ValidateEndpointPath(collection, endpoint, findings);

            ScanForSuspectedSecret(
                endpoint.RequestBodyExample, "example request body",
                NetworkValidationEntityKind.Endpoint, endpoint.Id, collection, findings);
        }

        private static void ValidateEndpointPath(
            NetworkEndpointCollection collection,
            NetworkEndpointDefinition endpoint,
            List<NetworkValidationFinding> findings)
        {
            // Validate against a synthetic origin: the join rules are origin-independent, and this
            // way a path problem is reported once on the endpoint rather than once per environment.
            if (!NetworkOrigin.TryJoin("https://validation.invalid", endpoint.RelativePath, out _, out string error))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodeEndpointPathInvalid,
                    NetworkErrorCategory.Configuration,
                    NetworkValidationEntityKind.Endpoint,
                    endpoint.Id,
                    $"Endpoint '{endpoint.Id}' path is unusable: {error}",
                    "Author a path relative to the service origin.",
                    collection));
                return;
            }

            var placeholders = ExtractPlaceholders(endpoint.RelativePath);
            var declared = new HashSet<string>(StringComparer.Ordinal);

            foreach (var parameter in endpoint.PathParameters)
            {
                if (parameter != null && !string.IsNullOrEmpty(parameter.Name))
                    declared.Add(parameter.Name);
            }

            foreach (string placeholder in placeholders)
            {
                if (!declared.Contains(placeholder))
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Error,
                        CodeEndpointParameterMismatch,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.Endpoint,
                        endpoint.Id,
                        $"Endpoint '{endpoint.Id}' path contains '{{{placeholder}}}' but declares no such path parameter.",
                        $"Declare a path parameter named '{placeholder}'.",
                        collection));
                }
            }

            foreach (string name in declared)
            {
                if (!placeholders.Contains(name))
                {
                    findings.Add(Finding(
                        NetworkValidationSeverity.Warning,
                        CodeEndpointParameterMismatch,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.Endpoint,
                        endpoint.Id,
                        $"Endpoint '{endpoint.Id}' declares path parameter '{name}', which does not appear in the path.",
                        $"Add '{{{name}}}' to the path, or remove the parameter.",
                        collection));
                }
            }
        }

        /// <summary>
        /// Extracts <c>{name}</c> placeholders from a path.
        /// </summary>
        /// <param name="path">The relative path to scan.</param>
        /// <returns>The placeholder names, without braces.</returns>
        private static HashSet<string> ExtractPlaceholders(string path)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(path)) return names;

            int cursor = 0;
            while (cursor < path.Length)
            {
                int open = path.IndexOf('{', cursor);
                if (open < 0) break;

                int close = path.IndexOf('}', open + 1);
                if (close < 0) break;

                string name = path.Substring(open + 1, close - open - 1);
                if (name.Length > 0)
                    names.Add(name);

                cursor = close + 1;
            }
            return names;
        }

        // ---- Shared helpers

        private static readonly NetworkProtocols[] AllProtocols =
        {
            NetworkProtocols.Http,
            NetworkProtocols.ServerSentEvents,
            NetworkProtocols.WebSocket,
            NetworkProtocols.SocketIO
        };

        private static void ValidateId(
            string id,
            NetworkValidationEntityKind kind,
            string positionLabel,
            UnityEngine.Object target,
            List<NetworkValidationFinding> findings)
        {
            if (!NetworkIds.IsValid(id, out string error))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Error,
                    CodeIdInvalid,
                    NetworkErrorCategory.Configuration,
                    kind,
                    id,
                    $"{positionLabel}: {error}",
                    "Use lowercase letters, digits, and single hyphens, starting with a letter.",
                    target));
                return;
            }

            if (NetworkIds.IsReserved(id))
            {
                findings.Add(Finding(
                    NetworkValidationSeverity.Warning,
                    CodeIdReserved,
                    NetworkErrorCategory.Configuration,
                    kind,
                    id,
                    $"{positionLabel}: '{id}' uses the framework-reserved '{NetworkIds.ReservedPrefix}' prefix.",
                    "Rename it unless this entry was generated by migration.",
                    target));
            }
        }

        /// <summary>
        /// Flags a serialized field that looks like it holds credential material.
        /// </summary>
        /// <remarks>
        /// A heuristic, not a guarantee. It exists because the one thing that must never end up in a
        /// catalog is a secret, and a false positive here costs an author one glance while a false
        /// negative commits a token to source control. Detects JWTs, common key prefixes, and
        /// <c>Bearer</c>-prefixed values.
        /// </remarks>
        private static void ScanForSuspectedSecret(
            string value,
            string fieldLabel,
            NetworkValidationEntityKind kind,
            string entityId,
            UnityEngine.Object target,
            List<NetworkValidationFinding> findings)
        {
            if (string.IsNullOrEmpty(value) || !LooksLikeSecret(value))
                return;

            findings.Add(Finding(
                NetworkValidationSeverity.Error,
                CodeSecretSuspected,
                NetworkErrorCategory.SecurityPolicy,
                kind,
                entityId,
                $"The {fieldLabel} looks like it contains a credential. Catalog assets must never hold secret values.",
                "Move the value to a credential provider and reference it by profile instead.",
                target));
        }

        private static bool LooksLikeSecret(string value)
        {
            string trimmed = value.Trim();

            if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return true;

            // JWTs: three dot-separated base64url segments beginning with the standard header.
            if (trimmed.Contains("eyJ") && CountChar(trimmed, '.') >= 2)
                return true;

            string[] prefixes = { "sk-", "ghp_", "github_pat_", "xoxb-", "xoxp-", "AKIA", "AIza", "SG." };
            foreach (string prefix in prefixes)
            {
                int at = trimmed.IndexOf(prefix, StringComparison.Ordinal);
                if (at < 0) continue;

                // Require enough trailing entropy that a prose mention ("use an sk- key") is not flagged.
                if (trimmed.Length - at >= prefix.Length + 16)
                    return true;
            }
            return false;
        }

        private static int CountChar(string value, char c)
        {
            int count = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == c) count++;
            }
            return count;
        }

        private static NetworkValidationFinding Finding(
            NetworkValidationSeverity severity,
            string code,
            NetworkErrorCategory category,
            NetworkValidationEntityKind kind,
            string entityId,
            string message,
            string remedy,
            UnityEngine.Object target,
            string environmentId = null)
        {
            return new NetworkValidationFinding(
                severity, code, category, kind, entityId, message, remedy, environmentId, target);
        }
    }
}
