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

        private readonly System.Random _rng;
        private int _attempt;

        public StreamReconnectPolicy(float baseDelaySeconds, float maxDelaySeconds, int maxAttempts, System.Random rng = null)
        {
            BaseDelaySeconds = Mathf.Max(0f, baseDelaySeconds);
            MaxDelaySeconds = Mathf.Max(0f, maxDelaySeconds);
            MaxAttempts = Mathf.Max(0, maxAttempts);
            _rng = rng ?? new System.Random();
        }

        /// <summary>Attempts consumed since the last <see cref="Reset"/>.</summary>
        public int AttemptCount => _attempt;

        /// <summary>Whether another attempt is allowed under <see cref="MaxAttempts"/>.</summary>
        public bool CanRetry => MaxAttempts <= 0 || _attempt < MaxAttempts;

        /// <summary>Clears the attempt counter; call after a successful (re)connect.</summary>
        public void Reset() => _attempt = 0;

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
    }
}
