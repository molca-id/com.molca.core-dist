#if MOLCA_SOCKETIO
using System;
using System.Collections.Generic;
using System.Threading;
using SocketIOClient;
using SocketIOClient.Newtonsoft.Json;
using SocketIOClient.Transport;
using UnityEngine;
using Molca.Networking.Auth;
using Molca.Networking.Configuration;
using Molca.Networking.Routing;
using Molca.Networking.Security;
using Molca.Networking.Streaming;

namespace Molca.Networking.Data
{
    /// <summary>Immutable connection settings a <see cref="SocketIoStreamSession"/> reads once.</summary>
    public sealed class SocketIoSessionOptions
    {
        /// <summary>Handshake path; defaults to <c>/socket.io</c> when empty.</summary>
        public string SocketPath = "/socket.io";

        /// <summary>Seconds to wait for a handshake before treating the attempt as failed.</summary>
        public float ConnectionTimeoutSeconds = 20f;

        /// <summary>Whether the connection must carry a credential.</summary>
        public bool RequireAuthentication;

        /// <summary>Header to carry the token in, or empty to use the query parameter instead.</summary>
        public string AuthHeaderName = "Authorization";

        /// <summary>Prefix prepended to the token value, e.g. <c>"Bearer "</c>.</summary>
        public string AuthScheme = string.Empty;

        /// <summary>Query parameter to carry the token in, or empty to use a header.</summary>
        public string AuthQueryParameter = string.Empty;

        /// <summary>Event names to subscribe to.</summary>
        public IReadOnlyList<string> Events = Array.Empty<string>();

        /// <summary>Log connection lifecycle events.</summary>
        public bool LogEvents;
    }

    /// <summary>
    /// A Socket.IO connection owned by the network subsystem.
    /// </summary>
    /// <remarks>
    /// Everything mutable lives here: the socket handle, the connecting flag, and whether the last
    /// failure looked like a rejected credential. The provider asset holds none of it.
    /// <para>
    /// <b>The library's own reconnection is switched off.</b> Socket.IO can retry by itself, but it reuses
    /// the headers built when the socket was constructed — which is why the previous implementation
    /// carried a hook that tore the socket down mid-reconnect whenever the auth token had changed
    /// underneath it. Letting <see cref="NetworkStreamSession"/> own reconnection removes that entirely:
    /// every attempt is a fresh connect with a freshly resolved route and a freshly acquired credential,
    /// and the backoff, jitter, attempt budget, and stable-connection window are the same ones SSE and
    /// WebSocket get.
    /// </para>
    /// </remarks>
    public sealed class SocketIoStreamSession : NetworkStreamSession
    {
        private readonly SocketIoSessionOptions _options;

        private SocketIOUnity _socket;
        private bool _authShapedFailure;
        private bool _closedByUs;

        /// <summary>Whether the socket is connected.</summary>
        public bool IsOpen => _socket != null && _socket.Connected;

        /// <summary>Raised for each subscribed event: the event name and its raw JSON payload.</summary>
        /// <remarks>
        /// Its own event rather than the base's <c>MessageReceived</c>, because a Socket.IO frame is a
        /// named event and flattening the name away would force every consumer to re-parse it out of the
        /// payload. §6.7 warns against a lowest-common-denominator transport for exactly this reason.
        /// </remarks>
        public event Action<string, SocketIOResponse> EventReceived;

        /// <summary>Creates a session.</summary>
        /// <param name="id">Stable identifier, usually the owning provider's ID.</param>
        /// <param name="route">The catalog route, or default for a direct URL.</param>
        /// <param name="resolver">The shared route resolver.</param>
        /// <param name="credentials">The shared credential registry.</param>
        /// <param name="options">Connection settings, snapshotted for the session's lifetime.</param>
        /// <param name="reconnect">Reconnect settings, or <c>null</c> for the defaults.</param>
        /// <param name="directUri">A directly authored URI, used when <paramref name="route"/> names no service.</param>
        public SocketIoStreamSession(
            string id,
            NetworkStreamRoute route,
            INetworkRouteResolver resolver,
            NetworkCredentialRegistry credentials,
            SocketIoSessionOptions options = null,
            StreamReconnectSettings reconnect = null,
            string directUri = null)
            : base(id, route, NetworkProtocols.SocketIO, resolver, credentials, reconnect, directUri)
        {
            _options = options ?? new SocketIoSessionOptions();
        }

        /// <inheritdoc />
        protected override async Awaitable<NetworkStreamAttempt> ConnectAndPumpAsync(
            NetworkStreamBinding binding,
            NetworkCredential credential,
            CancellationToken cancellationToken)
        {
            _authShapedFailure = false;
            _closedByUs = false;

            if (!Uri.TryCreate(binding.Uri, UriKind.Absolute, out var uri))
                return new NetworkStreamAttempt(false, error: $"'{binding.Uri}' is not a valid Socket.IO URI.");

            string token = credential.HasValue ? credential.Value : ReadExternalToken(binding);

            // A plain flag rather than a completion source: an Awaitable is single-consumption, so it
            // cannot be polled, and the callbacks below arrive on the main thread alongside this loop.
            bool ended = false;
            bool established = false;
            double connectedAt = 0d;
            string endReason = null;

            _socket = new SocketIOUnity(uri, BuildOptions(binding, token));
            _socket.JsonSerializer = new NewtonsoftJsonSerializer();

            _socket.OnConnected += (_, __) =>
            {
                established = true;
                connectedAt = Time.realtimeSinceStartupAsDouble;
                MarkConnected();

                if (_options.LogEvents)
                    Debug.Log($"[Network] Socket.IO '{Id}' connected.");
            };

            _socket.OnDisconnected += (_, reason) =>
            {
                endReason ??= $"disconnected ({reason})";
                if (IsAuthShaped(reason)) _authShapedFailure = true;
                ended = true;
            };

            _socket.OnError += (_, error) =>
            {
                endReason ??= error;
                if (IsAuthShaped(error)) _authShapedFailure = true;
                ended = true;
            };

            foreach (string eventName in _options.Events)
            {
                if (string.IsNullOrEmpty(eventName)) continue;

                string captured = eventName;
                _socket.OnUnityThread(captured, response => Raise(captured, response));
            }

            _socket.Connect();

            // The handshake has no completion signal of its own, so the deadline is watched here rather
            // than left to the library's ConnectionTimeout — which reports through OnError inconsistently
            // across transports.
            double deadline = Time.realtimeSinceStartupAsDouble +
                              Mathf.Max(1f, _options.ConnectionTimeoutSeconds);

            while (!ended)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_socket == null || _closedByUs)
                    break;

                if (!established && Time.realtimeSinceStartupAsDouble >= deadline)
                {
                    endReason = $"handshake did not complete within {_options.ConnectionTimeoutSeconds:0.#}s";
                    break;
                }

                await Awaitable.NextFrameAsync(cancellationToken);
            }

            float connectedSeconds = established
                ? Mathf.Max(0f, (float)(Time.realtimeSinceStartupAsDouble - connectedAt))
                : 0f;

            if (_closedByUs)
                return new NetworkStreamAttempt(established, false, connectedSeconds);

            return new NetworkStreamAttempt(
                established, _authShapedFailure, connectedSeconds,
                endReason == null ? null : $"Socket.IO ended: {endReason}");
        }

        /// <summary>
        /// Emits an event on the live connection.
        /// </summary>
        /// <param name="eventName">The event name.</param>
        /// <param name="payloadJson">A JSON payload, or <c>null</c> to emit with no data.</param>
        /// <returns><c>false</c> when there is no open connection to emit on.</returns>
        public bool Emit(string eventName, string payloadJson = null)
        {
            if (!IsOpen)
            {
                Debug.LogWarning($"[Network] Socket.IO '{Id}' is not connected; '{eventName}' was not emitted.");
                return false;
            }

            try
            {
                if (string.IsNullOrEmpty(payloadJson))
                    _socket.Emit(eventName);
                else
                    _socket.EmitStringAsJSON(eventName, payloadJson);

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Network] Socket.IO '{Id}' could not emit '{eventName}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Closes the current connection so the session reconnects immediately.
        /// </summary>
        /// <remarks>
        /// The reconnect still runs through the shared backoff and attempt budget, so a manual retry
        /// cannot be used to bypass them.
        /// </remarks>
        public void DropConnection()
        {
            _closedByUs = true;
            ReleaseConnection();
        }

        /// <inheritdoc />
        protected override void ReleaseConnection()
        {
            var socket = _socket;
            _socket = null;

            if (socket == null) return;

            try
            {
                socket.Disconnect();
                socket.Dispose();
            }
            catch (Exception e)
            {
                Debug.Log($"[Network] Socket.IO '{Id}' close reported: {e.Message}");
            }
        }

        /// <inheritdoc />
        protected override async Awaitable<bool> TryRefreshExternalCredentialAsync(
            CancellationToken cancellationToken)
        {
            if (!_options.RequireAuthentication || AuthManager.Instance == null)
                return false;

            bool refreshed;
            try
            {
                refreshed = await AuthManager.Instance.RefreshAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Network] Socket.IO '{Id}' token refresh failed: {e.Message}");
                return false;
            }

            if (!refreshed)
            {
                AuthEvents.Expired.Dispatch(
                    new AuthExpiredEventData(AuthManager.Instance?.User?.GetUserId()));
            }

            return refreshed;
        }

        private void Raise(string eventName, SocketIOResponse response)
        {
            try
            {
                EventReceived?.Invoke(eventName, response);
            }
            catch (Exception e)
            {
                // A listener that throws is the owner's bug and must not take the connection down.
                Debug.LogError($"[Network] Socket.IO '{Id}' listener for '{eventName}' threw: {e.Message}");
            }
        }

        private SocketIOOptions BuildOptions(NetworkStreamBinding binding, string token)
        {
            var options = new SocketIOOptions
            {
                Path = string.IsNullOrEmpty(_options.SocketPath) ? "/socket.io" : _options.SocketPath,
                Transport = TransportProtocol.WebSocket,
                AutoUpgrade = false,
                ConnectionTimeout = TimeSpan.FromSeconds(Mathf.Max(1f, _options.ConnectionTimeoutSeconds)),

                // Off deliberately. The session owns reconnection so every attempt re-resolves the route
                // and re-acquires the credential; the library would reuse construction-time headers.
                Reconnection = false,
            };

            if (string.IsNullOrEmpty(token))
                return options;

            if (!string.IsNullOrEmpty(_options.AuthQueryParameter))
            {
                options.Query = new Dictionary<string, string>
                {
                    [_options.AuthQueryParameter] = token,
                };
                return options;
            }

            string header = binding.Credential != null && !binding.IsDirect
                ? binding.Credential.HeaderName
                : _options.AuthHeaderName;

            string scheme = binding.Credential != null && !binding.IsDirect
                ? binding.Credential.Scheme
                : _options.AuthScheme;

            if (!string.IsNullOrEmpty(header))
            {
                options.ExtraHeaders = new Dictionary<string, string>
                {
                    [header] = (scheme ?? string.Empty) + token,
                };
            }

            return options;
        }

        /// <summary>
        /// The token to use when the catalog supplied none.
        /// </summary>
        /// <remarks>
        /// Withheld on a routed binding whose credential profile is scoped away from the resolved host:
        /// the catalog's scope is a statement about the host, not about where the token came from.
        /// </remarks>
        private string ReadExternalToken(NetworkStreamBinding binding)
        {
            if (!_options.RequireAuthentication || AuthManager.Instance == null)
                return null;

            if (!binding.IsDirect && !NetworkStreamRouting.AllowsCredential(binding, out string reason))
            {
                Debug.LogWarning($"[Network] Socket.IO '{Id}': {reason}");
                return null;
            }

            return AuthManager.Instance.AuthToken;
        }

        private static bool IsAuthShaped(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;

            return message.IndexOf("401", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("403", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("forbidden", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
#endif
