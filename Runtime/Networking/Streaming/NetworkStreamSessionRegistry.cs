using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Networking.Streaming
{
    /// <summary>
    /// Owns every live streaming session.
    /// </summary>
    /// <remarks>
    /// Lives on <c>NetworkRuntimeSubsystem</c>, not in a static, so a domain reload or a subsystem
    /// teardown closes the sessions rather than leaving sockets open and reconnect loops running against
    /// a world that no longer exists. This is the "subsystem-owned" half of plan §6.7 — the sessions are
    /// held here, and the provider assets that reference them hold nothing.
    /// <para>
    /// Keyed by session id, which is the owning provider's id for provider-driven streams. Opening a
    /// second session under an id that is already live closes the first: a provider re-activated after a
    /// scene reload must not leave its previous connection running.
    /// </para>
    /// </remarks>
    public sealed class NetworkStreamSessionRegistry
    {
        private readonly Dictionary<string, NetworkStreamSession> _sessions =
            new Dictionary<string, NetworkStreamSession>(StringComparer.Ordinal);

        /// <summary>Live sessions.</summary>
        public IReadOnlyCollection<NetworkStreamSession> Sessions => _sessions.Values;

        /// <summary>Live session count.</summary>
        public int Count => _sessions.Count;

        /// <summary>Raised when a session is added, removed, or changes state.</summary>
        public event Action Changed;

        /// <summary>
        /// Registers a session, replacing and closing any session already under its id.
        /// </summary>
        /// <param name="session">The session to adopt.</param>
        /// <returns>The same session, for chaining onto a <c>RunAsync</c> call.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="session"/> is <c>null</c>.</exception>
        public NetworkStreamSession Open(NetworkStreamSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            Close(session.Id);

            _sessions[session.Id] = session;
            session.StateChanged += OnSessionStateChanged;
            Changed?.Invoke();

            return session;
        }

        /// <summary>The session under an id, or <c>null</c>.</summary>
        /// <param name="id">The session id.</param>
        public NetworkStreamSession Find(string id) =>
            !string.IsNullOrEmpty(id) && _sessions.TryGetValue(id, out var session) ? session : null;

        /// <summary>
        /// Stops, disposes, and forgets a session.
        /// </summary>
        /// <param name="id">The session id.</param>
        /// <returns><c>true</c> when a session was closed.</returns>
        public bool Close(string id)
        {
            var session = Find(id);
            if (session == null) return false;

            _sessions.Remove(id);
            session.StateChanged -= OnSessionStateChanged;
            Dispose(session);
            Changed?.Invoke();

            return true;
        }

        /// <summary>Closes every session. Called on subsystem teardown.</summary>
        public void CloseAll()
        {
            if (_sessions.Count == 0) return;

            // Copied first: disposing a session can synchronously unwind its loop, and a loop that
            // closes its own session would mutate the dictionary being enumerated.
            var live = new List<NetworkStreamSession>(_sessions.Values);
            _sessions.Clear();

            foreach (var session in live)
            {
                session.StateChanged -= OnSessionStateChanged;
                Dispose(session);
            }

            Changed?.Invoke();
        }

        private void OnSessionStateChanged(NetworkStreamSession session) => Changed?.Invoke();

        private static void Dispose(NetworkStreamSession session)
        {
            try
            {
                session.Dispose();
            }
            catch (Exception e)
            {
                // A protocol handle that throws on close must not stop the other sessions from closing.
                Debug.LogWarning($"[Network] Stream session '{session.Id}' threw on close: {e.Message}");
            }
        }
    }
}
