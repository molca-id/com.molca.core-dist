#if MOLCA_WEBSOCKET
using System;
using Molca.Attributes;
using Molca.Networking.Configuration;
using Molca.Networking.Streaming;
using NativeWebSocket;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Data
{
    public enum MessageFormat
    {
        JSON,           // Standard JSON: {"type":"event", "data":{...}}
        Raw             // Raw string, no parsing
    }

    /// <summary>
    /// Standard WebSocket Data Provider for JSON or raw string payloads.
    ///
    /// JSON MODE:
    /// 1. Set Message Format to "JSON"
    /// 2. Optionally enable filtering and set the field name that contains the type identifier
    /// 3. Provide a DataMapping to parse the JSON payload
    ///
    /// RAW MODE:
    /// 1. Set Message Format to "Raw"
    /// 2. Incoming payloads are forwarded directly to the DataMapping/JsonPreProcessor
    /// </summary>
    /// <remarks>
    /// The asset is <b>configuration</b>: where to connect, how to authenticate, how to shape a
    /// reconnect, and how to interpret a frame. It holds no connection. The socket, the connecting flag,
    /// the handshake deadline, the keep-alive timer, the reconnect budget, and the last error live on a
    /// <see cref="WebSocketStreamSession"/> the network subsystem owns.
    /// <para>
    /// That split is not tidiness. A <see cref="ScriptableObject"/> is project data, so a provider
    /// recording its own connection status was mutating an asset at runtime — and two scenes referencing
    /// one provider were overwriting each other's state. The serialized <c>_connectionStatus</c> and
    /// <c>_reconnectAttemptCount</c> fields survive for serialization compatibility and are no longer
    /// written while the game runs; read <see cref="ConnectionStatus"/>, which reads through to the
    /// session.
    /// </para>
    /// <para>
    /// Set a catalog service on <b>Route</b> and the connection inherits the catalog's allowed hosts,
    /// production scheme rule, and credential scope. Leave it empty and the authored URL is used as
    /// before, outside all of those.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "WebSocketDataProvider", menuName = "Molca/Networking/WebSocketDataProvider", order = 20)]
    public class WebSocketDataProvider : DataProvider, Diagnostics.INetworkStreamStatus
    {
        [Header("Route (preferred)")]
        [Tooltip("Connect through the network catalog: a service, an environment strategy, and a relative path. " +
                 "When a service is set it replaces the URL below and the connection gains the catalog's " +
                 "allowed-host, production-scheme, and credential-scope rules.")]
        [SerializeField] private NetworkStreamRoute _route;

        [Header("WebSocket Settings")]
        [Tooltip("Direct URL. Used only when no catalog service is set above.")]
        [SerializeField, FormerlySerializedAs("url")] private string _url;
        [SerializeField, FormerlySerializedAs("useSecureConnection")] private bool _useSecureConnection = true;

        [Header("Authentication")]
        [SerializeField, FormerlySerializedAs("requireAuthentication")] private bool _requireAuthentication = false;
        [SerializeField, FormerlySerializedAs("tokenType")] private AuthTokenType _tokenType = AuthTokenType.Bearer;
        [SerializeField, FormerlySerializedAs("customTokenHeaderName")] private string _customTokenHeaderName = "Authorization";

        [Header("Connection Settings")]
        [SerializeField, FormerlySerializedAs("autoReconnect")] private bool _autoReconnect = true;
        [Tooltip("First reconnect delay in seconds; grows exponentially with jitter up to the max.")]
        [SerializeField, FormerlySerializedAs("reconnectDelaySeconds")] private float _reconnectDelaySeconds = 5f;
        [Tooltip("Upper bound on the reconnect backoff delay.")]
        [SerializeField] private float _reconnectMaxDelaySeconds = 30f;
        [Tooltip("0 = unbounded (still backed-off).")]
        [SerializeField, FormerlySerializedAs("maxReconnectAttempts")] private int _maxReconnectAttempts = 5;
        [Tooltip("A connection must live this long before a drop resets the backoff budget; guards against accept-then-drop servers causing a fast retry loop. 0 = any established connection resets.")]
        [SerializeField] private float _stableConnectionSeconds = 10f;
        [SerializeField, FormerlySerializedAs("connectionTimeoutSeconds")] private float _connectionTimeoutSeconds = 30f;

        [Header("Ping/Pong Settings")]
        [SerializeField, FormerlySerializedAs("enablePingPong")] private bool _enablePingPong = true;
        [SerializeField, FormerlySerializedAs("pingIntervalSeconds")] private float _pingIntervalSeconds = 30f;
        [SerializeField, FormerlySerializedAs("pingMessage")] private string _pingMessage = "{\"type\":\"ping\"}";

        [Header("Message Format")]
        [SerializeField, FormerlySerializedAs("messageFormat")] private MessageFormat _messageFormat = MessageFormat.JSON;
        [SerializeField, FormerlySerializedAs("filterMessages")] private bool _filterMessages = false;

        [Header("JSON Format Settings")]
        [Tooltip("For JSON format: field name that contains the message type (e.g., 'type')")]
        [SerializeField, FormerlySerializedAs("messageTypeFieldName")] private string _messageTypeFieldName = "type";

        [Header("Debug")]
        [SerializeField, FormerlySerializedAs("logMessages")] private bool _logMessages = false;
        [SerializeField, FormerlySerializedAs("logRawData")] private bool _logRawData = false;

        [Tooltip("Kept for serialization compatibility. Live state lives on the session — read ConnectionStatus.")]
        [SerializeField, FormerlySerializedAs("connectionStatus"), ReadOnly] private string _connectionStatus = "Disconnected";

        [Tooltip("Kept for serialization compatibility. Live state lives on the session — read ReconnectAttemptCount.")]
        [SerializeField, FormerlySerializedAs("reconnectAttemptCount"), ReadOnly] private int _reconnectAttemptCount = 0;

        private WebSocketStreamSession _session;

        /// <summary>How a token is carried to the server.</summary>
        public enum AuthTokenType
        {
            /// <summary>An <c>Authorization: Bearer &lt;token&gt;</c> header.</summary>
            Bearer,

            /// <summary>A custom header named by <c>customTokenHeaderName</c>.</summary>
            Custom,

            /// <summary>A <c>token</c> query parameter.</summary>
            QueryParameter
        }

        /// <summary>Whether this provider connects through a catalog route rather than a direct URL.</summary>
        public bool UsesRoutedStream => _route.IsConfigured;

        /// <summary>The subsystem-owned session, or <c>null</c> while inactive.</summary>
        public NetworkStreamSession Session => _session;

        /// <summary>The binding the current attempt resolved to, or <c>null</c>.</summary>
        public NetworkStreamBinding Binding => _session?.Binding;

        /// <summary>
        /// Check if connected
        /// </summary>
        public bool IsConnected => _session != null && _session.IsOpen;

        /// <summary>
        /// Get current connection state
        /// </summary>
        public WebSocketState ConnectionState => _session?.SocketState ?? WebSocketState.Closed;

        /// <summary>
        /// Get connection status string
        /// </summary>
        public string ConnectionStatus => _session != null ? _session.Describe() : _connectionStatus;

        /// <summary>Connection attempts made since the session started.</summary>
        public int ReconnectAttemptCount => _session?.AttemptCount ?? _reconnectAttemptCount;

        /// <inheritdoc />
        public bool IsStreamConnected => IsConnected;

        /// <inheritdoc />
        public string StreamStatus => ConnectionStatus ?? string.Empty;

        /// <inheritdoc />
        public override void Activate()
        {
            if (!ValidateConfiguration())
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Configuration validation failed!");
                return;
            }

            base.Activate();

            var network = RuntimeManager.GetSubsystem<NetworkRuntimeSubsystem>();
            if (network == null)
            {
                Debug.LogError(
                    $"[WebSocketDataProvider] {name}: no NetworkRuntimeSubsystem is active, so no session " +
                    "can be opened. Add one to the bootstrap, or declare " +
                    "[DependsOn(typeof(NetworkRuntimeSubsystem))] on whatever activates this provider.");
                return;
            }

            _session = new WebSocketStreamSession(
                ProviderId,
                _route,
                network.Resolver,
                network.Credentials,
                BuildOptions(),
                StreamReconnectSettings.Create(
                    _autoReconnect, _reconnectDelaySeconds, _reconnectMaxDelaySeconds,
                    _maxReconnectAttempts, _stableConnectionSeconds),
                directUri: BuildDirectUrl());

            _session.MessageReceived += OnFrameReceived;
            network.AdoptSession(_session);

            // Fire-and-forget keyed on this provider's activation token, the same lifetime contract the
            // provider has always used. Deactivate closes the session, which unwinds the loop.
            _ = _session.RunAsync(LifetimeToken);
        }

        /// <inheritdoc />
        public override void Deactivate()
        {
            if (_session != null)
            {
                _session.MessageReceived -= OnFrameReceived;
                _session = null;

                // The registry owns disposal: closing by id stops the session and forgets it, so a
                // re-activated provider never ends up with two live connections.
                RuntimeManager.GetSubsystem<NetworkRuntimeSubsystem>()?.Streams?.Close(ProviderId);
            }

            base.Deactivate();
        }

        /// <inheritdoc />
        /// <remarks>A WebSocket is pushed to, not polled, so there is nothing to fetch on demand.</remarks>
        public override void FetchData() { }

        /// <summary>
        /// Send a custom message through the WebSocket connection
        /// </summary>
        /// <returns>Completes once the message is sent. Awaiting is optional.</returns>
        public async Awaitable SendMessage(string message)
        {
            if (_session == null)
            {
                Debug.LogWarning($"[WebSocketDataProvider] {name}: Cannot send message - not active");
                return;
            }

            await _session.SendTextAsync(message);

            if (_logMessages)
            {
                Debug.Log($"[WebSocketDataProvider] {name}: Message sent: {message}");
            }
        }

        /// <summary>
        /// Send binary data through the WebSocket connection
        /// </summary>
        /// <returns>Completes once the data is sent. Awaiting is optional.</returns>
        public async Awaitable SendBinary(byte[] data)
        {
            if (_session == null)
            {
                Debug.LogWarning($"[WebSocketDataProvider] {name}: Cannot send binary data - not active");
                return;
            }

            await _session.SendBinaryAsync(data);

            if (_logMessages)
            {
                Debug.Log($"[WebSocketDataProvider] {name}: Binary data sent ({data?.Length ?? 0} bytes)");
            }
        }

        /// <summary>
        /// Manually trigger a reconnection.
        /// </summary>
        /// <remarks>
        /// It drops the connection rather than restarting the session, so the reconnect still runs through
        /// the backoff and the attempt budget. A manual retry that bypassed those would let a user hammer
        /// a failing server by holding a button.
        /// </remarks>
        public void Reconnect()
        {
            if (_session == null)
            {
                Debug.LogWarning($"[WebSocketDataProvider] {name}: Cannot reconnect - not active");
                return;
            }

            if (_logMessages)
            {
                Debug.Log($"[WebSocketDataProvider] {name}: Manual reconnect triggered");
            }

            _session.DropConnection();
        }

        /// <inheritdoc />
        public override bool ValidateConfiguration()
        {
            if (!base.ValidateConfiguration())
            {
                return false;
            }

            // A routed provider has no URL to validate: its destination comes from the catalog binding
            // and is checked when the route resolves.
            if (!UsesRoutedStream && string.IsNullOrEmpty(_url))
            {
                Debug.LogError(
                    $"[WebSocketDataProvider] {name}: set a catalog service on Route, or a direct URL.");
                return false;
            }

            if (_reconnectDelaySeconds < 0)
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Reconnect delay cannot be negative!");
                return false;
            }

            if (_maxReconnectAttempts < 0)
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Max reconnect attempts cannot be negative!");
                return false;
            }

            if (_enablePingPong && _pingIntervalSeconds <= 0)
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Ping interval must be greater than 0!");
                return false;
            }

            return true;
        }

        /// <summary>Snapshots the connection settings for the session's lifetime.</summary>
        internal WebSocketSessionOptions BuildOptions()
        {
            return new WebSocketSessionOptions
            {
                EnablePingPong = _enablePingPong,
                PingIntervalSeconds = _pingIntervalSeconds,
                PingMessage = _pingMessage,
                ConnectionTimeoutSeconds = _connectionTimeoutSeconds,
                RequireAuthentication = _requireAuthentication,
                AuthHeaderName = _tokenType == AuthTokenType.Custom
                    ? _customTokenHeaderName
                    : "Authorization",
                AuthScheme = _tokenType == AuthTokenType.Bearer ? "Bearer " : string.Empty,
                AuthQueryParameter = _tokenType == AuthTokenType.QueryParameter ? "token" : string.Empty,
                LogEvents = _logMessages,
            };
        }

        /// <summary>
        /// The authored URL with its scheme applied, or empty when this provider is routed.
        /// </summary>
        /// <remarks>
        /// Empty in routed mode on purpose: the session must have no URL to fall back to. A provider whose
        /// catalog binding was deleted would otherwise quietly resume connecting to whatever is still
        /// typed in the asset.
        /// </remarks>
        internal string BuildDirectUrl()
        {
            if (UsesRoutedStream || string.IsNullOrEmpty(_url))
                return string.Empty;

            string url = _url;
            if (!url.StartsWith("ws://", StringComparison.Ordinal) &&
                !url.StartsWith("wss://", StringComparison.Ordinal))
            {
                url = (_useSecureConnection ? "wss://" : "ws://") + url;
            }

            return url;
        }

        /// <summary>
        /// Interprets one raw frame: drop keep-alive replies, apply the configured format, then dispatch.
        /// </summary>
        /// <remarks>
        /// Interpretation stays on the provider because it is asset configuration — the message format,
        /// the type field, the filter switch. The session owns the connection; this owns what a frame
        /// means.
        /// </remarks>
        private void OnFrameReceived(string message)
        {
            try
            {
                if (_logRawData)
                {
                    Debug.Log($"[WebSocketDataProvider] {name}: Raw message: {message}");
                }

                if (IsPongMessage(message))
                {
                    if (_logMessages)
                    {
                        Debug.Log($"[WebSocketDataProvider] {name}: Pong received");
                    }
                    return;
                }

                string processedMessage = ParseMessageByFormat(message);

                if (string.IsNullOrEmpty(processedMessage))
                {
                    if (_logMessages)
                    {
                        Debug.Log($"[WebSocketDataProvider] {name}: Message filtered out or empty");
                    }
                    return;
                }

                OnDataFetched(processedMessage);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Error processing message: {ex.Message}");
            }
        }

        private bool IsPongMessage(string message)
        {
            // Override this method or add custom logic to detect pong messages
            return message.Contains("\"type\":\"pong\"") || message.Contains("\"pong\"");
        }

        /// <summary>
        /// Parse message based on configured format and return the data to process
        /// </summary>
        /// <param name="message">Raw message string</param>
        /// <returns>Processed message data, or null if should be filtered</returns>
        private string ParseMessageByFormat(string message)
        {
            switch (_messageFormat)
            {
                case MessageFormat.JSON:
                    return ParseJSONMessage(message);

                case MessageFormat.Raw:
                default:
                    return message;
            }
        }

        /// <summary>
        /// Parse standard JSON format with type field
        /// </summary>
        private string ParseJSONMessage(string message)
        {
            try
            {
                // Filter messages if enabled
                if (_filterMessages && !ShouldProcessJSONMessage(message, out _))
                {
                    return null;
                }

                return message;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Error parsing JSON message: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Check if JSON message should be processed based on type field
        /// </summary>
        private bool ShouldProcessJSONMessage(string message, out string messageType)
        {
            messageType = null;

            try
            {
                // Simple JSON parsing to check message type
                if (message.Contains($"\"{_messageTypeFieldName}\""))
                {
                    var startIndex = message.IndexOf($"\"{_messageTypeFieldName}\"") + _messageTypeFieldName.Length + 3;
                    var endIndex = message.IndexOf("\"", startIndex);
                    if (endIndex > startIndex)
                    {
                        messageType = message.Substring(startIndex, endIndex - startIndex);
                    }
                }

                return true;
            }
            catch
            {
                return true; // If parsing fails, process the message anyway
            }
        }

        private void OnValidate()
        {
            // Ensure proper URL format
            if (!string.IsNullOrEmpty(_url))
            {
                _url = _url.Trim();

                // Remove protocol if user added it incorrectly based on useSecureConnection setting
                if (_useSecureConnection && _url.StartsWith("ws://"))
                {
                    _url = _url.Substring(5);
                }
                else if (!_useSecureConnection && _url.StartsWith("wss://"))
                {
                    _url = _url.Substring(6);
                }
            }
        }
    }
}
#endif
