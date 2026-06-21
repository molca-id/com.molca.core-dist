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

        /// <summary>
        /// Connect → read → reconnect loop. Each connection attempt re-reads the current
        /// auth token and sends <c>Last-Event-ID</c> for resume; drops trigger a
        /// jittered, capped, bounded backoff via <see cref="StreamReconnectPolicy"/> (a
        /// server <c>retry:</c> overrides the delay). Keyed on the provider lifetime token.
        /// </summary>
        private async Awaitable RunStreamLoopAsync(CancellationToken token)
        {
            _parser = new SSEEventStreamParser();
            var policy = new StreamReconnectPolicy(_reconnectBaseDelaySeconds, _reconnectMaxDelaySeconds, _maxReconnectAttempts);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool connected = await StreamOnceAsync(token);
                    if (connected)
                        policy.Reset(); // a healthy connection clears the backoff budget

                    if (!_autoReconnect || token.IsCancellationRequested)
                        break;

                    // A server-directed retry overrides the computed backoff.
                    if (_parser.RetryMilliseconds.HasValue)
                    {
                        _connectionStatus = "Reconnecting";
                        await Awaitable.WaitForSecondsAsync(_parser.RetryMilliseconds.Value / 1000f, token);
                    }
                    else
                    {
                        _connectionStatus = $"Reconnecting (attempt {policy.AttemptCount + 1})";
                        if (!await policy.WaitForNextAttemptAsync(token))
                        {
                            _connectionStatus = "Reconnect attempts exhausted";
                            Debug.LogError($"[SSEProvider] {name}: reconnect attempts exhausted.");
                            break;
                        }
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
        /// Runs a single SSE connection until it ends or errors. Returns <c>true</c> if
        /// the connection was established (so the caller can reset the backoff).
        /// </summary>
        private async Awaitable<bool> StreamOnceAsync(CancellationToken token)
        {
            AbortRequest();
            _parser.ResetStream();

            _request = UnityWebRequest.Get(_url);
            _request.SetRequestHeader("Accept", "text/event-stream");
            _request.downloadHandler = new DownloadHandlerBuffer();

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
            int consumed = 0;

            while (!operation.isDone)
            {
                if (_request.downloadHandler != null)
                {
                    string full = _request.downloadHandler.text;
                    if (!string.IsNullOrEmpty(full) && full.Length > consumed)
                    {
                        if (!established)
                        {
                            established = true;
                            _connectionStatus = "Connected";
                        }
                        string delta = full.Substring(consumed);
                        consumed = full.Length;
                        foreach (var ev in _parser.Feed(delta))
                            DispatchEvent(ev);
                    }
                }

                await Awaitable.WaitForSecondsAsync(_pollIntervalSeconds, token);
                if (_request == null) // Deactivate aborted mid-loop
                    return established;
            }

            if (_request.result != UnityWebRequest.Result.Success)
            {
                _connectionStatus = $"Error: {_request.error}";
                Debug.LogError($"[SSEProvider] {name}: SSE error: {_request.error}");
            }
            else
            {
                established = true; // completed cleanly
            }

            return established;
        }

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
