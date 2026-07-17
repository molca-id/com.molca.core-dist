using System;
using System.Threading;
using UnityEngine;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Shared reconnection schedule for the streaming data providers (WebSocket, SSE,
    /// SocketIO): exponential backoff with full jitter, a delay cap, and a bounded
    /// attempt count. Replaces the providers' ad-hoc fixed-delay loops with one
    /// consistent, cancellation-aware policy.
    /// </summary>
    /// <remarks>
    /// Stateful: tracks how many attempts have been consumed since the last
    /// <see cref="Reset"/> (call it after a successful connect). Backoff timing is pure
    /// (<see cref="ComputeDelaySeconds"/>) and unit-testable. Not thread-safe; drive it
    /// from a single provider loop keyed on <c>LifetimeToken</c>/<c>ShutdownToken</c>.
    /// </remarks>
    public sealed class StreamReconnectPolicy
    {
        /// <summary>First-attempt delay in seconds; doubles each attempt before jitter/cap.</summary>
        public float BaseDelaySeconds { get; }

        /// <summary>Upper bound on the (pre-jitter) delay; <c>0</c> means no cap.</summary>
        public float MaxDelaySeconds { get; }

        /// <summary>Maximum reconnect attempts; <c>0</c> means unbounded (still backed-off).</summary>
        public int MaxAttempts { get; }

        /// <summary>
        /// Minimum connection lifetime (seconds) before a drop counts as "the connection
        /// was healthy" and resets the attempt budget via <see cref="OnConnectionEnded"/>.
        /// <c>0</c> preserves the legacy behavior: any established connection resets.
        /// </summary>
        public float StableResetSeconds { get; }

        private readonly System.Random _rng;
        private int _attempt;

        public StreamReconnectPolicy(float baseDelaySeconds, float maxDelaySeconds, int maxAttempts, System.Random rng = null, float stableResetSeconds = 0f)
        {
            BaseDelaySeconds = Mathf.Max(0f, baseDelaySeconds);
            MaxDelaySeconds = Mathf.Max(0f, maxDelaySeconds);
            MaxAttempts = Mathf.Max(0, maxAttempts);
            StableResetSeconds = Mathf.Max(0f, stableResetSeconds);
            _rng = rng ?? new System.Random();
        }

        /// <summary>Attempts consumed since the last <see cref="Reset"/>.</summary>
        public int AttemptCount => _attempt;

        /// <summary>Whether another attempt is allowed under <see cref="MaxAttempts"/>.</summary>
        public bool CanRetry => MaxAttempts <= 0 || _attempt < MaxAttempts;

        /// <summary>Clears the attempt counter; call after a successful (re)connect.</summary>
        public void Reset() => _attempt = 0;

        /// <summary>
        /// Reports that an established connection ended after
        /// <paramref name="connectedDurationSeconds"/>. Resets the attempt budget only
        /// when the connection outlived <see cref="StableResetSeconds"/> — an
        /// accept-then-drop server (connects fine, dies instantly) must keep consuming
        /// the backoff budget instead of retrying at full speed forever.
        /// </summary>
        /// <param name="connectedDurationSeconds">How long the connection was up, in seconds.</param>
        /// <returns><c>true</c> when the budget was reset (the connection counted as stable).</returns>
        public bool OnConnectionEnded(float connectedDurationSeconds)
        {
            if (connectedDurationSeconds < StableResetSeconds)
                return false;

            Reset();
            return true;
        }

        /// <summary>
        /// Full-jitter exponential backoff for a 0-based attempt: a random point in
        /// <c>[0, min(base · 2^attempt, max)]</c>. Pure — no internal state touched.
        /// </summary>
        public static float ComputeDelaySeconds(int attempt, float baseDelaySeconds, float maxDelaySeconds, System.Random rng)
        {
            if (baseDelaySeconds <= 0f)
                return 0f;
            // Clamp the shift so 2^attempt can't overflow the int after many attempts.
            int shift = Mathf.Clamp(attempt, 0, 30);
            float exp = baseDelaySeconds * (1 << shift);
            float cap = maxDelaySeconds > 0f ? Mathf.Min(exp, maxDelaySeconds) : exp;
            double fraction = rng?.NextDouble() ?? 1.0;
            return (float)(fraction * cap);
        }

        /// <summary>The delay (with jitter) that <see cref="WaitForNextAttemptAsync"/> would use next.</summary>
        public float PeekNextDelaySeconds() => ComputeDelaySeconds(_attempt, BaseDelaySeconds, MaxDelaySeconds, _rng);

        /// <summary>
        /// Waits the backoff delay for the next attempt and consumes one attempt.
        /// Returns <c>false</c> immediately (no wait) when the attempt budget is
        /// exhausted. Honors <paramref name="cancellationToken"/>; cancellation rethrows.
        /// </summary>
        public async Awaitable<bool> WaitForNextAttemptAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanRetry)
                return false;

            float delay = ComputeDelaySeconds(_attempt, BaseDelaySeconds, MaxDelaySeconds, _rng);
            _attempt++;

            if (delay > 0f)
                await Awaitable.WaitForSecondsAsync(delay, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        /// <summary>
        /// Waits a caller-supplied delay (e.g. a server <c>retry:</c> directive) instead
        /// of the computed backoff, while still consuming one attempt from the budget —
        /// a server directive shapes the delay but must not grant unlimited retries.
        /// Returns <c>false</c> immediately (no wait) when the budget is exhausted.
        /// Honors <paramref name="cancellationToken"/>; cancellation rethrows.
        /// </summary>
        /// <param name="overrideDelaySeconds">The delay to wait; clamped at 0.</param>
        /// <param name="cancellationToken">Aborts the wait.</param>
        public async Awaitable<bool> WaitForNextAttemptAsync(float overrideDelaySeconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanRetry)
                return false;

            _attempt++;

            if (overrideDelaySeconds > 0f)
                await Awaitable.WaitForSecondsAsync(overrideDelaySeconds, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }
}
