using System.Collections.Generic;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// Computes a <see cref="NetworkEffectivePolicy"/> from the precedence chain
    /// library default → catalog → environment → service → endpoint → per-send override.
    /// </summary>
    /// <remarks>
    /// Pure: no Unity API, no I/O, no state. Callable from edit mode, batch mode, tests, and the
    /// runtime pipeline, and it gives the same answer in all four — which is the point of having the
    /// Hub's preview and the runtime read the same resolver.
    /// <para>
    /// Two kinds of field behave differently:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Inheritable</b> numeric fields treat 0 as "not authored" and fall through to the next lower
    /// layer. That is how an endpoint can override a retry count without also having to restate a
    /// timeout.
    /// </description></item>
    /// <item><description>
    /// <b>Security-restricted</b> fields resolve tighten-only: every layer may make the rule stricter,
    /// none may relax it. Rejected attempts are recorded in
    /// <see cref="NetworkEffectivePolicy.SecurityClamps"/> rather than silently dropped.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class NetworkPolicyResolver
    {
        /// <summary>One layer's contribution to resolution.</summary>
        private readonly struct Contribution
        {
            public readonly NetworkPolicyProfile Profile;
            public readonly NetworkConfigurationLayer Layer;

            public Contribution(NetworkPolicyProfile profile, NetworkConfigurationLayer layer)
            {
                Profile = profile;
                Layer = layer;
            }
        }

        /// <summary>
        /// Resolves the effective policy for one route, optionally narrowed by an endpoint and a
        /// per-send override.
        /// </summary>
        /// <param name="catalog">The catalog supplying profiles. <c>null</c> yields library defaults.</param>
        /// <param name="environment">
        /// The target environment profile, or <c>null</c>. A production environment forces
        /// <see cref="NetworkEffectivePolicy.RequireSecureTransport"/> on and
        /// <see cref="NetworkEffectivePolicy.ValidateTlsCertificate"/> on regardless of any profile.
        /// </param>
        /// <param name="service">The target service definition, or <c>null</c>.</param>
        /// <param name="endpoint">The endpoint template being called, or <c>null</c> for a raw path send.</param>
        /// <param name="sendOverride">The caller's per-send override, or <c>null</c>.</param>
        /// <returns>A frozen effective policy. Never <c>null</c>.</returns>
        public static NetworkEffectivePolicy Resolve(
            NetworkCatalog catalog,
            NetworkEnvironmentProfile environment,
            NetworkServiceDefinition service,
            NetworkEndpointDefinition endpoint,
            NetworkSendPolicyOverride sendOverride = null)
        {
            var layers = BuildLayers(catalog, environment, service, endpoint);
            var clamps = new List<string>();

            var policy = new NetworkEffectivePolicy
            {
                // Inheritable: 0 means "not authored here", so resolution falls through.
                OverallTimeoutSeconds = ResolveInheritableFloat(layers, p => p.OverallTimeoutSeconds, 60f),
                AttemptTimeoutSeconds = ResolveInheritableFloat(layers, p => p.AttemptTimeoutSeconds, 30f),
                MaxConcurrentRequests = ResolveInheritableInt(layers, p => p.MaxConcurrentRequests, 4),

                // Highest authored layer wins outright.
                RetryEnabled = ResolveTopmost(layers, p => p.RetryEnabled, true),
                MaxRetries = ResolveTopmost(layers, p => p.MaxRetries, 2),
                RetryBaseDelaySeconds = ResolveTopmost(layers, p => p.RetryBaseDelaySeconds, 0.5f),
                RetryMaxDelaySeconds = ResolveTopmost(layers, p => p.RetryMaxDelaySeconds, 30f),
                RetryJitter = ResolveTopmost(layers, p => p.RetryJitter, true),
                RetryRequiresIdempotence = ResolveTopmost(layers, p => p.RetryRequiresIdempotence, true),
                HonorRetryAfter = ResolveTopmost(layers, p => p.HonorRetryAfter, true),
                MaxQueueDepth = ResolveTopmost(layers, p => p.MaxQueueDepth, 128),
                CircuitFailureThreshold = ResolveTopmost(layers, p => p.CircuitFailureThreshold, 0),
                CircuitResetSeconds = ResolveTopmost(layers, p => p.CircuitResetSeconds, 30f),
                CacheMode = ResolveTopmost(layers, p => p.CacheMode, NetworkCacheMode.Disabled),
                CacheTtlSeconds = ResolveTopmost(layers, p => p.CacheTtlSeconds, 60f),
                LogRequests = ResolveTopmost(layers, p => p.LogRequests, true),
                CaptureBodies = ResolveTopmost(layers, p => p.CaptureBodies, false),

                // Security-restricted: strictest layer wins.
                RedirectMode = ResolveStrictestRedirect(layers),
                MaxRedirects = ResolveSmallestInt(layers, p => p.MaxRedirects, 3),
                RequireSecureTransport = ResolveAnyTrue(layers, p => p.RequireSecureTransport),
                // Not "any true wins": relaxing certificate validation against a local mock server is
                // legitimate, so the topmost layer decides. What cannot happen is relaxing it in a
                // production environment — clamped below, with a recorded reason.
                ValidateTlsCertificate = ResolveTopmost(layers, p => p.ValidateTlsCertificate, true),
                MaxRequestBytes = ResolveSmallestNonZeroLong(layers, p => p.MaxRequestBytes),
                MaxResponseBytes = ResolveSmallestNonZeroLong(layers, p => p.MaxResponseBytes)
            };

            // The environment's own posture is not a policy profile, but it tightens the same fields.
            if (environment != null && environment.RequireSecureTransport && !policy.RequireSecureTransport.Value)
            {
                policy.RequireSecureTransport =
                    new NetworkEffectiveValue<bool>(true, NetworkConfigurationLayer.Environment);
            }

            if (environment != null && environment.IsProductionSafetyEnforced && !policy.ValidateTlsCertificate.Value)
            {
                policy.ValidateTlsCertificate =
                    new NetworkEffectiveValue<bool>(true, NetworkConfigurationLayer.Environment);
                clamps.Add(
                    $"TLS validation cannot be disabled in production environment '{environment.Id}'; " +
                    "the authored policy was overruled.");
            }

            ApplySendOverride(policy, sendOverride, clamps);

            policy.SecurityClamps = clamps.Count == 0 ? System.Array.Empty<string>() : clamps.ToArray();
            return policy;
        }

        private static List<Contribution> BuildLayers(
            NetworkCatalog catalog,
            NetworkEnvironmentProfile environment,
            NetworkServiceDefinition service,
            NetworkEndpointDefinition endpoint)
        {
            var layers = new List<Contribution>(5)
            {
                new Contribution(NetworkPolicyProfile.CreateLibraryDefault(), NetworkConfigurationLayer.LibraryDefault)
            };

            if (catalog == null)
                return layers;

            AddIfResolved(layers, catalog, catalog.DefaultPolicyProfileId, NetworkConfigurationLayer.CatalogDefault);
            AddIfResolved(layers, catalog, environment?.PolicyProfileId, NetworkConfigurationLayer.Environment);
            AddIfResolved(layers, catalog, service?.PolicyProfileId, NetworkConfigurationLayer.Service);
            AddIfResolved(layers, catalog, endpoint?.PolicyProfileId, NetworkConfigurationLayer.Endpoint);

            return layers;
        }

        private static void AddIfResolved(
            List<Contribution> layers,
            NetworkCatalog catalog,
            string profileId,
            NetworkConfigurationLayer layer)
        {
            if (string.IsNullOrEmpty(profileId))
                return;

            // A dangling profile ID contributes nothing here. The validator reports it as a finding;
            // resolution must not throw or silently invent values in the meantime.
            var profile = catalog.FindPolicyProfile(profileId);
            if (profile != null)
                layers.Add(new Contribution(profile, layer));
        }

        /// <summary>
        /// Takes the highest authored layer's value outright. <paramref name="fallback"/> only applies
        /// when the layer list is empty, which <see cref="BuildLayers"/> never produces.
        /// </summary>
        private static NetworkEffectiveValue<T> ResolveTopmost<T>(
            List<Contribution> layers,
            System.Func<NetworkPolicyProfile, T> read,
            T fallback)
        {
            if (layers.Count == 0)
                return new NetworkEffectiveValue<T>(fallback, NetworkConfigurationLayer.LibraryDefault);

            Contribution top = layers[layers.Count - 1];
            return new NetworkEffectiveValue<T>(read(top.Profile), top.Layer);
        }

        /// <summary>Takes the highest layer whose value is non-zero; 0 means "inherit".</summary>
        private static NetworkEffectiveValue<float> ResolveInheritableFloat(
            List<Contribution> layers,
            System.Func<NetworkPolicyProfile, float> read,
            float fallback)
        {
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                float value = read(layers[i].Profile);
                if (value > 0f)
                    return new NetworkEffectiveValue<float>(value, layers[i].Layer);
            }
            return new NetworkEffectiveValue<float>(fallback, NetworkConfigurationLayer.LibraryDefault);
        }

        /// <summary>Takes the highest layer whose value is non-zero; 0 means "inherit".</summary>
        private static NetworkEffectiveValue<int> ResolveInheritableInt(
            List<Contribution> layers,
            System.Func<NetworkPolicyProfile, int> read,
            int fallback)
        {
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                int value = read(layers[i].Profile);
                if (value > 0)
                    return new NetworkEffectiveValue<int>(value, layers[i].Layer);
            }
            return new NetworkEffectiveValue<int>(fallback, NetworkConfigurationLayer.LibraryDefault);
        }

        /// <summary>
        /// Security-restricted: the strictest (lowest ordinal) redirect mode across the <em>authored</em>
        /// layers, falling back to the library default when nothing is authored.
        /// </summary>
        /// <remarks>
        /// The library default is excluded from the comparison on purpose. Including it would make it an
        /// absolute ceiling — no service could ever author a looser mode than the built-in
        /// <see cref="NetworkRedirectMode.SameOrigin"/>, so <see cref="NetworkRedirectMode.AllowedHosts"/>
        /// would be unreachable. Tighten-only is a rule about what one authored layer may do to another,
        /// not a licence for the default to outvote every one of them.
        /// </remarks>
        private static NetworkEffectiveValue<NetworkRedirectMode> ResolveStrictestRedirect(List<Contribution> layers)
        {
            var mode = NetworkRedirectMode.AllowedHosts;
            var source = NetworkConfigurationLayer.LibraryDefault;
            bool authored = false;

            foreach (var contribution in layers)
            {
                if (contribution.Layer == NetworkConfigurationLayer.LibraryDefault)
                    continue;

                var candidate = contribution.Profile.RedirectMode;
                if (!authored || candidate <= mode)
                {
                    mode = candidate;
                    source = contribution.Layer;
                    authored = true;
                }
            }

            return authored
                ? new NetworkEffectiveValue<NetworkRedirectMode>(mode, source)
                : new NetworkEffectiveValue<NetworkRedirectMode>(
                    LibraryDefaultOf(layers, p => p.RedirectMode, NetworkRedirectMode.SameOrigin),
                    NetworkConfigurationLayer.LibraryDefault);
        }

        /// <summary>Reads a field from the library-default layer, or a fallback when it is absent.</summary>
        private static T LibraryDefaultOf<T>(
            List<Contribution> layers,
            System.Func<NetworkPolicyProfile, T> read,
            T fallback)
        {
            foreach (var contribution in layers)
            {
                if (contribution.Layer == NetworkConfigurationLayer.LibraryDefault)
                    return read(contribution.Profile);
            }
            return fallback;
        }

        /// <summary>
        /// Security-restricted tightening flag: <c>false</c> means "no opinion", so <c>true</c> at any
        /// layer wins and no higher layer can turn it back off.
        /// </summary>
        private static NetworkEffectiveValue<bool> ResolveAnyTrue(
            List<Contribution> layers,
            System.Func<NetworkPolicyProfile, bool> read)
        {
            foreach (var contribution in layers)
            {
                if (read(contribution.Profile))
                    return new NetworkEffectiveValue<bool>(true, contribution.Layer);
            }
            return new NetworkEffectiveValue<bool>(false, NetworkConfigurationLayer.LibraryDefault);
        }

        /// <summary>
        /// Security-restricted: the smallest limit across the <em>authored</em> layers, falling back to
        /// the library default when nothing is authored.
        /// </summary>
        /// <remarks>
        /// Excludes the library default for the same reason as
        /// <see cref="ResolveStrictestRedirect"/>: a built-in bound must not be a ceiling no authored
        /// policy can raise.
        /// </remarks>
        private static NetworkEffectiveValue<int> ResolveSmallestInt(
            List<Contribution> layers,
            System.Func<NetworkPolicyProfile, int> read,
            int fallback)
        {
            int smallest = int.MaxValue;
            var source = NetworkConfigurationLayer.LibraryDefault;

            foreach (var contribution in layers)
            {
                if (contribution.Layer == NetworkConfigurationLayer.LibraryDefault)
                    continue;

                int value = read(contribution.Profile);
                if (value < smallest)
                {
                    smallest = value;
                    source = contribution.Layer;
                }
            }

            return smallest == int.MaxValue
                ? new NetworkEffectiveValue<int>(
                    LibraryDefaultOf(layers, read, fallback), NetworkConfigurationLayer.LibraryDefault)
                : new NetworkEffectiveValue<int>(smallest, source);
        }

        /// <summary>
        /// Security-restricted: the smallest non-zero limit, where 0 means unlimited and therefore
        /// never wins against an authored bound.
        /// </summary>
        private static NetworkEffectiveValue<long> ResolveSmallestNonZeroLong(
            List<Contribution> layers,
            System.Func<NetworkPolicyProfile, long> read)
        {
            long smallest = 0;
            var source = NetworkConfigurationLayer.LibraryDefault;

            foreach (var contribution in layers)
            {
                long value = read(contribution.Profile);
                if (value <= 0) continue;

                if (smallest == 0 || value < smallest)
                {
                    smallest = value;
                    source = contribution.Layer;
                }
            }
            return new NetworkEffectiveValue<long>(smallest, source);
        }

        private static void ApplySendOverride(
            NetworkEffectivePolicy policy,
            NetworkSendPolicyOverride sendOverride,
            List<string> clamps)
        {
            if (sendOverride == null || !sendOverride.HasAnyValue)
                return;

            const NetworkConfigurationLayer layer = NetworkConfigurationLayer.SendOverride;

            if (sendOverride.OverallTimeoutSeconds is > 0f)
                policy.OverallTimeoutSeconds = new NetworkEffectiveValue<float>(sendOverride.OverallTimeoutSeconds.Value, layer);

            if (sendOverride.AttemptTimeoutSeconds is > 0f)
                policy.AttemptTimeoutSeconds = new NetworkEffectiveValue<float>(sendOverride.AttemptTimeoutSeconds.Value, layer);

            if (sendOverride.RetryEnabled.HasValue)
                policy.RetryEnabled = new NetworkEffectiveValue<bool>(sendOverride.RetryEnabled.Value, layer);

            if (sendOverride.MaxRetries is >= 0)
                policy.MaxRetries = new NetworkEffectiveValue<int>(sendOverride.MaxRetries.Value, layer);

            if (sendOverride.LogRequests.HasValue)
                policy.LogRequests = new NetworkEffectiveValue<bool>(sendOverride.LogRequests.Value, layer);

            if (sendOverride.CaptureBodies.HasValue)
                policy.CaptureBodies = new NetworkEffectiveValue<bool>(sendOverride.CaptureBodies.Value, layer);

            if (sendOverride.RedirectMode.HasValue)
            {
                var requested = sendOverride.RedirectMode.Value;
                if (requested <= policy.RedirectMode.Value)
                {
                    policy.RedirectMode = new NetworkEffectiveValue<NetworkRedirectMode>(requested, layer);
                }
                else
                {
                    clamps.Add(
                        $"Per-send redirect mode '{requested}' is looser than the inherited " +
                        $"'{policy.RedirectMode.Value}' from {policy.RedirectMode.Source}; the inherited rule stands.");
                }
            }

            policy.IdempotencyKey = sendOverride.IdempotencyKey;
        }
    }
}
