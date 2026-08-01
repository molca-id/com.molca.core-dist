using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Molca.Networking.Configuration;
using Molca.Networking.Routing;

namespace Molca.Networking.Security
{
    /// <summary>
    /// Resolves credentials for the pipeline: picks the right provider, caches by profile, and
    /// collapses concurrent acquisitions of the same profile into one.
    /// </summary>
    /// <remarks>
    /// Owned by the network subsystem. Every request goes through
    /// <see cref="AcquireForHostAsync"/>, which refuses to hand back a credential for a host outside the
    /// profile's scope — the check happens here rather than at the call site so no code path can skip it
    /// (plan §6.6).
    /// <para>
    /// Single-flight matters because a token refresh is often slow and frequently triggered by several
    /// requests failing at once; without it, a burst of 401s becomes a burst of refreshes.
    /// </para>
    /// <para>
    /// Main-thread only, matching the rest of the DI/subsystem surface. The in-flight map is not
    /// synchronized.
    /// </para>
    /// </remarks>
    public sealed class NetworkCredentialRegistry
    {
        private readonly Dictionary<NetworkCredentialProviderKind, INetworkCredentialProvider> _providers =
            new Dictionary<NetworkCredentialProviderKind, INetworkCredentialProvider>();

        private readonly Dictionary<string, NetworkCredential> _cache =
            new Dictionary<string, NetworkCredential>(StringComparer.Ordinal);

        // One list per in-flight profile acquisition, holding a completion source per waiter. A Unity
        // Awaitable is single-consumption, so waiters cannot share one — each gets its own source and
        // the leader completes them all.
        private readonly Dictionary<string, List<AwaitableCompletionSource<NetworkCredential>>> _inFlight =
            new Dictionary<string, List<AwaitableCompletionSource<NetworkCredential>>>(StringComparer.Ordinal);

        /// <summary>
        /// Registers a provider for its <see cref="INetworkCredentialProvider.Kind"/>, replacing any
        /// previous registration for that kind.
        /// </summary>
        /// <param name="provider">The provider to register.</param>
        public void Register(INetworkCredentialProvider provider)
        {
            if (provider == null) return;
            _providers[provider.Kind] = provider;
        }

        /// <summary>Removes a provider registration.</summary>
        /// <param name="kind">The kind to unregister.</param>
        /// <returns><c>true</c> when a provider was removed.</returns>
        public bool Unregister(NetworkCredentialProviderKind kind) => _providers.Remove(kind);

        /// <summary>Whether a provider is registered for <paramref name="kind"/>.</summary>
        /// <param name="kind">The kind to test.</param>
        public bool HasProvider(NetworkCredentialProviderKind kind) => _providers.ContainsKey(kind);

        /// <summary>Drops every cached credential. In-flight acquisitions are left to complete.</summary>
        public void ClearCache() => _cache.Clear();

        /// <summary>
        /// Acquires a credential for a profile, but only if <paramref name="host"/> is inside the
        /// profile's service and host scope.
        /// </summary>
        /// <param name="profile">The profile, or <c>null</c> for an anonymous request.</param>
        /// <param name="serviceId">The service making the request.</param>
        /// <param name="host">The host the request is about to reach, after any redirect.</param>
        /// <param name="forceRefresh">Whether to bypass the cache — used after a 401.</param>
        /// <param name="cancellationToken">Cancels acquisition.</param>
        /// <returns>
        /// The credential, or <see cref="NetworkCredential.None"/> when the profile is absent,
        /// anonymous, out of scope for the host, or has no registered provider.
        /// </returns>
        /// <remarks>
        /// Scope is checked against the host passed in, not the authored origin. Calling this again with
        /// a redirect target is how a credential is prevented from following a 302 off-domain.
        /// </remarks>
        public async Awaitable<NetworkCredential> AcquireForHostAsync(
            NetworkCredentialProfile profile,
            string serviceId,
            string host,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            if (!NetworkRouteResolver.CredentialApplies(profile, serviceId, host))
                return NetworkCredential.None;

            if (!_providers.TryGetValue(profile.ProviderKind, out var provider))
            {
                // A profile naming a provider nobody registered is a configuration problem the
                // validator reports. At runtime the request degrades to anonymous rather than failing,
                // which is the same behaviour as an endpoint that tolerates anonymous access.
                Debug.LogWarning(
                    $"[Network] No credential provider is registered for '{profile.ProviderKind}', " +
                    $"required by profile '{profile.Id}'. The request will be sent anonymously.");
                return NetworkCredential.None;
            }

            string key = profile.Id;

            if (!forceRefresh &&
                _cache.TryGetValue(key, out var cached) &&
                cached.HasValue &&
                !cached.IsExpired(DateTime.UtcNow))
            {
                return cached;
            }

            // Single-flight: join an acquisition already running for this profile rather than starting
            // a second one. A forced refresh still joins — two 401s arriving together want one refresh,
            // not two.
            if (_inFlight.TryGetValue(key, out var waiters))
            {
                var waiter = new AwaitableCompletionSource<NetworkCredential>();
                waiters.Add(waiter);
                return await waiter.Awaitable;
            }

            var followers = new List<AwaitableCompletionSource<NetworkCredential>>();
            _inFlight[key] = followers;

            NetworkCredential result = NetworkCredential.None;
            try
            {
                result = await provider.AcquireAsync(profile, forceRefresh, cancellationToken);

                if (result.HasValue)
                    _cache[key] = result;
                else
                    _cache.Remove(key);

                return result;
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not an error, but followers must not hang. They receive the absent
                // credential; their own tokens surface the cancellation to their callers.
                throw;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Network] Credential provider for '{profile.Id}' threw: {e.Message}");
                return NetworkCredential.None;
            }
            finally
            {
                // Release followers before clearing the slot, so a follower that immediately re-asks
                // sees a settled cache rather than joining a list nobody will complete.
                _inFlight.Remove(key);
                for (int i = 0; i < followers.Count; i++)
                    followers[i].TrySetResult(result);
            }
        }

        /// <summary>
        /// Whether a profile currently has a usable cached credential, without acquiring one.
        /// </summary>
        /// <param name="profileId">The profile ID.</param>
        /// <returns><c>true</c> when a non-expired value is cached.</returns>
        /// <remarks>Used by the Hub's Credentials view to show readiness without triggering a fetch.</remarks>
        public bool HasCachedCredential(string profileId) =>
            !string.IsNullOrEmpty(profileId) &&
            _cache.TryGetValue(profileId, out var credential) &&
            credential.HasValue &&
            !credential.IsExpired(DateTime.UtcNow);
    }
}
