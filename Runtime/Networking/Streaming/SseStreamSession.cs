using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Molca.Networking.Auth;
using Molca.Networking.Configuration;
using Molca.Networking.Data;
using Molca.Networking.Routing;
using Molca.Networking.Security;

namespace Molca.Networking.Streaming
{
    /// <summary>
    /// A Server-Sent Events session on a catalog route.
    /// </summary>
    /// <remarks>
    /// Implements only the protocol: open with <c>Accept: text/event-stream</c>, resume with
    /// <c>Last-Event-ID</c>, feed chunks through <see cref="SSEEventStreamParser"/>, and honour a server
    /// <c>retry:</c> directive for exactly one wait. Route resolution, host and production checks,
    /// credential scoping, backoff, and give-up rules come from <see cref="NetworkStreamSession"/>.
    /// <para>
    /// Events are re-emitted in their canonical <c>event:</c>/<c>data:</c> block form, because the
    /// existing <c>JsonPreProcessor</c> chain downstream parses that shape and changing it would break
    /// every consumer that already works.
    /// </para>
    /// </remarks>
    public sealed class SseStreamSession : NetworkStreamSession
    {
        private readonly INetworkStreamTransport _transport;
        private readonly SSEEventStreamParser _parser = new SSEEventStreamParser();
        private readonly float _pollIntervalSeconds;
        private readonly string _authHeaderName;
        private readonly string _authScheme;

        private INetworkStreamConnection _connection;

        /// <summary>Creates a session.</summary>
        /// <param name="id">Stable identifier, usually the owning provider's ID.</param>
        /// <param name="route">Where to connect.</param>
        /// <param name="resolver">The shared route resolver.</param>
        /// <param name="credentials">The shared credential registry.</param>
        /// <param name="transport">The stream transport; <c>null</c> uses the UnityWebRequest one.</param>
        /// <param name="reconnect">Reconnect settings, or <c>null</c> for the defaults.</param>
        /// <param name="pollIntervalSeconds">Seconds between receive-buffer polls.</param>
        /// <param name="directUri">A directly authored URI, used when <paramref name="route"/> names no service.</param>
        /// <param name="authHeaderName">
        /// Header to carry an auth-session token in when the catalog supplies no credential, or
        /// <c>null</c> to stay anonymous.
        /// </param>
        /// <param name="authScheme">Prefix prepended to that token, e.g. <c>"Bearer "</c>.</param>
        public SseStreamSession(
            string id,
            NetworkStreamRoute route,
            INetworkRouteResolver resolver,
            NetworkCredentialRegistry credentials,
            INetworkStreamTransport transport = null,
            StreamReconnectSettings reconnect = null,
            float pollIntervalSeconds = 0.1f,
            string directUri = null,
            string authHeaderName = null,
            string authScheme = null)
            : base(id, route, NetworkProtocols.ServerSentEvents, resolver, credentials, reconnect, directUri)
        {
            _transport = transport ?? new UnityWebRequestStreamTransport();
            _pollIntervalSeconds = pollIntervalSeconds;
            _authHeaderName = authHeaderName;
            _authScheme = authScheme ?? string.Empty;
        }

        /// <inheritdoc />
        protected override async Awaitable<NetworkStreamAttempt> ConnectAndPumpAsync(
            NetworkStreamBinding binding,
            NetworkCredential credential,
            CancellationToken cancellationToken)
        {
            // A fresh stream position per attempt. LastEventId deliberately survives, because resuming
            // from it is the whole point of the header below.
            _parser.ResetStream();

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Accept", "text/event-stream"),
            };

            if (credential.HasValue)
            {
                var profile = binding.Credential;
                headers.Add(new KeyValuePair<string, string>(
                    profile.HeaderName, (profile.Scheme ?? string.Empty) + credential.Value));
            }
            else
            {
                string external = ReadExternalToken(binding);
                if (!string.IsNullOrEmpty(external))
                    headers.Add(new KeyValuePair<string, string>(_authHeaderName, _authScheme + external));
            }

            if (!string.IsNullOrEmpty(_parser.LastEventId))
                headers.Add(new KeyValuePair<string, string>("Last-Event-ID", _parser.LastEventId));

            _connection = await _transport.ConnectAsync(
                new NetworkStreamConnectRequest(binding.Uri, headers, _pollIntervalSeconds),
                cancellationToken);

            bool established = false;
            double connectedAt = 0d;

            while (await _connection.MoveNextAsync(cancellationToken))
            {
                if (!established)
                {
                    established = true;
                    connectedAt = Time.realtimeSinceStartupAsDouble;
                    MarkConnected();
                }

                foreach (var received in _parser.Feed(_connection.Current))
                    Dispatch(Canonical(received));
            }

            float connectedSeconds = established
                ? Mathf.Max(0f, (float)(Time.realtimeSinceStartupAsDouble - connectedAt))
                : 0f;

            long status = _connection.StatusCode;
            string error = _connection.Error;

            if (string.IsNullOrEmpty(error))
            {
                // A clean end with no payload still counts as established: the server accepted the
                // connection. It gets a zero duration, so it cannot clear the backoff budget unless the
                // stable window is 0 — which is what stops an accept-then-drop server from producing a
                // tight retry loop.
                return new NetworkStreamAttempt(true, false, connectedSeconds);
            }

            return new NetworkStreamAttempt(
                established,
                authRejected: status == 401 || status == 403,
                connectedSeconds,
                error: $"SSE stream ended: {error} (HTTP {status})");
        }

        /// <inheritdoc />
        protected override void ReleaseConnection()
        {
            _connection?.Dispose();
            _connection = null;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The provider's own authentication path, for a stream not carrying a catalog credential. One
        /// refresh per rejection episode; the base class enforces that.
        /// </remarks>
        protected override async Awaitable<bool> TryRefreshExternalCredentialAsync(
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_authHeaderName) || AuthManager.Instance == null)
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
                Debug.LogWarning($"[Network] SSE '{Id}' token refresh failed: {e.Message}");
                return false;
            }

            if (!refreshed)
            {
                // Telling the app the session expired is what lets it re-authenticate, rather than leaving
                // a dead stream that silently stopped delivering.
                AuthEvents.Expired.Dispatch(
                    new AuthExpiredEventData(AuthManager.Instance?.User?.GetUserId()));
            }

            return refreshed;
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
            if (string.IsNullOrEmpty(_authHeaderName) || AuthManager.Instance == null)
                return null;

            if (!binding.IsDirect && !NetworkStreamRouting.AllowsCredential(binding, out string reason))
            {
                Debug.LogWarning($"[Network] SSE '{Id}': {reason}");
                return null;
            }

            return AuthManager.Instance.AuthToken;
        }

        /// <summary>
        /// The server's <c>retry:</c> directive in seconds, consumed if present.
        /// </summary>
        /// <param name="seconds">The requested delay.</param>
        /// <returns><c>true</c> when the server asked for a specific delay.</returns>
        /// <remarks>
        /// Exposed rather than applied internally because a server directive shapes one wait but must
        /// still consume an attempt from the bounded budget — a <c>retry: 0</c> loop would otherwise
        /// disable the backoff entirely.
        /// </remarks>
        public bool TryConsumeServerRetry(out float seconds)
        {
            if (_parser.TryConsumeRetry(out int milliseconds))
            {
                seconds = milliseconds / 1000f;
                return true;
            }

            seconds = 0f;
            return false;
        }

        private static string Canonical(SSEEventStreamParser.SSEEvent received) =>
            string.IsNullOrEmpty(received.EventType)
                ? $"data: {received.Data}"
                : $"event: {received.EventType}\ndata: {received.Data}";
    }
}
