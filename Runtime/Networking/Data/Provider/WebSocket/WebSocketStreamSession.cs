#if MOLCA_WEBSOCKET
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NativeWebSocket;
using UnityEngine;
using Molca.Networking.Auth;
using Molca.Networking.Configuration;
using Molca.Networking.Routing;
using Molca.Networking.Security;
using Molca.Networking.Streaming;

namespace Molca.Networking.Data
{
    /// <summary>Immutable connection settings a <see cref="WebSocketStreamSession"/> reads once.</summary>
    /// <remarks>
    /// A snapshot taken when the session opens, so editing the provider asset mid-session cannot change
    /// what the live connection is doing — the same freeze the request pipeline applies before queueing.
    /// </remarks>
    public sealed class WebSocketSessionOptions
    {
        /// <summary>Send a periodic keep-alive frame.</summary>
        public bool EnablePingPong = true;

        /// <summary>Seconds between keep-alive frames.</summary>
        public float PingIntervalSeconds = 30f;

        /// <summary>The keep-alive payload.</summary>
        public string PingMessage = "{\"type\":\"ping\"}";

        /// <summary>Seconds to wait for a handshake before treating the attempt as failed.</summary>
        public float ConnectionTimeoutSeconds = 30f;

        /// <summary>Whether the connection must carry a credential.</summary>
        public bool RequireAuthentication;

        /// <summary>Header to carry the token in, or empty to use the query parameter instead.</summary>
        public string AuthHeaderName = "Authorization";

        /// <summary>Prefix prepended to the token value, e.g. <c>"Bearer "</c>.</summary>
        public string AuthScheme = string.Empty;

        /// <summary>Query parameter to carry the token in, or empty to use a header.</summary>
        public string AuthQueryParameter = string.Empty;

        /// <summary>Log connection lifecycle events.</summary>
        public bool LogEvents;
    }

    /// <summary>
    /// A WebSocket connection owned by the network subsystem.
    /// </summary>
    /// <remarks>
    /// Everything mutable about the connection lives here: the socket handle, the connecting flag, the
    /// handshake deadline, the keep-alive timer, and whether the last failure looked like a rejected
    /// credential. The provider asset holds none of it. Before this existed, a provider wrote its own
    /// connection status and reconnect counter into serialized fields — a runtime asset mutation, and a
    /// way for two scenes sharing one provider to overwrite each other's state.
    /// <para>
    /// It implements the protocol and nothing else. Route resolution, the encrypted-transport check,
    /// credential scoping, bounded jittered backoff, the stable-connection window, and the give-up rules
    /// all come from <see cref="NetworkStreamSession"/> — the same code the SSE session runs.
    /// </para>
    /// <para>
    /// Frames arrive as raw text through <see cref="NetworkStreamSession.MessageReceived"/>. Parsing,
    /// filtering, and pong detection stay on the provider, because those are asset configuration: the
    /// session owns the connection, the provider owns interpretation.
    /// </para>
    /// </remarks>
    public sealed class WebSocketStreamSession : NetworkStreamSession
    {
        private readonly WebSocketSessionOptions _options;

        private WebSocket _socket;
        private bool _authShapedFailure;
        private bool _closedByUs;

        /// <summary>Whether the socket is open.</summary>
        public bool IsOpen => _socket != null && _socket.State == WebSocketState.Open;

        /// <summary>The raw socket state, for diagnostics.</summary>
        public WebSocketState SocketState => _socket?.State ?? WebSocketState.Closed;

        /// <summary>Creates a session.</summary>
        /// <param name="id">Stable identifier, usually the owning provider's ID.</param>
        /// <param name="route">The catalog route, or default for a direct URL.</param>
        /// <param name="resolver">The shared route resolver.</param>
        /// <param name="credentials">The shared credential registry.</param>
        /// <param name="options">Connection settings, snapshotted for the session's lifetime.</param>
        /// <param name="reconnect">Reconnect settings, or <c>null</c> for the defaults.</param>
        /// <param name="directUri">A directly authored URI, used when <paramref name="route"/> names no service.</param>
        public WebSocketStreamSession(
            string id,
            NetworkStreamRoute route,
            INetworkRouteResolver resolver,
            NetworkCredentialRegistry credentials,
            WebSocketSessionOptions options = null,
            StreamReconnectSettings reconnect = null,
            string directUri = null)
            : base(id, route, NetworkProtocols.WebSocket, resolver, credentials, reconnect, directUri)
        {
            _options = options ?? new WebSocketSessionOptions();
        }

        /// <inheritdoc />
        protected override async Awaitable<NetworkStreamAttempt> ConnectAndPumpAsync(
            NetworkStreamBinding binding,
            NetworkCredential credential,
            CancellationToken cancellationToken)
        {
            _authShapedFailure = false;
            _closedByUs = false;

            string token = credential.HasValue ? credential.Value : ReadExternalToken(binding);
            string uri = AppendQueryToken(binding.Uri, token);
            var headers = BuildHeaders(binding, token);

            _socket = headers.Count > 0 ? new WebSocket(uri, headers) : new WebSocket(uri);

            bool established = false;
            double connectedAt = 0d;
            string closeReason = null;

            _socket.OnOpen += OnOpen;
            _socket.OnMessage += OnMessage;
            _socket.OnError += OnError;
            _socket.OnClose += OnClose;

            void OnOpen()
            {
                established = true;
                connectedAt = Time.realtimeSinceStartupAsDouble;
                MarkConnected();

                if (_options.LogEvents)
                    Debug.Log($"[Network] WebSocket '{Id}' connected.");
            }

            void OnMessage(byte[] data)
            {
                try
                {
                    Dispatch(Encoding.UTF8.GetString(data));
                }
                catch (Exception e)
                {
                    // A frame that will not decode is one bad frame, not a dead connection.
                    Debug.LogWarning($"[Network] WebSocket '{Id}' could not decode a frame: {e.Message}");
                }
            }

            void OnError(string message)
            {
                closeReason = message;
                if (IsAuthShaped(message)) _authShapedFailure = true;
            }

            void OnClose(WebSocketCloseCode code)
            {
                closeReason ??= $"closed ({code})";

                // A WebSocket handshake rejected for auth has no typed status: it surfaces as a 401 in
                // the error text, or as a 1008 policy violation on close.
                if (code == WebSocketCloseCode.PolicyViolation) _authShapedFailure = true;
            }

            // NativeWebSocket's Connect() completes when the connection ends, so it is started rather
            // than awaited — the pump below has to keep running while it is open. Its Task is a
            // third-party boundary; nothing outside this method sees one.
            var connect = _socket.Connect();

            double deadline = Time.realtimeSinceStartupAsDouble +
                              Mathf.Max(1f, _options.ConnectionTimeoutSeconds);
            double nextPing = double.MaxValue;

            try
            {
                while (!connect.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_socket == null)
                        break;

#if !UNITY_WEBGL || UNITY_EDITOR
                    // Messages queue on a background thread and are raised from here, so every callback
                    // above runs on the main thread.
                    if (_socket.State == WebSocketState.Open)
                        _socket.DispatchMessageQueue();
#endif

                    double now = Time.realtimeSinceStartupAsDouble;

                    if (established)
                    {
                        if (nextPing > now + _options.PingIntervalSeconds)
                            nextPing = now + _options.PingIntervalSeconds;

                        if (_options.EnablePingPong && now >= nextPing)
                        {
                            nextPing = now + Mathf.Max(1f, _options.PingIntervalSeconds);
                            await SendTextAsync(_options.PingMessage);
                        }
                    }
                    else if (now >= deadline)
                    {
                        // The watchdog is outside the Open check on purpose: while connecting the state
                        // is not Open, so gating it on Open means it could never fire.
                        closeReason = $"handshake did not complete within {_options.ConnectionTimeoutSeconds:0.#}s";
                        break;
                    }

                    await Awaitable.NextFrameAsync(cancellationToken);
                }

                if (connect.IsFaulted && closeReason == null)
                    closeReason = connect.Exception?.GetBaseException().Message;
            }
            finally
            {
                _socket.OnOpen -= OnOpen;
                _socket.OnMessage -= OnMessage;
                _socket.OnError -= OnError;
                _socket.OnClose -= OnClose;
            }

            float connectedSeconds = established
                ? Mathf.Max(0f, (float)(Time.realtimeSinceStartupAsDouble - connectedAt))
                : 0f;

            // A close we asked for is not a failure and must not consume the reconnect budget.
            if (_closedByUs)
                return new NetworkStreamAttempt(established, false, connectedSeconds);

            return new NetworkStreamAttempt(
                established, _authShapedFailure, connectedSeconds,
                closeReason == null ? null : $"WebSocket ended: {closeReason}");
        }

        /// <summary>
        /// Sends a text frame on the live connection.
        /// </summary>
        /// <param name="message">The payload.</param>
        /// <returns>Completes once the frame is handed to the socket.</returns>
        /// <remarks>
        /// A send on a closed socket is a warning rather than an exception: a caller reacting to game
        /// state cannot know the connection dropped a frame ago, and throwing would turn a recoverable
        /// drop into an unhandled error at an unrelated call site.
        /// </remarks>
        public async Awaitable SendTextAsync(string message)
        {
            if (!IsOpen)
            {
                Debug.LogWarning($"[Network] WebSocket '{Id}' is not open; the message was not sent.");
                return;
            }

            try
            {
                await _socket.SendText(message);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Network] WebSocket '{Id}' could not send: {e.Message}");
            }
        }

        /// <summary>Sends a binary frame on the live connection.</summary>
        /// <param name="data">The payload.</param>
        /// <returns>Completes once the frame is handed to the socket.</returns>
        public async Awaitable SendBinaryAsync(byte[] data)
        {
            if (!IsOpen)
            {
                Debug.LogWarning($"[Network] WebSocket '{Id}' is not open; the data was not sent.");
                return;
            }

            try
            {
                await _socket.Send(data);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Network] WebSocket '{Id}' could not send: {e.Message}");
            }
        }

        /// <summary>
        /// Closes the current connection so the session reconnects immediately.
        /// </summary>
        /// <remarks>
        /// The manual-reconnect path. It closes rather than tearing the session down, so the reconnect
        /// runs through the same backoff and give-up rules an involuntary drop does — a manual retry that
        /// bypassed the budget would let a user hammer a failing server by holding a button.
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
                if (socket.State == WebSocketState.Open)
                    _ = socket.Close();
                else
                    socket.CancelConnection();
            }
            catch (Exception e)
            {
                // Closing a socket that already died is not worth reporting.
                Debug.Log($"[Network] WebSocket '{Id}' close reported: {e.Message}");
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// The provider's own authentication path, for a connection that is not carrying a catalog
        /// credential. One refresh per rejection episode; the base class enforces that.
        /// </remarks>
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
                Debug.LogWarning($"[Network] WebSocket '{Id}' token refresh failed: {e.Message}");
                return false;
            }

            if (!refreshed)
            {
                // The session is over. Telling the app the session expired is what lets it re-authenticate
                // the user, rather than leaving a dead stream that silently stopped delivering.
                AuthEvents.Expired.Dispatch(
                    new AuthExpiredEventData(AuthManager.Instance?.User?.GetUserId()));
            }

            return refreshed;
        }

        /// <summary>
        /// The token to use when the catalog supplied none.
        /// </summary>
        /// <remarks>
        /// Withheld on a routed binding whose credential profile is scoped away from the resolved host.
        /// The catalog's scope is a statement about the host, not about where the token came from, so a
        /// provider's own token does not get to ignore it.
        /// </remarks>
        private string ReadExternalToken(NetworkStreamBinding binding)
        {
            if (!_options.RequireAuthentication || AuthManager.Instance == null)
                return null;

            if (!binding.IsDirect && !NetworkStreamRouting.AllowsCredential(binding, out string reason))
            {
                Debug.LogWarning($"[Network] WebSocket '{Id}': {reason}");
                return null;
            }

            return AuthManager.Instance.AuthToken;
        }

        private Dictionary<string, string> BuildHeaders(NetworkStreamBinding binding, string token)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(_options.AuthQueryParameter))
                return headers;

            string header = binding.Credential != null && !binding.IsDirect
                ? binding.Credential.HeaderName
                : _options.AuthHeaderName;

            string scheme = binding.Credential != null && !binding.IsDirect
                ? binding.Credential.Scheme
                : _options.AuthScheme;

            if (!string.IsNullOrEmpty(header))
                headers[header] = (scheme ?? string.Empty) + token;

            return headers;
        }

        /// <summary>
        /// Appends the token as a query parameter, when that is how this connection authenticates.
        /// </summary>
        /// <remarks>
        /// A token in a query string is still a credential — and it is the one that ends up in access
        /// logs — so it goes through the same gate the header path does.
        /// </remarks>
        private string AppendQueryToken(string uri, string token)
        {
            if (string.IsNullOrEmpty(_options.AuthQueryParameter) || string.IsNullOrEmpty(token))
                return uri;

            char separator = uri.IndexOf('?') >= 0 ? '&' : '?';
            return $"{uri}{separator}{Uri.EscapeDataString(_options.AuthQueryParameter)}=" +
                   Uri.EscapeDataString(token);
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
