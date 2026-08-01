#if MOLCA_SOCKETIO
using System;
using System.Collections.Generic;
using Molca.Attributes;
using Molca.Networking.Streaming;
using SocketIOClient;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Data
{
    [System.Serializable]
    public class SocketIOEventMapping
    {
        [Tooltip("Socket.IO event name (e.g., 'MesinCastingAll')")]
        public string eventName;

        [Tooltip("DataMapping that should parse the payload for this event")]
        public DataMapping dataMapping;

        [Tooltip("Optional: override cache key for this event (defaults to ProviderId_EventName)")]
        public string customCacheKey;

        public bool IsValid =>
            !string.IsNullOrEmpty(eventName) &&
            dataMapping != null &&
            dataMapping.Model != null;
    }

    /// <summary>
    /// Socket.IO data provider: subscribes to named events and parses each payload with its own mapping.
    /// </summary>
    /// <remarks>
    /// The asset is <b>configuration</b> — where to connect, how to authenticate, which events to listen
    /// for, and how to parse each one. The socket, the connecting flag, the reconnect budget, and the last
    /// error live on a <see cref="SocketIoStreamSession"/> the network subsystem owns. The serialized
    /// <c>_connectionStatus</c> and <c>_reconnectAttemptCount</c> fields survive for serialization
    /// compatibility and are no longer written while the game runs.
    /// <para>
    /// <b>Reconnection is now the session's.</b> The Socket.IO library can retry on its own, but it reuses
    /// the headers built when the socket was constructed — which is why this provider used to carry a hook
    /// that tore the socket down mid-reconnect whenever the auth token had changed underneath it. That is
    /// gone: every attempt is a fresh connect with a freshly resolved route and a freshly acquired
    /// credential, and the backoff, jitter, attempt budget, and stable-connection window are the shared
    /// ones. <c>Randomization Factor</c> is superseded by that shared jitter and is kept only so existing
    /// assets deserialize.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "SocketIODataProvider", menuName = "Molca/Networking/SocketIODataProvider", order = 20)]
    public class SocketIODataProvider : DataProvider, Diagnostics.INetworkStreamStatus
    {
        private const string DefaultPath = "/socket.io";

        [Header("Socket.IO Settings")]
        [Tooltip("Connect through the network catalog: a service, an environment strategy, and a relative path. " +
                 "When a service is set it replaces the server URL below and the connection gains the " +
                 "catalog's allowed-host, production-scheme, and credential-scope rules.")]
        [SerializeField] private NetworkStreamRoute _route;

        [Tooltip("Direct server URL. Used only when no catalog service is set above.")]
        [SerializeField, FormerlySerializedAs("serverUrl")] private string _serverUrl;
        [SerializeField, FormerlySerializedAs("useSecureConnection")] private bool _useSecureConnection = true;
        [SerializeField, FormerlySerializedAs("socketPath")] private string _socketPath = DefaultPath;
        [SerializeField, FormerlySerializedAs("connectionTimeoutSeconds")] private float _connectionTimeoutSeconds = 20f;

        [Header("Reconnection")]
        [SerializeField, FormerlySerializedAs("autoReconnect")] private bool _autoReconnect = true;
        [Tooltip("0 or below = unbounded (still backed-off).")]
        [SerializeField, FormerlySerializedAs("maxReconnectAttempts")] private int _maxReconnectAttempts = -1;
        [SerializeField, FormerlySerializedAs("reconnectDelaySeconds")] private float _reconnectDelaySeconds = 2f;
        [SerializeField, FormerlySerializedAs("reconnectDelayMaxSeconds")] private float _reconnectDelayMaxSeconds = 10f;
        [Tooltip("Superseded by the shared reconnect policy's jitter. Kept so existing assets deserialize.")]
#pragma warning disable CS0414 // Written by deserialization only; no code reads it since the shared policy took over jitter.
        [SerializeField, FormerlySerializedAs("randomizationFactor")] private float _randomizationFactor = 0.5f;
#pragma warning restore CS0414
        [Tooltip("A connection must live this long before a drop resets the backoff budget; guards against accept-then-drop servers causing a fast retry loop. 0 = any established connection resets.")]
        [SerializeField] private float _stableConnectionSeconds = 10f;

        [Header("Authentication")]
        [SerializeField, FormerlySerializedAs("requireAuthentication")] private bool _requireAuthentication = false;
        [SerializeField, FormerlySerializedAs("tokenType")] private AuthTokenType _tokenType = AuthTokenType.Bearer;
        [SerializeField, FormerlySerializedAs("customTokenHeaderName")] private string _customTokenHeaderName = "Authorization";
        [SerializeField, FormerlySerializedAs("queryParameterName")] private string _queryParameterName = "token";

        [Header("Event Mappings")]
        [SerializeField, FormerlySerializedAs("socketIOEventMappings")] private SocketIOEventMapping[] _socketIOEventMappings;

        [Header("Debug")]
        [SerializeField, FormerlySerializedAs("logMessages")] private bool _logMessages = false;
        [SerializeField, FormerlySerializedAs("logRawData")] private bool _logRawData = false;

        [Tooltip("Kept for serialization compatibility. Live state lives on the session — read ConnectionStatus.")]
        [SerializeField, FormerlySerializedAs("connectionStatus"), ReadOnly] private string _connectionStatus = "Disconnected";

        [Tooltip("Kept for serialization compatibility. Live state lives on the session — read ReconnectAttemptCount.")]
        [SerializeField, FormerlySerializedAs("reconnectAttemptCount"), ReadOnly] private int _reconnectAttemptCount = 0;

        private SocketIoStreamSession _session;
        private Dictionary<string, SocketIOEventMapping> _mappingLookup;

        /// <summary>How a token is carried to the server.</summary>
        public enum AuthTokenType
        {
            /// <summary>An <c>Authorization: Bearer &lt;token&gt;</c> header.</summary>
            Bearer,

            /// <summary>A custom header named by <c>customTokenHeaderName</c>.</summary>
            Custom,

            /// <summary>A query parameter named by <c>queryParameterName</c>.</summary>
            QueryParameter
        }

        /// <summary>Whether this provider connects through a catalog route rather than a direct URL.</summary>
        public bool UsesRoutedStream => _route.IsConfigured;

        /// <summary>The subsystem-owned session, or <c>null</c> while inactive.</summary>
        public NetworkStreamSession Session => _session;

        /// <summary>The binding the current attempt resolved to, or <c>null</c>.</summary>
        public NetworkStreamBinding Binding => _session?.Binding;

        /// <summary>Whether the socket is connected.</summary>
        public bool IsConnected => _session != null && _session.IsOpen;

        /// <summary>Human-readable connection state, read from the session while one exists.</summary>
        public string ConnectionStatus => _session != null ? _session.Describe() : _connectionStatus;

        /// <inheritdoc />
        public bool IsStreamConnected => IsConnected;

        /// <inheritdoc />
        public string StreamStatus => ConnectionStatus ?? string.Empty;

        /// <summary>Connection attempts made since the session started.</summary>
        public int ReconnectAttemptCount => _session?.AttemptCount ?? _reconnectAttemptCount;

        /// <inheritdoc />
        public override void Activate()
        {
            if (!ValidateConfiguration())
            {
                Debug.LogError($"[SocketIODataProvider] {name}: Configuration validation failed!");
                return;
            }

            base.Activate();

            BuildMappingLookup();

            var network = RuntimeManager.GetSubsystem<NetworkRuntimeSubsystem>();
            if (network == null)
            {
                Debug.LogError(
                    $"[SocketIODataProvider] {name}: no NetworkRuntimeSubsystem is active, so no session " +
                    "can be opened. Add one to the bootstrap, or declare " +
                    "[DependsOn(typeof(NetworkRuntimeSubsystem))] on whatever activates this provider.");
                return;
            }

            _session = new SocketIoStreamSession(
                ProviderId,
                _route,
                network.Resolver,
                network.Credentials,
                BuildSessionOptions(),
                StreamReconnectSettings.Create(
                    _autoReconnect,
                    _reconnectDelaySeconds,
                    Mathf.Max(_reconnectDelayMaxSeconds, _reconnectDelaySeconds),
                    // This provider spells "unbounded" as -1; the shared settings spell it 0.
                    Mathf.Max(0, _maxReconnectAttempts),
                    _stableConnectionSeconds),
                directUri: BuildDirectUrl());

            _session.EventReceived += HandleSocketEvent;
            network.AdoptSession(_session);

            // Fire-and-forget keyed on this provider's activation token. Deactivate closes the session,
            // which unwinds the loop.
            _ = _session.RunAsync(LifetimeToken);
        }

        /// <inheritdoc />
        public override void Deactivate()
        {
            if (_session != null)
            {
                _session.EventReceived -= HandleSocketEvent;
                _session = null;

                RuntimeManager.GetSubsystem<NetworkRuntimeSubsystem>()?.Streams?.Close(ProviderId);
            }

            base.Deactivate();
        }

        /// <inheritdoc />
        /// <remarks>Socket.IO pushes data through events. No polling required.</remarks>
        public override void FetchData() { }

        /// <summary>
        /// Drops the current connection so the session reconnects.
        /// </summary>
        /// <remarks>
        /// The reconnect still runs through the shared backoff and attempt budget, so a manual retry
        /// cannot be used to bypass them.
        /// </remarks>
        public void Reconnect()
        {
            if (_session == null)
            {
                Debug.LogWarning($"[SocketIODataProvider] {name}: Cannot reconnect - not active");
                return;
            }

            if (_logMessages)
            {
                Debug.Log($"[SocketIODataProvider] {name}: Manual reconnect requested");
            }

            _session.DropConnection();
        }

        /// <summary>
        /// Emits an event on the live connection.
        /// </summary>
        /// <param name="eventName">The event name.</param>
        /// <param name="payloadJson">A JSON payload, or <c>null</c> to emit with no data.</param>
        public void Emit(string eventName, string payloadJson = null)
        {
            if (_session == null)
            {
                Debug.LogWarning($"[SocketIODataProvider] {name}: Cannot emit '{eventName}' - not active");
                return;
            }

            if (_session.Emit(eventName, payloadJson) && _logMessages)
            {
                Debug.Log($"[SocketIODataProvider] {name}: Emitted event '{eventName}'");
            }
        }

        /// <inheritdoc />
        public override bool ValidateConfiguration()
        {
            // A routed provider has no URL to validate: its destination comes from the catalog binding
            // and is checked when the route resolves.
            if (!UsesRoutedStream && string.IsNullOrEmpty(_serverUrl))
            {
                Debug.LogError(
                    $"[SocketIODataProvider] {name}: set a catalog service on Route, or a direct server URL.");
                return false;
            }

            if (_socketIOEventMappings == null || _socketIOEventMappings.Length == 0)
            {
                Debug.LogError($"[SocketIODataProvider] {name}: No Socket.IO event mappings configured!");
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _socketIOEventMappings.Length; i++)
            {
                var mapping = _socketIOEventMappings[i];
                if (mapping == null || !mapping.IsValid)
                {
                    Debug.LogError($"[SocketIODataProvider] {name}: Mapping at index {i} is invalid. Event name and DataMapping (with Model) are required.");
                    return false;
                }

                if (!seen.Add(mapping.eventName))
                {
                    Debug.LogError($"[SocketIODataProvider] {name}: Duplicate event name '{mapping.eventName}' detected in mappings.");
                    return false;
                }
            }

            if (_reconnectDelaySeconds < 0)
            {
                Debug.LogError($"[SocketIODataProvider] {name}: Reconnect delay cannot be negative!");
                return false;
            }

            if (_reconnectDelayMaxSeconds < _reconnectDelaySeconds)
            {
                Debug.LogWarning($"[SocketIODataProvider] {name}: Reconnect delay max is less than base delay. Adjusting automatically.");
                _reconnectDelayMaxSeconds = _reconnectDelaySeconds;
            }

            if (_connectionTimeoutSeconds <= 0)
            {
                Debug.LogError($"[SocketIODataProvider] {name}: Connection timeout must be greater than 0!");
                return false;
            }

            return true;
        }

        /// <summary>Snapshots the connection settings for the session's lifetime.</summary>
        internal SocketIoSessionOptions BuildSessionOptions()
        {
            var events = new List<string>();
            if (_socketIOEventMappings != null)
            {
                foreach (var mapping in _socketIOEventMappings)
                {
                    if (mapping != null && !string.IsNullOrEmpty(mapping.eventName))
                        events.Add(mapping.eventName);
                }
            }

            return new SocketIoSessionOptions
            {
                SocketPath = string.IsNullOrEmpty(_socketPath) ? DefaultPath : _socketPath,
                ConnectionTimeoutSeconds = _connectionTimeoutSeconds,
                RequireAuthentication = _requireAuthentication,
                AuthHeaderName = _tokenType == AuthTokenType.Custom && !string.IsNullOrEmpty(_customTokenHeaderName)
                    ? _customTokenHeaderName
                    : "Authorization",
                AuthScheme = _tokenType == AuthTokenType.Bearer ? "Bearer " : string.Empty,
                AuthQueryParameter = _tokenType == AuthTokenType.QueryParameter
                    ? (string.IsNullOrEmpty(_queryParameterName) ? "token" : _queryParameterName)
                    : string.Empty,
                Events = events,
                LogEvents = _logMessages,
            };
        }

        /// <summary>
        /// The authored server URL with its scheme applied, or empty when this provider is routed.
        /// </summary>
        /// <remarks>
        /// Empty in routed mode on purpose: the session must have no URL to fall back to, or a provider
        /// whose catalog binding was deleted would quietly resume connecting to a stale address.
        /// </remarks>
        internal string BuildDirectUrl()
        {
            if (UsesRoutedStream || string.IsNullOrWhiteSpace(_serverUrl))
                return string.Empty;

            string trimmed = _serverUrl.Trim();

            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = (_useSecureConnection ? "https://" : "http://") + trimmed;
            }

            return trimmed;
        }

        /// <summary>
        /// Parses one event payload with its mapping and publishes it to the data cache.
        /// </summary>
        /// <remarks>
        /// Interpretation stays on the provider: the mapping, the model, and the cache key are asset
        /// configuration. The session owns the connection and knows nothing about data models.
        /// </remarks>
        private void HandleSocketEvent(string eventName, SocketIOResponse response)
        {
            if (_mappingLookup == null || !_mappingLookup.TryGetValue(eventName, out var mapping))
            {
                if (_logMessages)
                {
                    Debug.LogWarning($"[SocketIODataProvider] {name}: Received event '{eventName}' with no mapping");
                }
                return;
            }

            try
            {
                string payload = response.Count > 0 ? response.GetValue().GetRawText() : "{}";

                if (_logRawData)
                {
                    Debug.Log($"[SocketIODataProvider] {name}: Event {eventName} payload: {payload}");
                }

                string cacheKey = string.IsNullOrEmpty(mapping.customCacheKey)
                    ? $"{ProviderId}_{eventName}"
                    : mapping.customCacheKey;

                var cache = DataManager.Instance.GetOrCreateCache(cacheKey, mapping.dataMapping.Model);
                var parsedData = mapping.dataMapping.ParseJson(payload);

                if (parsedData.IsValid)
                {
                    cache.AddData(parsedData);
                    DataManager.TriggerDataUpdated(cacheKey, parsedData);
                }
                else
                {
                    Debug.LogWarning($"[SocketIODataProvider] {name}: Parsed data for event '{eventName}' is invalid");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SocketIODataProvider] {name}: Error handling event '{eventName}': {ex.Message}");
            }
        }

        private void BuildMappingLookup()
        {
            _mappingLookup = new Dictionary<string, SocketIOEventMapping>(StringComparer.OrdinalIgnoreCase);
            if (_socketIOEventMappings == null) return;

            foreach (var mapping in _socketIOEventMappings)
            {
                if (mapping == null || string.IsNullOrEmpty(mapping.eventName)) continue;
                _mappingLookup[mapping.eventName] = mapping;
            }
        }

        private void OnValidate()
        {
            if (!string.IsNullOrEmpty(_serverUrl))
            {
                _serverUrl = _serverUrl.Trim();
                if (_serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    _useSecureConnection = false;
                }
                else if (_serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    _useSecureConnection = true;
                }
            }
        }
    }
}
#endif
