using System.Collections.Generic;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// A fully resolved policy: every field carries its value and the layer that supplied it.
    /// Immutable, and frozen before a request is queued.
    /// </summary>
    /// <remarks>
    /// Produced by <see cref="NetworkPolicyResolver.Resolve"/>. The pipeline reads only from an
    /// instance of this type, never from <see cref="NetworkCatalog"/> or the Hub's current selection —
    /// which is what makes an in-flight request immune to configuration changes underneath it
    /// (plan §6.2).
    /// </remarks>
    public sealed class NetworkEffectivePolicy
    {
        /// <summary>Wall-clock budget for the whole send: queueing, auth, retry delays, and transfer.</summary>
        public NetworkEffectiveValue<float> OverallTimeoutSeconds { get; internal set; }

        /// <summary>Budget for a single transport attempt.</summary>
        public NetworkEffectiveValue<float> AttemptTimeoutSeconds { get; internal set; }

        /// <summary>Whether retries happen at all.</summary>
        public NetworkEffectiveValue<bool> RetryEnabled { get; internal set; }

        /// <summary>Retry attempts after the initial send.</summary>
        public NetworkEffectiveValue<int> MaxRetries { get; internal set; }

        /// <summary>First retry delay in seconds; doubles per attempt.</summary>
        public NetworkEffectiveValue<float> RetryBaseDelaySeconds { get; internal set; }

        /// <summary>Ceiling on a single backoff delay, before jitter.</summary>
        public NetworkEffectiveValue<float> RetryMaxDelaySeconds { get; internal set; }

        /// <summary>Whether full jitter is applied to backoff delays.</summary>
        public NetworkEffectiveValue<bool> RetryJitter { get; internal set; }

        /// <summary>Whether retry requires an idempotent method or an explicit idempotency key.</summary>
        public NetworkEffectiveValue<bool> RetryRequiresIdempotence { get; internal set; }

        /// <summary>Whether a <c>Retry-After</c> header is honoured within the remaining budget.</summary>
        public NetworkEffectiveValue<bool> HonorRetryAfter { get; internal set; }

        /// <summary>Requests in flight per route.</summary>
        public NetworkEffectiveValue<int> MaxConcurrentRequests { get; internal set; }

        /// <summary>Requests allowed to wait per route before sends fail fast.</summary>
        public NetworkEffectiveValue<int> MaxQueueDepth { get; internal set; }

        /// <summary>Consecutive failures that open the circuit; 0 disables it.</summary>
        public NetworkEffectiveValue<int> CircuitFailureThreshold { get; internal set; }

        /// <summary>Seconds the circuit stays open before a trial request is admitted.</summary>
        public NetworkEffectiveValue<float> CircuitResetSeconds { get; internal set; }

        /// <summary>How redirects are treated. Resolved tighten-only.</summary>
        public NetworkEffectiveValue<NetworkRedirectMode> RedirectMode { get; internal set; }

        /// <summary>Maximum redirects followed within one send. Resolved tighten-only.</summary>
        public NetworkEffectiveValue<int> MaxRedirects { get; internal set; }

        /// <summary>Whether an encrypted scheme is required. Resolved tighten-only.</summary>
        public NetworkEffectiveValue<bool> RequireSecureTransport { get; internal set; }

        /// <summary>Whether the server certificate is validated. Resolved tighten-only.</summary>
        public NetworkEffectiveValue<bool> ValidateTlsCertificate { get; internal set; }

        /// <summary>How responses are cached.</summary>
        public NetworkEffectiveValue<NetworkCacheMode> CacheMode { get; internal set; }

        /// <summary>Lifetime for fixed-TTL caching, in seconds.</summary>
        public NetworkEffectiveValue<float> CacheTtlSeconds { get; internal set; }

        /// <summary>Whether requests are recorded in the redacted diagnostic buffer.</summary>
        public NetworkEffectiveValue<bool> LogRequests { get; internal set; }

        /// <summary>Whether redacted bodies are captured in diagnostics.</summary>
        public NetworkEffectiveValue<bool> CaptureBodies { get; internal set; }

        /// <summary>Maximum encoded request body in bytes; 0 means unlimited. Resolved tighten-only.</summary>
        public NetworkEffectiveValue<long> MaxRequestBytes { get; internal set; }

        /// <summary>Maximum response body in bytes; 0 means unlimited. Resolved tighten-only.</summary>
        public NetworkEffectiveValue<long> MaxResponseBytes { get; internal set; }

        /// <summary>Idempotency key supplied by the caller, or <c>null</c>.</summary>
        public string IdempotencyKey { get; internal set; }

        /// <summary>
        /// Layers that attempted to weaken a security-restricted field and were overruled, with an
        /// explanation. Empty when nothing was clamped.
        /// </summary>
        /// <remarks>
        /// Surfaced by the Hub's effective-policy inspector so the reason an override "did nothing" is
        /// visible rather than mysterious, and logged by the pipeline so an attempt to relax a
        /// production rule leaves a trace.
        /// </remarks>
        public IReadOnlyList<string> SecurityClamps { get; internal set; } = System.Array.Empty<string>();

        /// <summary>Whether any security-restricted field was clamped during resolution.</summary>
        public bool HasSecurityClamps => SecurityClamps != null && SecurityClamps.Count > 0;

        /// <summary>
        /// Whether a request using <paramref name="method"/> may be retried under this policy.
        /// </summary>
        /// <param name="method">The HTTP method of the request.</param>
        /// <param name="endpointIsIdempotent">
        /// Whether the endpoint template classifies the call as safe to repeat. Pass <c>true</c> when
        /// no endpoint template is involved, so the method decides on its own.
        /// </param>
        /// <returns><c>true</c> when retry is permitted.</returns>
        /// <remarks>
        /// A mutating call is never retried merely because it failed. It qualifies only when the policy
        /// does not require idempotence, or when the caller supplied an
        /// <see cref="IdempotencyKey"/> (plan §6.4).
        /// </remarks>
        public bool AllowsRetryFor(Http.Models.HttpMethod method, bool endpointIsIdempotent = true)
        {
            if (!RetryEnabled.Value || MaxRetries.Value <= 0)
                return false;

            if (!RetryRequiresIdempotence.Value)
                return true;

            if (!string.IsNullOrEmpty(IdempotencyKey))
                return true;

            return endpointIsIdempotent && NetworkEndpointDefinition.IsIdempotentMethod(method);
        }
    }
}
