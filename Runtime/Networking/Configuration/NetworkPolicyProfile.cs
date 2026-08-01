using System;
using UnityEngine;

namespace Molca.Networking.Configuration
{
    /// <summary>How the pipeline treats HTTP redirects. Ordered from strictest to loosest.</summary>
    /// <remarks>
    /// The ordinal order matters: precedence resolution takes the <em>minimum</em> of the layers so a
    /// looser per-send override can never weaken an inherited restriction (plan §5.6).
    /// </remarks>
    public enum NetworkRedirectMode
    {
        /// <summary>Never follow a redirect. The 3xx response is returned as-is.</summary>
        Disallow = 0,

        /// <summary>Follow only when the target has the same scheme, host, and port.</summary>
        SameOrigin = 1,

        /// <summary>Follow when the target host matches the service's allowed-host rules.</summary>
        AllowedHosts = 2
    }

    /// <summary>How the pipeline caches responses.</summary>
    public enum NetworkCacheMode
    {
        /// <summary>No caching.</summary>
        Disabled = 0,

        /// <summary>Honour the server's cache directives.</summary>
        RespectServer,

        /// <summary>Cache successful safe responses for <see cref="NetworkPolicyProfile.CacheTtlSeconds"/>.</summary>
        FixedTtl
    }

    /// <summary>
    /// A named bundle of timeout, retry, concurrency, redirect, TLS, cache, logging, and size
    /// limits. Serialized inside <see cref="NetworkCatalog"/> and referenced by ID from
    /// environments, services, and endpoints.
    /// </summary>
    /// <remarks>
    /// Every field here must be enforced by the pipeline or removed from the model — plan §11
    /// Phase 2 exit criteria. Do not add a knob before the pipeline reads it.
    /// </remarks>
    [Serializable]
    public class NetworkPolicyProfile
    {
        [SerializeField] private string _id = "";
        [SerializeField] private string _displayName = "";

        [Header("Timeouts")]
        [Tooltip("Wall-clock budget for the whole send: queueing, auth, retry delays, and transfer. 0 inherits.")]
        [SerializeField, Min(0f)] private float _overallTimeoutSeconds = 60f;

        [Tooltip("Budget for a single transport attempt. 0 inherits.")]
        [SerializeField, Min(0f)] private float _attemptTimeoutSeconds = 30f;

        [Header("Retry")]
        [SerializeField] private bool _retryEnabled = true;

        [Tooltip("Retry attempts after the initial send.")]
        [SerializeField, Range(0, 10)] private int _maxRetries = 2;

        [Tooltip("First retry delay in seconds; doubles per attempt.")]
        [SerializeField, Min(0f)] private float _retryBaseDelaySeconds = 0.5f;

        [Tooltip("Upper bound on a single backoff delay, before jitter.")]
        [SerializeField, Min(0f)] private float _retryMaxDelaySeconds = 30f;

        [Tooltip("Apply full jitter to backoff so clients that failed together do not retry in lockstep.")]
        [SerializeField] private bool _retryJitter = true;

        [Tooltip("Only retry methods that are safe to repeat (GET/HEAD/OPTIONS/PUT/DELETE), or that carry an idempotency key.")]
        [SerializeField] private bool _retryRequiresIdempotence = true;

        [Tooltip("Honour a Retry-After response header, within the remaining overall budget.")]
        [SerializeField] private bool _honorRetryAfter = true;

        [Header("Concurrency")]
        [Tooltip("Requests in flight per route. 0 inherits.")]
        [SerializeField, Range(0, 64)] private int _maxConcurrentRequests = 4;

        [Tooltip("Requests allowed to wait per route. Exceeding this fails the send immediately rather than queueing without bound.")]
        [SerializeField, Range(0, 4096)] private int _maxQueueDepth = 128;

        [Header("Circuit breaker")]
        [Tooltip("Consecutive failures that open the circuit. 0 disables the breaker.")]
        [SerializeField, Range(0, 100)] private int _circuitFailureThreshold = 0;

        [Tooltip("Seconds the circuit stays open before admitting one trial request.")]
        [SerializeField, Min(0f)] private float _circuitResetSeconds = 30f;

        [Header("Transport safety")]
        [SerializeField] private NetworkRedirectMode _redirectMode = NetworkRedirectMode.SameOrigin;

        [Tooltip("Maximum redirects followed within one send.")]
        [SerializeField, Range(0, 10)] private int _maxRedirects = 3;

        [Tooltip("Require an encrypted scheme. Cannot be relaxed by a lower precedence layer.")]
        [SerializeField] private bool _requireSecureTransport = false;

        [Tooltip("Validate the server certificate. Cannot be relaxed by a lower precedence layer, and never in production.")]
        [SerializeField] private bool _validateTlsCertificate = true;

        [Header("Cache")]
        [SerializeField] private NetworkCacheMode _cacheMode = NetworkCacheMode.Disabled;

        [Tooltip("Lifetime for FixedTtl caching, in seconds.")]
        [SerializeField, Min(0f)] private float _cacheTtlSeconds = 60f;

        [Header("Diagnostics")]
        [Tooltip("Record each request in the redacted diagnostic ring buffer.")]
        [SerializeField] private bool _logRequests = true;

        [Tooltip("Capture redacted request/response bodies in diagnostics. Off by default — bodies are the likeliest place for secrets.")]
        [SerializeField] private bool _captureBodies = false;

        [Header("Size limits")]
        [Tooltip("Maximum encoded request body in bytes. 0 means unlimited.")]
        [SerializeField, Min(0)] private long _maxRequestBytes = 0;

        [Tooltip("Maximum response body in bytes. 0 means unlimited.")]
        [SerializeField, Min(0)] private long _maxResponseBytes = 0;

        /// <summary>Stable kebab-case identifier.</summary>
        public string Id => _id;

        /// <summary>Human-readable name.</summary>
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? _id : _displayName;

        /// <summary>Wall-clock budget for the whole send, including queueing and retry delays. 0 inherits.</summary>
        public float OverallTimeoutSeconds => _overallTimeoutSeconds;

        /// <summary>Budget for one transport attempt. 0 inherits.</summary>
        public float AttemptTimeoutSeconds => _attemptTimeoutSeconds;

        /// <summary>Whether retries happen at all.</summary>
        public bool RetryEnabled => _retryEnabled;

        /// <summary>Retry attempts after the initial send.</summary>
        public int MaxRetries => _maxRetries;

        /// <summary>First retry delay in seconds; doubles per attempt.</summary>
        public float RetryBaseDelaySeconds => _retryBaseDelaySeconds;

        /// <summary>Ceiling on a single backoff delay, before jitter.</summary>
        public float RetryMaxDelaySeconds => _retryMaxDelaySeconds;

        /// <summary>Whether full jitter is applied to backoff delays.</summary>
        public bool RetryJitter => _retryJitter;

        /// <summary>Whether retry requires an idempotent method or an explicit idempotency key.</summary>
        public bool RetryRequiresIdempotence => _retryRequiresIdempotence;

        /// <summary>Whether a <c>Retry-After</c> header is honoured within the remaining budget.</summary>
        public bool HonorRetryAfter => _honorRetryAfter;

        /// <summary>Requests in flight per route. 0 inherits.</summary>
        public int MaxConcurrentRequests => _maxConcurrentRequests;

        /// <summary>Requests allowed to wait per route before sends fail fast.</summary>
        public int MaxQueueDepth => _maxQueueDepth;

        /// <summary>Consecutive failures that open the circuit; 0 disables it.</summary>
        public int CircuitFailureThreshold => _circuitFailureThreshold;

        /// <summary>Seconds the circuit stays open before a trial request is admitted.</summary>
        public float CircuitResetSeconds => _circuitResetSeconds;

        /// <summary>How redirects are treated. Security-restricted: never relaxable by a lower layer.</summary>
        public NetworkRedirectMode RedirectMode => _redirectMode;

        /// <summary>Maximum redirects followed within one send.</summary>
        public int MaxRedirects => _maxRedirects;

        /// <summary>Whether an encrypted scheme is required. Security-restricted.</summary>
        public bool RequireSecureTransport => _requireSecureTransport;

        /// <summary>Whether the server certificate is validated. Security-restricted.</summary>
        public bool ValidateTlsCertificate => _validateTlsCertificate;

        /// <summary>How responses are cached.</summary>
        public NetworkCacheMode CacheMode => _cacheMode;

        /// <summary>Lifetime for <see cref="NetworkCacheMode.FixedTtl"/> caching, in seconds.</summary>
        public float CacheTtlSeconds => _cacheTtlSeconds;

        /// <summary>Whether requests are recorded in the redacted diagnostic buffer.</summary>
        public bool LogRequests => _logRequests;

        /// <summary>Whether redacted bodies are captured in diagnostics.</summary>
        public bool CaptureBodies => _captureBodies;

        /// <summary>Maximum encoded request body in bytes; 0 means unlimited.</summary>
        public long MaxRequestBytes => _maxRequestBytes;

        /// <summary>Maximum response body in bytes; 0 means unlimited.</summary>
        public long MaxResponseBytes => _maxResponseBytes;

        /// <summary>
        /// Library defaults used as the lowest precedence layer when no profile is authored
        /// (plan §5.6). Conservative on purpose: retry on, redirects same-origin, TLS validated,
        /// no caching, no body capture.
        /// </summary>
        /// <returns>A profile whose ID is <c>molca-library-default</c>.</returns>
        public static NetworkPolicyProfile CreateLibraryDefault()
        {
            return new NetworkPolicyProfile
            {
                _id = "molca-library-default",
                _displayName = "Library default"
            };
        }

        /// <summary>
        /// Creates a profile in code. Used by migration, import, and tests.
        /// </summary>
        /// <param name="id">Stable identifier; must satisfy <see cref="NetworkIds.IsValid"/>.</param>
        /// <param name="displayName">Human-readable name, or <c>null</c> to reuse <paramref name="id"/>.</param>
        internal static NetworkPolicyProfile Create(string id, string displayName)
        {
            return new NetworkPolicyProfile
            {
                _id = id,
                _displayName = displayName ?? id
            };
        }
    }
}
