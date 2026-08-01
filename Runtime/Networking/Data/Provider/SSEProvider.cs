using System;
using Molca.Attributes;
using Molca.Networking.Streaming;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Server-Sent Events data provider: holds a long-lived stream open and forwards each event to the
    /// data pipeline.
    /// </summary>
    /// <remarks>
    /// The asset is <b>configuration</b>. The request handle, the stream position, the reconnect budget,
    /// and the connection state live on an <see cref="SseStreamSession"/> the network subsystem owns — so
    /// nothing here writes to project data at runtime, and two scenes referencing one provider no longer
    /// share mutable state. The serialized <c>_connectionStatus</c> field survives for serialization
    /// compatibility and is not written while the game runs; read <see cref="ConnectionStatus"/>.
    /// <para>
    /// Set a catalog service on <b>Route</b> and the connection inherits the catalog's allowed hosts,
    /// production scheme rule, and credential scope. Leave it empty and the authored URL is used, outside
    /// all of those.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "SSEProvider", menuName = "Molca/Networking/SSEProvider", order = 20)]
    public class SSEProvider : DataProvider, Diagnostics.INetworkStreamStatus
    {
        [Header("Route (preferred)")]
        [Tooltip("Connect through the network catalog: a service, an environment strategy, and a relative path. " +
                 "When a service is set this replaces the URL below, and the connection gains the catalog's " +
                 "allowed-host, production-scheme, and credential-scope rules.")]
        [SerializeField] private NetworkStreamRoute _route;

        [Header("SSE Settings")]
        [Tooltip("Direct URL. Used only when no catalog service is set above.")]
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
        [Tooltip("Kept for serialization compatibility. Live state lives on the session — read ConnectionStatus.")]
        [SerializeField, ReadOnly] private string _connectionStatus = "Disconnected";

        private SseStreamSession _session;

        /// <summary>Whether this provider connects through a catalog route rather than a direct URL.</summary>
        public bool UsesRoutedStream => _route.IsConfigured;

        /// <summary>The subsystem-owned session, or <c>null</c> while inactive.</summary>
        public NetworkStreamSession Session => _session;

        /// <summary>The binding the current attempt resolved to, or <c>null</c>.</summary>
        public NetworkStreamBinding Binding => _session?.Binding;

        /// <summary>Human-readable connection state, read from the session while one exists.</summary>
        public string ConnectionStatus => _session != null ? _session.Describe() : _connectionStatus;

        /// <inheritdoc />
        public bool IsStreamConnected => _session != null && _session.IsStreamConnected;

        /// <inheritdoc />
        public string StreamStatus => ConnectionStatus ?? string.Empty;

        /// <inheritdoc />
        public override void Activate()
        {
            base.Activate();

            var network = RuntimeManager.GetSubsystem<NetworkRuntimeSubsystem>();
            if (network == null)
            {
                Debug.LogError(
                    $"[SSEProvider] {name}: no NetworkRuntimeSubsystem is active, so no session can be " +
                    "opened. Add one to the bootstrap, or declare " +
                    "[DependsOn(typeof(NetworkRuntimeSubsystem))] on whatever activates this provider.");
                return;
            }

            _session = network.OpenSseSession(
                ProviderId,
                _route,
                StreamReconnectSettings.Create(
                    _autoReconnect, _reconnectBaseDelaySeconds, _reconnectMaxDelaySeconds,
                    _maxReconnectAttempts, _stableConnectionSeconds),
                pollIntervalSeconds: _pollIntervalSeconds,
                directUri: BuildDirectUrl(),
                authHeaderName: _sendAuthToken ? _authHeaderName : null,
                authScheme: _authScheme);

            if (_session == null)
                return;

            _session.MessageReceived += OnDataFetched;

            // Fire-and-forget keyed on this provider's activation token, the lifetime contract this
            // provider has always used. Deactivate closes the session, which unwinds the loop.
            _ = _session.RunAsync(LifetimeToken);
        }

        /// <inheritdoc />
        public override void Deactivate()
        {
            if (_session != null)
            {
                _session.MessageReceived -= OnDataFetched;
                _session = null;

                // The registry owns disposal: closing by id stops the session and forgets it, so a
                // re-activated provider never ends up with two live streams.
                RuntimeManager.GetSubsystem<NetworkRuntimeSubsystem>()?.Streams?.Close(ProviderId);
            }

            base.Deactivate();
        }

        /// <inheritdoc />
        /// <remarks>An SSE stream is pushed to, not polled, so there is nothing to fetch on demand.</remarks>
        public override void FetchData() { }

        /// <summary>
        /// The authored URL, or empty when this provider is routed.
        /// </summary>
        /// <remarks>
        /// Empty in routed mode on purpose: the session must have no URL to fall back to, or a provider
        /// whose catalog binding was deleted would quietly resume connecting to a stale address.
        /// </remarks>
        internal string BuildDirectUrl() => UsesRoutedStream ? string.Empty : _url ?? string.Empty;
    }
}
