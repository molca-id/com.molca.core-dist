namespace Molca.Networking.Configuration
{
    /// <summary>
    /// An explicit per-send policy override — the highest precedence layer. Every field is optional;
    /// an unset field inherits.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <see cref="NetworkPolicyProfile"/>: a per-send override may only adjust the
    /// operational knobs below. It carries no TLS-validation, secure-transport, or credential field,
    /// so a call site has no vocabulary in which to weaken a security rule. The one security-adjacent
    /// field it does expose, <see cref="RedirectMode"/>, is tighten-only — passing a looser mode than
    /// the inherited one has no effect (plan §5.6).
    /// </remarks>
    public sealed class NetworkSendPolicyOverride
    {
        /// <summary>Wall-clock budget for the whole send, or <c>null</c> to inherit.</summary>
        public float? OverallTimeoutSeconds { get; set; }

        /// <summary>Budget for one transport attempt, or <c>null</c> to inherit.</summary>
        public float? AttemptTimeoutSeconds { get; set; }

        /// <summary>Whether retries happen, or <c>null</c> to inherit.</summary>
        public bool? RetryEnabled { get; set; }

        /// <summary>Retry attempts after the initial send, or <c>null</c> to inherit.</summary>
        public int? MaxRetries { get; set; }

        /// <summary>
        /// Redirect handling, or <c>null</c> to inherit. Tighten-only: a looser mode than the
        /// inherited one is ignored.
        /// </summary>
        public NetworkRedirectMode? RedirectMode { get; set; }

        /// <summary>Whether this request is recorded in diagnostics, or <c>null</c> to inherit.</summary>
        public bool? LogRequests { get; set; }

        /// <summary>Whether redacted bodies are captured, or <c>null</c> to inherit.</summary>
        public bool? CaptureBodies { get; set; }

        /// <summary>
        /// Idempotency key marking a mutating request as safe to retry, or <c>null</c> when none.
        /// </summary>
        /// <remarks>
        /// Presence of a key is what lets a mutating call pass
        /// <see cref="NetworkPolicyProfile.RetryRequiresIdempotence"/>. The key itself is echoed to the
        /// server in a header by the pipeline; it is not a credential and is not redacted.
        /// </remarks>
        public string IdempotencyKey { get; set; }

        /// <summary>Whether any field on this override is set.</summary>
        public bool HasAnyValue =>
            OverallTimeoutSeconds.HasValue ||
            AttemptTimeoutSeconds.HasValue ||
            RetryEnabled.HasValue ||
            MaxRetries.HasValue ||
            RedirectMode.HasValue ||
            LogRequests.HasValue ||
            CaptureBodies.HasValue ||
            !string.IsNullOrEmpty(IdempotencyKey);
    }
}
