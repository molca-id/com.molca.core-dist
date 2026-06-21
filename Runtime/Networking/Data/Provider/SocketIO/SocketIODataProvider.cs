#if MOLCA_SOCKETIO
using System;
using System.Collections.Generic;
using Molca.Attributes;
using Molca.Networking.Auth;
using SocketIOClient;
using SocketIOClient.Newtonsoft.Json;
using SocketIOClient.Transport;
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

    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "SocketIODataProvider", menuName = "Molca/Networking/SocketIODataProvider", order = 20)]
    public class SocketIODataProvider : DataProvider
    {
        private const string DefaultPath = "/socket.io";

        [Header("Socket.IO Settings")]
        [SerializeField, FormerlySerializedAs("serverUrl")] private string _serverUrl;
        [SerializeField, FormerlySerializedAs("useSecureConnection")] private bool _useSecureConnection = true;
        [SerializeField, FormerlySerializedAs("socketPath")] private string _socketPath = DefaultPath;
        [SerializeField, FormerlySerializedAs("connectionTimeoutSeconds")] private float _connectionTimeoutSeconds = 20f;

        [Header("Reconnection")]
        [SerializeField, FormerlySerializedAs("autoReconnect")] private bool _autoReconnect = true;
        [SerializeField, FormerlySerializedAs("maxReconnectAttempts")] private int _maxReconnectAttempts = -1; // -1 => unlimited
        [SerializeField, FormerlySerializedAs("reconnectDelaySeconds")] private float _reconnectDelaySeconds = 2f;
        [SerializeField, FormerlySerializedAs("reconnectDelayMaxSeconds")] private float _reconnectDelayMaxSeconds = 10f;
        [SerializeField, FormerlySerializedAs("randomizationFactor")] private float _randomizationFactor = 0.5f;

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
        [SerializeField, FormerlySerializedAs("connectionStatus"), ReadOnly] private string _connectionStatus = "Disconnected";
        [SerializeField, FormerlySerializedAs("reconnectAttemptCount"), ReadOnly] private int _reconnectAttemptCount = 0;

        private SocketIOUnity _socket;
        private bool _isManualDisconnect;
        private bool _isConnecting;
        private bool _isAuthenticated;
        private Dictionary<string, SocketIOEventMapping> _mappingLookup;
        // Auth token captured at connect time. The library's native reconnection reuses
        // construction-time headers, so on a reconnect attempt we compare against the
        // current token and force a fresh connect when it changed (Sprint 39 refresh).
        private string _tokenAtConnect;
        private bool _refreshingAuth;

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
                Debug.LogError($"[SocketIODataProvider] {name}: Configuration validation failed!");
                return;
            }

            base.Activate();

            _connectionStatus = "Initializing";
            _reconnectAttemptCount = 0;
            _isManualDisconnect = false;

            BuildMappingLookup();

            if (_requireAuthentication)
            {
                AuthEvents.StateChanged.Register(OnAuthStateChanged);

                if (AuthManager.Instance != null && AuthManager.Instance.IsAuthenticated)
                {
                    _isAuthenticated = true;
                    ConnectSocket();
                }
                else
                {
                    _connectionStatus = "Waiting for Authentication";
                    Debug.Log($"[SocketIODataProvider] {name}: Waiting for authentication...");
                }
            }
            else
            {
                ConnectSocket();
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

            DisconnectSocket();
            base.Deactivate();
            _connectionStatus = "Disconnected";
        }

        public override void FetchData()
        {
            // Socket.IO pushes data through events. No polling required.
        }

        private void OnAuthStateChanged(AuthChangedEventData data)
        {
            _isAuthenticated = data.IsAuthenticated;

            if (_isAuthenticated)
            {
                if (!IsConnected)
                {
                    ConnectSocket();
                }
            }
            else
            {
                Debug.LogWarning($"[SocketIODataProvider] {name}: Authentication lost, disconnecting...");
                DisconnectSocket();
            }
        }

        private void ConnectSocket()
        {
            if (_isConnecting || IsConnected)
            {
                return;
            }

            Uri uri = BuildServerUri();
            if (uri == null)
            {
                Debug.LogError($"[SocketIODataProvider] {name}: Invalid Socket.IO URL");
                return;
            }

            try
            {
                _isConnecting = true;
                _connectionStatus = $"Connecting ({uri})";

                var options = BuildOptions();
                // Record the token baked into these options so a later reconnect can
                // detect a refreshed token and rebuild.
                _tokenAtConnect = AuthManager.Instance != null ? AuthManager.Instance.AuthToken : null;
                _socket = new SocketIOUnity(uri, options);
                _socket.JsonSerializer = new NewtonsoftJsonSerializer();

                RegisterSocketCallbacks();
                RegisterEventHandlers();

                _socket.Connect();
            }
            catch (Exception ex)
            {
                _isConnecting = false;
                _connectionStatus = "Connection Failed";
                Debug.LogError($"[SocketIODataProvider] {name}: Connection error: {ex.Message}");
            }
        }

        private void DisconnectSocket()
        {
            if (_socket == null) return;

            try
            {
                if (_socket.Connected)
                {
                    _socket.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SocketIODataProvider] {name}: Error during disconnect: {ex.Message}");
            }
            finally
            {
                _socket.Dispose();
                _socket = null;
                _isConnecting = false;
            }
        }

        private void RegisterSocketCallbacks()
        {
            _socket.OnConnected += (sender, args) =>
            {
                _isConnecting = false;
                _reconnectAttemptCount = 0;
                _connectionStatus = "Connected";

                if (_logMessages)
                {
                    Debug.Log($"[SocketIODataProvider] {name}: Connected to {_serverUrl}");
                }
            };

            _socket.OnDisconnected += (sender, reason) =>
            {
                _connectionStatus = $"Disconnected ({reason})";

                if (_logMessages)
                {
                    Debug.Log($"[SocketIODataProvider] {name}: Disconnected ({reason})");
                }
            };

            _socket.OnError += (sender, error) =>
            {
                _connectionStatus = $"Error: {error}";
                Debug.LogError($"[SocketIODataProvider] {name}: Socket error: {error}");
            };

            _socket.OnReconnectAttempt += (sender, attempt) =>
            {
                _reconnectAttemptCount = attempt;
                _connectionStatus = $"Reconnecting (attempt {attempt})";

                if (_logMessages)
                {
                    Debug.Log($"[SocketIODataProvider] {name}: Reconnecting (attempt {attempt})");
                }

                RefreshAuthOnReconnect();
            };

            _socket.OnReconnectFailed += (sender, args) =>
            {
                _connectionStatus = "Reconnect Failed";
                Debug.LogError($"[SocketIODataProvider] {name}: Reconnect failed");
            };

            _socket.OnReconnectError += (sender, exception) =>
            {
                Debug.LogError($"[SocketIODataProvider] {name}: Reconnect error: {exception?.Message}");
            };
        }

        private void RegisterEventHandlers()
        {
            if (_socketIOEventMappings == null) return;

            foreach (var mapping in _socketIOEventMappings)
            {
                if (mapping == null || string.IsNullOrEmpty(mapping.eventName)) continue;

                _socket.OnUnityThread(mapping.eventName, response =>
                {
                    HandleSocketEvent(mapping.eventName, response);
                });
            }
        }

        private void HandleSocketEvent(string eventName, SocketIOResponse response)
        {
            if (!_mappingLookup.TryGetValue(eventName, out var mapping))
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

        /// <summary>
        /// On a native reconnect attempt, if the live auth token differs from the one
        /// baked into the current connection's options, tear down and reconnect with
        /// fresh options so the refreshed token is sent. No-op when auth isn't required
        /// or the token is unchanged (the library's own reconnect then proceeds).
        /// </summary>
        private void RefreshAuthOnReconnect()
        {
            if (!_requireAuthentication || _refreshingAuth)
                return;

            string current = AuthManager.Instance != null ? AuthManager.Instance.AuthToken : null;
            if (current == _tokenAtConnect)
                return;

            _refreshingAuth = true;
            try
            {
                if (_logMessages)
                {
                    Debug.Log($"[SocketIODataProvider] {name}: auth token changed; reconnecting with fresh credentials");
                }
                DisconnectSocket();
                ConnectSocket();
            }
            finally
            {
                _refreshingAuth = false;
            }
        }

        public void Reconnect()
        {
            if (_logMessages)
            {
                Debug.Log($"[SocketIODataProvider] {name}: Manual reconnect requested");
            }

            DisconnectSocket();
            ConnectSocket();
        }

        public void Emit(string eventName, string payloadJson = null)
        {
            if (_socket == null || !_socket.Connected)
            {
                Debug.LogWarning($"[SocketIODataProvider] {name}: Cannot emit '{eventName}' - not connected");
                return;
            }

            if (string.IsNullOrEmpty(payloadJson))
            {
                _socket.Emit(eventName);
            }
            else
            {
                _socket.EmitStringAsJSON(eventName, payloadJson);
            }

            if (_logMessages)
            {
                Debug.Log($"[SocketIODataProvider] {name}: Emitted event '{eventName}'");
            }
        }

        public bool IsConnected => _socket != null && _socket.Connected;
        public string ConnectionStatus => _connectionStatus;
        public int ReconnectAttemptCount => _reconnectAttemptCount;

        public override bool ValidateConfiguration()
        {
            if (string.IsNullOrEmpty(_serverUrl))
            {
                Debug.LogError($"[SocketIODataProvider] {name}: Server URL is not set!");
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

            if (_randomizationFactor < 0 || _randomizationFactor > 1)
            {
                Debug.LogError($"[SocketIODataProvider] {name}: Randomization factor must be between 0 and 1.");
                return false;
            }

            return true;
        }

        private Uri BuildServerUri()
        {
            string trimmed = _serverUrl?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return null;
            }

            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = (_useSecureConnection ? "https://" : "http://") + trimmed;
            }

            try
            {
                return new Uri(trimmed);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SocketIODataProvider] {name}: Invalid server URL '{_serverUrl}'. {ex.Message}");
                return null;
            }
        }

        private SocketIOOptions BuildOptions()
        {
            var options = new SocketIOOptions
            {
                Path = string.IsNullOrEmpty(_socketPath) ? DefaultPath : _socketPath,
                Transport = TransportProtocol.WebSocket,
                AutoUpgrade = false,
                ConnectionTimeout = TimeSpan.FromSeconds(_connectionTimeoutSeconds),
                Reconnection = _autoReconnect,
                ReconnectionDelay = Math.Max(100, _reconnectDelaySeconds * 1000f),
                ReconnectionDelayMax = Mathf.Max((int)(_reconnectDelayMaxSeconds * 1000f), (int)(_reconnectDelaySeconds * 1000f)),
                RandomizationFactor = _randomizationFactor,
                ReconnectionAttempts = _maxReconnectAttempts <= 0 ? int.MaxValue : _maxReconnectAttempts,
                Query = BuildQueryParameters(),
                ExtraHeaders = BuildHeaders()
            };

            return options;
        }

        private IEnumerable<KeyValuePair<string, string>> BuildQueryParameters()
        {
            var query = new Dictionary<string, string>();

            if (_requireAuthentication && _tokenType == AuthTokenType.QueryParameter)
            {
                string token = AuthManager.Instance != null ? AuthManager.Instance.AuthToken : null;
                if (!string.IsNullOrEmpty(token))
                {
                    string key = string.IsNullOrEmpty(_queryParameterName) ? "token" : _queryParameterName;
                    query[key] = token;
                }
            }

            return query.Count > 0 ? query : null;
        }

        private Dictionary<string, string> BuildHeaders()
        {
            if (!_requireAuthentication)
            {
                return null;
            }

            string token = AuthManager.Instance != null ? AuthManager.Instance.AuthToken : null;
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var headers = new Dictionary<string, string>();

            switch (_tokenType)
            {
                case AuthTokenType.Bearer:
                    headers["Authorization"] = $"Bearer {token}";
                    break;
                case AuthTokenType.Custom:
                    string headerName = string.IsNullOrEmpty(_customTokenHeaderName) ? "Authorization" : _customTokenHeaderName;
                    headers[headerName] = token;
                    break;
            }

            return headers.Count > 0 ? headers : null;
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