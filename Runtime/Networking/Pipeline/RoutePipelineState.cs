using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Molca.Networking.Configuration;
using Molca.Networking.Routing;

namespace Molca.Networking.Pipeline
{
    /// <summary>State of a route's circuit breaker.</summary>
    public enum NetworkCircuitState
    {
        /// <summary>Requests flow normally.</summary>
        Closed = 0,

        /// <summary>The failure threshold was reached; requests fail fast until the reset window elapses.</summary>
        Open,

        /// <summary>The reset window elapsed; one trial request is admitted to test recovery.</summary>
        HalfOpen
    }

    /// <summary>
    /// Per-route runtime state: the concurrency bulkhead, the queue-depth bound, and the circuit
    /// breaker.
    /// </summary>
    /// <remarks>
    /// Scoped per route rather than per process, which is the point of the bulkhead: a slow or failing
    /// service must not exhaust the budget of an unrelated one (plan §6.4 step 6). One instance per
    /// <see cref="NetworkRouteKey"/>, owned by <see cref="NetworkRouteStateStore"/>.
    /// <para>
    /// Main-thread only. Counters are plain fields because every mutation happens on the Awaitable
    /// continuation chain, which Unity runs on the main thread.
    /// </para>
    /// </remarks>
    public sealed class RoutePipelineState
    {
        private int _active;
        private int _waiting;
        private int _consecutiveFailures;
        private DateTime _openedUtc = DateTime.MinValue;
        private bool _halfOpenTrialInFlight;

        /// <summary>The route this state belongs to.</summary>
        public NetworkRouteKey Route { get; }

        /// <summary>Requests currently on the wire for this route.</summary>
        public int ActiveCount => _active;

        /// <summary>
        /// Requests waiting for a concurrency slot on this route. Excludes the ones already on the wire —
        /// a request leaves the queue the moment it takes a slot.
        /// </summary>
        public int WaitingCount => _waiting;

        /// <summary>Consecutive failures since the last success.</summary>
        public int ConsecutiveFailures => _consecutiveFailures;

        /// <summary>Creates state for one route.</summary>
        /// <param name="route">The route this state tracks.</param>
        public RoutePipelineState(NetworkRouteKey route)
        {
            Route = route;
        }

        /// <summary>
        /// The circuit's state at <paramref name="nowUtc"/> under a policy.
        /// </summary>
        /// <param name="policy">The effective policy supplying the threshold and reset window.</param>
        /// <param name="nowUtc">The current UTC time.</param>
        /// <returns>The state; always <see cref="NetworkCircuitState.Closed"/> when the breaker is disabled.</returns>
        public NetworkCircuitState CircuitStateAt(NetworkEffectivePolicy policy, DateTime nowUtc)
        {
            int threshold = policy.CircuitFailureThreshold.Value;
            if (threshold <= 0 || _consecutiveFailures < threshold)
                return NetworkCircuitState.Closed;

            double resetSeconds = policy.CircuitResetSeconds.Value;
            if (resetSeconds > 0d && (nowUtc - _openedUtc).TotalSeconds >= resetSeconds)
                return NetworkCircuitState.HalfOpen;

            return NetworkCircuitState.Open;
        }

        /// <summary>
        /// Whether a request may proceed past the circuit breaker.
        /// </summary>
        /// <param name="policy">The effective policy.</param>
        /// <param name="nowUtc">The current UTC time.</param>
        /// <param name="reason">Why the request was rejected, or <c>null</c> when admitted.</param>
        /// <returns><c>true</c> when the request may proceed.</returns>
        /// <remarks>
        /// In the half-open state exactly one trial request is admitted. Admitting several would defeat
        /// the point: the breaker exists so a recovering service is probed, not stampeded.
        /// </remarks>
        public bool TryPassCircuit(NetworkEffectivePolicy policy, DateTime nowUtc, out string reason)
        {
            switch (CircuitStateAt(policy, nowUtc))
            {
                case NetworkCircuitState.Closed:
                    reason = null;
                    return true;

                case NetworkCircuitState.HalfOpen:
                    if (_halfOpenTrialInFlight)
                    {
                        reason = $"Circuit for {Route} is half-open and a trial request is already in flight.";
                        return false;
                    }
                    _halfOpenTrialInFlight = true;
                    reason = null;
                    return true;

                default:
                    reason =
                        $"Circuit for {Route} is open after {_consecutiveFailures} consecutive failures; " +
                        $"it retries in {Math.Max(0d, policy.CircuitResetSeconds.Value - (nowUtc - _openedUtc).TotalSeconds):F1}s.";
                    return false;
            }
        }

        /// <summary>
        /// Reserves a queue position, refusing when the route's queue bound is already reached.
        /// </summary>
        /// <param name="policy">The effective policy supplying the queue bound.</param>
        /// <param name="reason">Why the request was refused, or <c>null</c> on success.</param>
        /// <returns><c>true</c> when a position was reserved; release it with <see cref="LeaveQueue"/>.</returns>
        /// <remarks>
        /// Failing fast on an overfull queue is deliberate. Queueing without bound converts a backend
        /// outage into unbounded memory growth and response times nobody is still waiting for.
        /// </remarks>
        public bool TryEnterQueue(NetworkEffectivePolicy policy, out string reason)
        {
            int maxDepth = policy.MaxQueueDepth.Value;
            if (maxDepth > 0 && _waiting >= maxDepth)
            {
                reason = $"Queue for {Route} is full ({_waiting}/{maxDepth} waiting).";
                return false;
            }

            _waiting++;
            reason = null;
            return true;
        }

        /// <summary>Releases a queue position reserved by <see cref="TryEnterQueue"/>.</summary>
        public void LeaveQueue()
        {
            if (_waiting > 0) _waiting--;
        }

        /// <summary>
        /// Occupies a concurrency slot if one is free.
        /// </summary>
        /// <param name="policy">The effective policy supplying the concurrency limit.</param>
        /// <returns><c>true</c> when a slot was taken; release it with <see cref="ReleaseSlot"/>.</returns>
        /// <remarks>
        /// Non-blocking by design. The caller polls this per frame, which keeps the cancellation and
        /// overall-deadline checks in one place — and means a cancelled request leaves the queue within a
        /// frame and is never handed to the transport, satisfying the immediate-queued-cancellation
        /// requirement in plan §6.4.
        /// <para>
        /// Safe as an unsynchronized check-then-increment because every caller runs on the main thread,
        /// on the Awaitable continuation chain.
        /// </para>
        /// </remarks>
        public bool TryOccupySlot(NetworkEffectivePolicy policy)
        {
            int limit = policy.MaxConcurrentRequests.Value;
            if (limit > 0 && _active >= limit)
                return false;

            _active++;
            return true;
        }

        /// <summary>Releases a slot taken by <see cref="AcquireSlotAsync"/>.</summary>
        public void ReleaseSlot()
        {
            if (_active > 0) _active--;
        }

        /// <summary>Records a successful send, closing the circuit.</summary>
        public void RecordSuccess()
        {
            _consecutiveFailures = 0;
            _openedUtc = DateTime.MinValue;
            _halfOpenTrialInFlight = false;
        }

        /// <summary>
        /// Records a failed send, opening the circuit when the threshold is reached.
        /// </summary>
        /// <param name="policy">The effective policy supplying the threshold.</param>
        /// <param name="nowUtc">The current UTC time.</param>
        public void RecordFailure(NetworkEffectivePolicy policy, DateTime nowUtc)
        {
            _halfOpenTrialInFlight = false;
            _consecutiveFailures++;

            // Stamp the open time on every threshold-crossing failure, not just the first: a failed
            // half-open trial has to restart the reset window rather than leaving it already elapsed.
            int threshold = policy.CircuitFailureThreshold.Value;
            if (threshold > 0 && _consecutiveFailures >= threshold)
                _openedUtc = nowUtc;
        }

        /// <summary>
        /// Records an outcome that must not move the circuit — a cancellation, or a configuration or
        /// security rejection.
        /// </summary>
        /// <remarks>
        /// A cancelled request says nothing about the service's health, and neither does a request the
        /// pipeline refused to send. Counting either toward the breaker would open circuits for reasons
        /// the backend had no part in.
        /// </remarks>
        public void RecordNeutral()
        {
            _halfOpenTrialInFlight = false;
        }
    }

    /// <summary>
    /// Owns one <see cref="RoutePipelineState"/> per route.
    /// </summary>
    /// <remarks>
    /// Lives on the network subsystem, not in a static — so a domain reload or a subsystem teardown
    /// discards queue and circuit state instead of carrying a stale breaker into the next session.
    /// </remarks>
    public sealed class NetworkRouteStateStore
    {
        private readonly Dictionary<NetworkRouteKey, RoutePipelineState> _states =
            new Dictionary<NetworkRouteKey, RoutePipelineState>();

        /// <summary>State for a route, created on first use.</summary>
        /// <param name="route">The route to look up.</param>
        public RoutePipelineState For(NetworkRouteKey route)
        {
            if (_states.TryGetValue(route, out var state))
                return state;

            state = new RoutePipelineState(route);
            _states[route] = state;
            return state;
        }

        /// <summary>Every route with tracked state, for diagnostics.</summary>
        public IEnumerable<RoutePipelineState> All => _states.Values;

        /// <summary>Discards all state.</summary>
        public void Clear() => _states.Clear();
    }
}
