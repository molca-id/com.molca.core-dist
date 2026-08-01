using System;
using UnityEngine;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// Protocols a service can speak. Flags, because one service commonly offers several.
    /// </summary>
    [Flags]
    public enum NetworkProtocols
    {
        /// <summary>No protocol declared.</summary>
        None = 0,

        /// <summary>Request/response HTTP.</summary>
        Http = 1 << 0,

        /// <summary>Server-Sent Events streaming.</summary>
        ServerSentEvents = 1 << 1,

        /// <summary>Raw WebSocket.</summary>
        WebSocket = 1 << 2,

        /// <summary>Socket.IO over WebSocket/long-polling.</summary>
        SocketIO = 1 << 3
    }

    /// <summary>
    /// One environment's concrete origins for a service. Serialized inside
    /// <see cref="NetworkServiceDefinition"/>.
    /// </summary>
    /// <remarks>
    /// A service may deliberately omit a binding for an environment. The resolver then returns a
    /// typed <see cref="NetworkErrorCategory.RouteResolution"/> failure rather than falling back to
    /// another environment — silent fallback is how a staging build ends up talking to production.
    /// </remarks>
    [Serializable]
    public class NetworkServiceBinding
    {
        [SerializeField] private string _environmentId = "";

        [Tooltip("Absolute HTTP origin, e.g. https://api.example.com/v1. Required when the service declares Http.")]
        [SerializeField] private string _httpOrigin = "";

        [Tooltip("Absolute SSE origin. Leave empty to reuse the HTTP origin.")]
        [SerializeField] private string _sseOrigin = "";

        [Tooltip("Absolute WebSocket origin, e.g. wss://stream.example.com.")]
        [SerializeField] private string _webSocketOrigin = "";

        [Tooltip("Absolute Socket.IO origin, e.g. https://rt.example.com.")]
        [SerializeField] private string _socketIoOrigin = "";

        [Tooltip("Socket.IO handshake path. Defaults to /socket.io/ when empty.")]
        [SerializeField] private string _socketIoPath = "";

        [Tooltip("Non-secret region or tenant label surfaced in diagnostics.")]
        [SerializeField] private string _regionLabel = "";

        [Tooltip("Disable to make this environment resolve as an explicit configuration error instead of sending traffic.")]
        [SerializeField] private bool _enabled = true;

        /// <summary>ID of the environment this binding applies to.</summary>
        public string EnvironmentId => _environmentId;

        /// <summary>Authored HTTP origin. Empty when the service does not serve HTTP here.</summary>
        public string HttpOrigin => _httpOrigin;

        /// <summary>
        /// Authored SSE origin, falling back to <see cref="HttpOrigin"/> when unset — SSE is an HTTP
        /// response, so sharing the origin is the common case.
        /// </summary>
        public string SseOrigin => string.IsNullOrEmpty(_sseOrigin) ? _httpOrigin : _sseOrigin;

        /// <summary>Authored WebSocket origin. Empty when the service does not serve WebSocket here.</summary>
        public string WebSocketOrigin => _webSocketOrigin;

        /// <summary>Authored Socket.IO origin. Empty when the service does not serve Socket.IO here.</summary>
        public string SocketIoOrigin => _socketIoOrigin;

        /// <summary>Socket.IO handshake path, defaulting to <c>/socket.io/</c>.</summary>
        public string SocketIoPath => string.IsNullOrEmpty(_socketIoPath) ? "/socket.io/" : _socketIoPath;

        /// <summary>Non-secret region or tenant label.</summary>
        public string RegionLabel => _regionLabel;

        /// <summary>Whether this binding may be resolved. A disabled binding is a typed error, not a fallback.</summary>
        public bool Enabled => _enabled;

        /// <summary>
        /// Returns the authored origin for one protocol.
        /// </summary>
        /// <param name="protocol">A single protocol flag.</param>
        /// <returns>The origin string, or empty when this binding does not serve that protocol.</returns>
        public string OriginFor(NetworkProtocols protocol)
        {
            switch (protocol)
            {
                case NetworkProtocols.Http: return _httpOrigin;
                case NetworkProtocols.ServerSentEvents: return SseOrigin;
                case NetworkProtocols.WebSocket: return _webSocketOrigin;
                case NetworkProtocols.SocketIO: return _socketIoOrigin;
                default: return string.Empty;
            }
        }

        /// <summary>Whether this binding declares a usable origin for <paramref name="protocol"/>.</summary>
        /// <param name="protocol">A single protocol flag.</param>
        public bool HasOriginFor(NetworkProtocols protocol) => !string.IsNullOrWhiteSpace(OriginFor(protocol));

        /// <summary>
        /// Creates a binding in code. Used by migration, import, and tests.
        /// </summary>
        /// <param name="environmentId">Environment this binding serves.</param>
        /// <param name="httpOrigin">Absolute HTTP origin, or <c>null</c>.</param>
        internal static NetworkServiceBinding Create(string environmentId, string httpOrigin)
        {
            return new NetworkServiceBinding
            {
                _environmentId = environmentId,
                _httpOrigin = httpOrigin ?? string.Empty
            };
        }
    }
}
