using System;
using System.Collections.Generic;
using UnityEngine;
using Molca.Networking.Diagnostics;

namespace Molca.Networking.Pipeline
{
    /// <summary>
    /// Observes routed sends. Implementations must not throw; one that does is recorded and ignored.
    /// </summary>
    public interface IRoutedHttpObserver
    {
        /// <summary>Called when a request is handed to the transport for its first attempt.</summary>
        /// <param name="request">The resolved request.</param>
        void OnRequestStarted(ResolvedHttpRequest request);

        /// <summary>Called once when a send reaches a final outcome, successful or not.</summary>
        /// <param name="request">The resolved request.</param>
        /// <param name="outcome">The final outcome.</param>
        void OnRequestCompleted(ResolvedHttpRequest request, RoutedHttpOutcome outcome);
    }

    /// <summary>
    /// Notifies observers with each callback isolated, so a throwing observer cannot change a request's
    /// completion or prevent the observers after it from being notified.
    /// </summary>
    /// <remarks>
    /// Plan §6.4: observer exceptions are recorded separately and cannot change request completion. That
    /// matters because an observer is usually project code — a telemetry sink or a UI hook — and a bug
    /// there must not turn a successful request into a failed one.
    /// <para>
    /// Iterates a copy, so an observer that unsubscribes (or subscribes) during notification does not
    /// invalidate the enumeration.
    /// </para>
    /// </remarks>
    public sealed class NetworkObserverDispatcher
    {
        private readonly List<IRoutedHttpObserver> _observers = new List<IRoutedHttpObserver>();
        private readonly NetworkDiagnosticStore _diagnostics;

        /// <summary>Observers currently registered.</summary>
        public int Count => _observers.Count;

        /// <summary>Creates a dispatcher.</summary>
        /// <param name="diagnostics">Store that counts observer failures, or <c>null</c>.</param>
        public NetworkObserverDispatcher(NetworkDiagnosticStore diagnostics = null)
        {
            _diagnostics = diagnostics;
        }

        /// <summary>Registers an observer. Duplicate registrations are ignored.</summary>
        /// <param name="observer">The observer to add.</param>
        public void Add(IRoutedHttpObserver observer)
        {
            if (observer == null || _observers.Contains(observer)) return;
            _observers.Add(observer);
        }

        /// <summary>Removes an observer. No-op when absent.</summary>
        /// <param name="observer">The observer to remove.</param>
        /// <returns><c>true</c> when one was removed.</returns>
        public bool Remove(IRoutedHttpObserver observer) => _observers.Remove(observer);

        /// <summary>Removes every observer.</summary>
        public void Clear() => _observers.Clear();

        /// <summary>Notifies observers that a request started.</summary>
        /// <param name="request">The resolved request.</param>
        public void NotifyStarted(ResolvedHttpRequest request) =>
            Dispatch(observer => observer.OnRequestStarted(request), nameof(IRoutedHttpObserver.OnRequestStarted));

        /// <summary>Notifies observers that a request completed.</summary>
        /// <param name="request">The resolved request.</param>
        /// <param name="outcome">The final outcome.</param>
        public void NotifyCompleted(ResolvedHttpRequest request, RoutedHttpOutcome outcome) =>
            Dispatch(observer => observer.OnRequestCompleted(request, outcome), nameof(IRoutedHttpObserver.OnRequestCompleted));

        private void Dispatch(Action<IRoutedHttpObserver> callback, string callbackName)
        {
            if (_observers.Count == 0) return;

            // Snapshot: an observer is allowed to unsubscribe from inside its own callback, which would
            // otherwise mutate the list mid-enumeration.
            var snapshot = _observers.ToArray();

            foreach (var observer in snapshot)
            {
                try
                {
                    callback(observer);
                }
                catch (Exception e)
                {
                    _diagnostics?.RecordObserverFailure();
                    Debug.LogError(
                        $"[Network] Observer '{observer.GetType().Name}.{callbackName}' threw and was " +
                        $"ignored; the request is unaffected: {e}");
                }
            }
        }
    }
}
