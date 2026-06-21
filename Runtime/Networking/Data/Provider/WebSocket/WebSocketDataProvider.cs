#if MOLCA_WEBSOCKET
using System;
using System.Collections.Generic;
using System.Text;
using Molca.Attributes;
using Molca.Networking.Auth;
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
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "WebSocketDataProvider", menuName = "Molca/Networking/WebSocketDataProvider", order = 20)]
    public class WebSocketDataProvider : DataProvider
    {
        [Header("WebSocket Settings")]
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
        [SerializeField, FormerlySerializedAs("connectionStatus"), ReadOnly] private string _connectionStatus = "Disconnected";
        [SerializeField, FormerlySerializedAs("reconnectAttemptCount"), ReadOnly] private int _reconnectAttemptCount = 0;
        
        private WebSocket _webSocket;
        private bool _isAuthenticated;
        private bool _isConnecting;
        private bool _isManualDisconnect;
        private float _lastPingTime;
        private float _connectionStartTime;
        private Dictionary<string, string> _headers = new Dictionary<string, string>();
        // Shared backoff schedule; built on Activate, reset on a successful open.
        private StreamReconnectPolicy _reconnectPolicy;

        public enum AuthTokenType
        {
            Bearer,
            Custom,
            QueryParameter
        }

        public override void Activate()
        {
            if (!ValidateConfiguration())
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Configuration validation failed!");
                return;
            }

            base.Activate();
            
            _connectionStatus = "Initializing";
            _isManualDisconnect = false;
            _reconnectAttemptCount = 0;
            _reconnectPolicy = new StreamReconnectPolicy(_reconnectDelaySeconds, _reconnectMaxDelaySeconds, _maxReconnectAttempts);

            if (_requireAuthentication)
            {
                AuthEvents.StateChanged.Register(OnAuthStateChanged);
                
                if (AuthManager.Instance != null && AuthManager.Instance.IsAuthenticated)
                {
                    _isAuthenticated = true;
                    ConnectWebSocket();
                }
                else
                {
                    _connectionStatus = "Waiting for Authentication";
                    Debug.Log($"[WebSocketDataProvider] {name}: Waiting for authentication...");
                }
            }
            else
            {
                ConnectWebSocket();
            }
        }

        public override void Deactivate()
        {
            _isManualDisconnect = true;
            _connectionStatus = "Disconnecting";
            
            if (_requireAuthentication)
            {
                AuthEvents.StateChanged.Unregister(OnAuthStateChanged);
            }
            
            DisconnectWebSocket();
            base.Deactivate();
            
            _connectionStatus = "Disconnected";
        }

        public override void FetchData()
        {
            // WebSocket receives data through events, not by polling
            // This method is not used but required by the base class
        }

        private void OnAuthStateChanged(AuthChangedEventData data)
        {
            _isAuthenticated = data.IsAuthenticated;
            
            if (_isAuthenticated)
            {
                if (_tokenType != AuthTokenType.QueryParameter)
                {
                    ApplyAuthToken();
                }
                
                if (_webSocket == null || _webSocket.State == WebSocketState.Closed)
                {
                    ConnectWebSocket();
                }
            }
            else
            {
                Debug.LogWarning($"[WebSocketDataProvider] {name}: Authentication lost, disconnecting...");
                DisconnectWebSocket();
            }
        }

        private void ApplyAuthToken()
        {
            if (AuthManager.Instance == null) return;
            
            string token = AuthManager.Instance.AuthToken;
            if (string.IsNullOrEmpty(token)) return;
            
            switch (_tokenType)
            {
                case AuthTokenType.Bearer:
                    _headers["Authorization"] = $"Bearer {token}";
                    break;
                case AuthTokenType.Custom:
                    _headers[_customTokenHeaderName] = token;
                    break;
            }
            
            if (_logMessages)
            {
                Debug.Log($"[WebSocketDataProvider] {name}: Auth token applied");
            }
        }

        private void ConnectWebSocket()
        {
            // Explicit fire-and-forget: the async method owns its exceptions.
            _ = ConnectWebSocketAsync();
        }

        private async Awaitable ConnectWebSocketAsync()
        {
            if (_isConnecting || (_webSocket != null && _webSocket.State == WebSocketState.Open))
            {
                Debug.LogWarning($"[WebSocketDataProvider] {name}: Already connected or connecting");
                return;
            }

            try
            {
                _isConnecting = true;
                _connectionStartTime = Time.time;
                _connectionStatus = $"Connecting (Attempt {reconnectAttemptCount + 1})";
                
                string finalUrl = BuildConnectionUrl();
                
                if (_logMessages)
                {
                    Debug.Log($"[WebSocketDataProvider] {name}: Connecting to {Molca.Networking.Utils.LogRedaction.RedactUrl(finalUrl)}");
                }

                _webSocket = _headers.Count > 0 
                    ? new WebSocket(finalUrl, _headers) 
                    : new WebSocket(finalUrl);
                
                // Register event handlers
                _webSocket.OnOpen += OnWebSocketOpen;
                _webSocket.OnMessage += OnWebSocketMessage;
                _webSocket.OnError += OnWebSocketError;
                _webSocket.OnClose += OnWebSocketClose;
                
                await _webSocket.Connect();
            }
            catch (Exception ex)
            {
                _isConnecting = false;
                _connectionStatus = "Connection Failed";
                Debug.LogError($"[WebSocketDataProvider] {name}: Connection error: {ex.Message}");
                HandleReconnect();
            }
        }

        private string BuildConnectionUrl()
        {
            string finalUrl = _url;
            
            // Ensure proper protocol
            if (!finalUrl.StartsWith("ws://") && !finalUrl.StartsWith("wss://"))
            {
                finalUrl = (_useSecureConnection ? "wss://" : "ws://") + finalUrl;
            }
            
            // Apply token as query parameter if needed
            if (_requireAuthentication && _tokenType == AuthTokenType.QueryParameter && AuthManager.Instance != null)
            {
                string token = AuthManager.Instance.AuthToken;
                if (!string.IsNullOrEmpty(token))
                {
                    char separator = finalUrl.Contains("?") ? '&' : '?';
                    finalUrl = $"{finalUrl}{separator}token={token}";
                }
            }
            
            return finalUrl;
        }

        private void DisconnectWebSocket()
        {
            if (_webSocket == null) return;

            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    _webSocket.Close();
                }
                
                // Unregister event handlers
                _webSocket.OnOpen -= OnWebSocketOpen;
                _webSocket.OnMessage -= OnWebSocketMessage;
                _webSocket.OnError -= OnWebSocketError;
                _webSocket.OnClose -= OnWebSocketClose;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Error during disconnect: {ex.Message}");
            }
            finally
            {
                _webSocket = null;
            }
        }

        private void OnWebSocketOpen()
        {
            _isConnecting = false;
            _reconnectAttemptCount = 0;
            _reconnectPolicy?.Reset(); // healthy connection clears the backoff budget
            _connectionStatus = "Connected";
            _lastPingTime = Time.time;
            
            if (_logMessages)
            {
                Debug.Log($"[WebSocketDataProvider] {name}: Connection established");
            }
        }

        private void OnWebSocketMessage(byte[] data)
        {
            try
            {
                string message = Encoding.UTF8.GetString(data);
                
                if (_logRawData)
                {
                    Debug.Log($"[WebSocketDataProvider] {name}: Raw message: {message}");
                }
                
                // Check if this is a pong response
                if (IsPongMessage(message))
                {
                    if (_logMessages)
                    {
                        Debug.Log($"[WebSocketDataProvider] {name}: Pong received");
                    }
                    return;
                }
                
                // Parse message based on format
                string processedMessage = ParseMessageByFormat(message);
                
                if (string.IsNullOrEmpty(processedMessage))
                {
                    if (_logMessages)
                    {
                        Debug.Log($"[WebSocketDataProvider] {name}: Message filtered out or empty");
                    }
                    return;
                }
                
                if (_logMessages)
                {
                    Debug.Log($"[WebSocketDataProvider] {name}: Processing message");
                }
                
                OnDataFetched(processedMessage);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Error processing message: {ex.Message}");
            }
        }

        private void OnWebSocketError(string errorMessage)
        {
            _connectionStatus = $"Error: {errorMessage}";
            Debug.LogError($"[WebSocketDataProvider] {name}: WebSocket error: {errorMessage}");
        }

        private void OnWebSocketClose(WebSocketCloseCode closeCode)
        {
            _isConnecting = false;
            _connectionStatus = $"Closed ({closeCode})";
            
            if (_logMessages)
            {
                Debug.Log($"[WebSocketDataProvider] {name}: Connection closed with code: {closeCode}");
            }
            
            if (!_isManualDisconnect && IsActive)
            {
                HandleReconnect();
            }
        }

        private void HandleReconnect()
        {
            // Explicit fire-and-forget: the async method owns its exceptions.
            _ = HandleReconnectAsync();
        }

        private async Awaitable HandleReconnectAsync()
        {
            if (!_autoReconnect || _isManualDisconnect || !IsActive)
            {
                return;
            }

            // Re-read the current auth token before reconnecting so a refresh (Sprint 39)
            // is applied to the header path (query-parameter mode re-reads it in
            // BuildConnectionUrl). A reconnect that then fails auth surfaces; it won't spin.
            if (_requireAuthentication && _tokenType != AuthTokenType.QueryParameter)
            {
                ApplyAuthToken();
            }

            bool canRetry;
            try
            {
                // Exponential backoff + jitter, capped, bounded by _maxReconnectAttempts.
                canRetry = await _reconnectPolicy.WaitForNextAttemptAsync(LifetimeToken);
            }
            catch (OperationCanceledException)
            {
                // Provider deactivated while waiting — abandon the reconnect.
                return;
            }

            if (!canRetry)
            {
                _connectionStatus = "Max Reconnect Attempts Reached";
                Debug.LogError($"[WebSocketDataProvider] {name}: Max reconnect attempts ({_maxReconnectAttempts}) reached");
                return;
            }

            _reconnectAttemptCount = _reconnectPolicy.AttemptCount;
            _connectionStatus = $"Reconnecting (attempt {_reconnectAttemptCount})";

            if (_logMessages)
            {
                Debug.Log($"[WebSocketDataProvider] {name}: Reconnecting... (attempt {_reconnectAttemptCount})");
            }

            if (!_isManualDisconnect && IsActive)
            {
                ConnectWebSocket();
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
                if (message.Contains($"\"{messageTypeFieldName}\""))
                {
                    var startIndex = message.IndexOf($"\"{messageTypeFieldName}\"") + _messageTypeFieldName.Length + 3;
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

        private void SendPing()
        {
            // Explicit fire-and-forget: the async method owns its exceptions.
            _ = SendPingAsync();
        }

        private async Awaitable SendPingAsync()
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                return;
            }
            
            try
            {
                await _webSocket.SendText(_pingMessage);
                
                if (_logMessages)
                {
                    Debug.Log($"[WebSocketDataProvider] {name}: Ping sent");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Error sending ping: {ex.Message}");
            }
        }

        /// <summary>
        /// Send a custom message through the WebSocket connection
        /// </summary>
        /// <returns>Completes once the message is sent. Awaiting is optional.</returns>
        public async Awaitable SendMessage(string message)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                Debug.LogWarning($"[WebSocketDataProvider] {name}: Cannot send message - not connected");
                return;
            }
            
            try
            {
                await _webSocket.SendText(message);
                
                if (_logMessages)
                {
                    Debug.Log($"[WebSocketDataProvider] {name}: Message sent: {message}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Error sending message: {ex.Message}");
            }
        }

        /// <summary>
        /// Send binary data through the WebSocket connection
        /// </summary>
        /// <returns>Completes once the data is sent. Awaiting is optional.</returns>
        public async Awaitable SendBinary(byte[] data)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                Debug.LogWarning($"[WebSocketDataProvider] {name}: Cannot send binary data - not connected");
                return;
            }
            
            try
            {
                await _webSocket.Send(data);
                
                if (_logMessages)
                {
                    Debug.Log($"[WebSocketDataProvider] {name}: Binary data sent ({data.Length} bytes)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: Error sending binary data: {ex.Message}");
            }
        }

        /// <summary>
        /// Manually trigger a reconnection
        /// </summary>
        public void Reconnect()
        {
            if (_logMessages)
            {
                Debug.Log($"[WebSocketDataProvider] {name}: Manual reconnect triggered");
            }
            
            DisconnectWebSocket();
            _reconnectAttemptCount = 0;
            ConnectWebSocket();
        }

        /// <summary>
        /// Check if connected
        /// </summary>
        public bool IsConnected => _webSocket != null && _webSocket.State == WebSocketState.Open;

        /// <summary>
        /// Get current connection state
        /// </summary>
        public WebSocketState ConnectionState => _webSocket?.State ?? WebSocketState.Closed;

        /// <summary>
        /// Get connection status string
        /// </summary>
        public string ConnectionStatus => _connectionStatus;

        public override bool ValidateConfiguration()
        {
            if (!base.ValidateConfiguration())
            {
                return false;
            }
            
            if (string.IsNullOrEmpty(_url))
            {
                Debug.LogError($"[WebSocketDataProvider] {name}: URL is not set!");
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

        private void Update()
        {
            // Dispatch queued messages on the main thread
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                _webSocket.DispatchMessageQueue();
#endif
                
                // Handle ping/pong
                if (_enablePingPong && Time.time - _lastPingTime >= _pingIntervalSeconds)
                {
                    _lastPingTime = Time.time;
                    SendPing();
                }
                
                // Check for connection timeout
                if (_isConnecting && Time.time - _connectionStartTime >= _connectionTimeoutSeconds)
                {
                    Debug.LogError($"[WebSocketDataProvider] {name}: Connection timeout");
                    DisconnectWebSocket();
                    HandleReconnect();
                }
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