using System;
using System.Threading;
using Molca.Attributes;
using Molca.Networking.Auth;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Networking;

namespace Molca.Networking.Data
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "SSEProvider", menuName = "Molca/Networking/SSEProvider", order = 20)]
    public class SSEProvider : DataProvider
    {
        [Header("SSE Settings")]
        [SerializeField, FormerlySerializedAs("url")] private string _url;
        [Tooltip("Seconds between polls of the streaming download buffer.")]
        [SerializeField] private float _pollIntervalSeconds = 0.1f;

        [Header("Reconnection")]
        [SerializeField] private bool _autoReconnect = true;
        [Tooltip("First reconnect delay in seconds; grows exponentially with jitter up to the max.")]
        [SerializeField] private float _reconnectBaseDelaySeconds = 2f;
        [SerializeField] private float _reconnectMaxDelaySeconds = 30f;
        [Tooltip("0 = unbounded (still backed-off).")]
        [SerializeField] private int _maxReconnectAttempts = 0;
        [Tooltip("A connection must live this long before a drop resets the backoff budget; guards against accept-then-drop servers causing a fast retry loop. 0 = any established connection resets.")]
        [SerializeField] private float _stableConnectionSeconds = 10f;

        [Header("Authentication")]
        [Tooltip("Send the current auth token as a header; re-read on every (re)connect so a refreshed token is picked up.")]
        [SerializeField] private bool _sendAuthToken = false;
        [SerializeField] private string _authHeaderName = "Authorization";
        [Tooltip("Prefix prepended to the token value, e.g. 'Bearer '. Leave empty for a raw token.")]
        [SerializeField] private string _authScheme = "Bearer ";

        [Header("Debug")]
        [SerializeField, ReadOnly] private string _connectionStatus = "Disconnected";

        private UnityWebRequest _request;
        private SSEEventStreamParser _parser;

        public string ConnectionStatus => _connectionStatus;

        public override void Activate()
        {
            base.Activate();
            // Explicit fire-and-forget: the stream loop owns its exceptions and unwinds
            // on Deactivate via LifetimeToken.
            _ = RunStreamLoopAsync(LifetimeToken);
        }

        public override void Deactivate()
        {
            base.Deactivate();
            AbortRequest();
            _connectionStatus = "Disconnected";
        }

        public override void FetchData() { }

        /// <summary>Outcome of a single SSE connection, driving reconnect decisions.</summary>
        private readonly struct StreamOutcome
        {
            /// <summary>Whether the server ever sent payload data on this connection.</summary>
            public readonly bool Established;
            /// <summary>Whether the connection was rejected with an auth-shaped status (401).</summary>
            public readonly bool AuthRejected;
            /// <summary>Seconds the connection was live (0 when never established).</summary>
            public readonly float ConnectedSeconds;

            public StreamOutcome(bool established, bool authRejected, float connectedSeconds)
            {
                Established = established;
                AuthRejected = authRejected;
                ConnectedSeconds = connectedSeconds;
            }
        }

        /// <summary>
        /// Connect → read → reconnect loop. Each connection attempt re-reads the current
        /// auth token and sends <c>Last-Event-ID</c> for resume; drops trigger a
        /// jittered, capped, bounded backoff via <see cref="StreamReconnectPolicy"/> (a
        /// server <c>retry:</c> shapes exactly one wait and still consumes an attempt).
        /// A 401 triggers one <see cref="AuthManager.RefreshAsync"/>; if the refreshed
        /// token is also rejected, <see cref="AuthEvents.Expired"/> is raised and the
        /// loop stops. Keyed on the provider lifetime token.
        /// </summary>
        private async Awaitable RunStreamLoopAsync(CancellationToken token)
        {
            _parser = new SSEEventStreamParser();
            var policy = new StreamReconnectPolicy(
                _reconnectBaseDelaySeconds, _reconnectMaxDelaySeconds, _maxReconnectAttempts,
                stableResetSeconds: _stableConnectionSeconds);
            bool authRefreshAttempted = false;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    StreamOutcome outcome = await StreamOnceAsync(token);
                    if (outcome.Established)
                    {
                        // Only a connection that outlived the stable window clears the
                        // backoff budget (accept-then-drop must keep consuming it).
                        policy.OnConnectionEnded(outcome.ConnectedSeconds);
                        // Data flowed, so the current token was accepted.
                        authRefreshAttempted = false;
                    }

                    if (!_autoReconnect || token.IsCancellationRequested)
                        break;

                    if (outcome.AuthRejected && _sendAuthToken)
                    {
                        if (!authRefreshAttempted && AuthManager.Instance != null)
                        {
                            authRefreshAttempted = true;
                            _connectionStatus = "Refreshing authentication";
                            if (await AuthManager.Instance.RefreshAsync(token))
                                continue; // reconnect immediately with the fresh token
                        }

                        // Refresh unavailable, failed, or the refreshed token was also
                        // rejected — the session is dead; reconnecting cannot help.
                        _connectionStatus = "Authentication expired";
                        Debug.LogError($"[SSEProvider] {name}: stream rejected with 401 and token refresh did not recover; stopping.");
                        AuthEvents.Expired.Dispatch(new AuthExpiredEventData(AuthManager.Instance?.User?.GetUserId()));
                        break;
                    }

                    // A server retry: directive shapes one wait but still consumes an
                    // attempt — it must not disable the backoff budget (retry: 0 loop).
                    _connectionStatus = $"Reconnecting (attempt {policy.AttemptCount + 1})";
                    bool budgetLeft = _parser.TryConsumeRetry(out int retryMs)
                        ? await policy.WaitForNextAttemptAsync(retryMs / 1000f, token)
                        : await policy.WaitForNextAttemptAsync(token);
                    if (!budgetLeft)
                    {
                        _connectionStatus = "Reconnect attempts exhausted";
                        Debug.LogError($"[SSEProvider] {name}: reconnect attempts exhausted.");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Provider deactivated — exit quietly.
            }
            catch (Exception e)
            {
                Debug.LogError($"[SSEProvider] {name}: stream loop failed: {e}");
            }
            finally
            {
                AbortRequest();
            }
        }

        /// <summary>
        /// Runs a single SSE connection until it ends or errors, streaming decoded
        /// chunks through <see cref="SSEStreamDownloadHandler"/> (each byte is decoded
        /// once — no full-buffer re-decode per poll).
        /// </summary>
        private async Awaitable<StreamOutcome> StreamOnceAsync(CancellationToken token)
        {
            AbortRequest();
            _parser.ResetStream();

            var handler = new SSEStreamDownloadHandler();
            _request = UnityWebRequest.Get(_url);
            _request.SetRequestHeader("Accept", "text/event-stream");
            _request.downloadHandler = handler;

            // Re-read the current auth token each connect so a refresh (Sprint 39) is picked up.
            if (_sendAuthToken)
            {
                string authToken = AuthManager.Instance != null ? AuthManager.Instance.AuthToken : null;
                if (!string.IsNullOrEmpty(authToken))
                    _request.SetRequestHeader(_authHeaderName, (_authScheme ?? string.Empty) + authToken);
            }

            // Resume from the last event id, if we have one.
            if (!string.IsNullOrEmpty(_parser.LastEventId))
                _request.SetRequestHeader("Last-Event-ID", _parser.LastEventId);

            _connectionStatus = "Connecting";
            var operation = _request.SendWebRequest();
            bool established = false;
            float establishedAt = 0f;

            while (!operation.isDone)
            {
                DrainHandler(handler, ref established, ref establishedAt);

                await Awaitable.WaitForSecondsAsync(_pollIntervalSeconds, token);
                if (_request == null) // Deactivate aborted mid-loop
                    return new StreamOutcome(established, false, ConnectedSeconds(established, establishedAt));
            }

            // Final drain: data may have arrived between the last poll and completion.
            DrainHandler(handler, ref established, ref establishedAt);

            bool authRejected = false;
            if (_request.result != UnityWebRequest.Result.Success)
            {
                authRejected = _request.responseCode == 401;
                _connectionStatus = $"Error: {_request.error}";
                Debug.LogError($"[SSEProvider] {name}: SSE error: {_request.error} (HTTP {_request.responseCode})");
            }
            else if (!established)
            {
                // Completed cleanly without payload: accepted, but zero-duration —
                // it must not clear the backoff budget unless the window is 0.
                established = true;
                establishedAt = Time.realtimeSinceStartup;
            }

            return new StreamOutcome(established, authRejected, ConnectedSeconds(established, establishedAt));
        }

        private void DrainHandler(SSEStreamDownloadHandler handler, ref bool established, ref float establishedAt)
        {
            while (handler.TryDequeue(out string chunk))
            {
                if (!established)
                {
                    established = true;
                    establishedAt = Time.realtimeSinceStartup;
                    _connectionStatus = "Connected";
                }

                foreach (var ev in _parser.Feed(chunk))
                    DispatchEvent(ev);
            }
        }

        private static float ConnectedSeconds(bool established, float establishedAt) =>
            established ? Mathf.Max(0f, Time.realtimeSinceStartup - establishedAt) : 0f;

        // Reconstruct the canonical event block so downstream JsonPreProcessors (which
        // expect the `event:`/`data:` shape) keep working unchanged.
        private void DispatchEvent(SSEEventStreamParser.SSEEvent ev)
        {
            string block = string.IsNullOrEmpty(ev.EventType)
                ? $"data: {ev.Data}"
                : $"event: {ev.EventType}\ndata: {ev.Data}";
            OnDataFetched(block);
        }

        private void AbortRequest()
        {
            if (_request == null)
                return;
            _request.Abort();
            _request.Dispose();
            _request = null;
        }
    }
}
