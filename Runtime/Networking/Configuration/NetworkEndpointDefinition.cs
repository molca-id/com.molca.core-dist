using System;
using System.Collections.Generic;
using UnityEngine;
using Molca.Networking.Http.Models;

namespace Molca.Networking.Configuration
{
    /// <summary>Which transport an endpoint template describes.</summary>
    public enum NetworkEndpointKind
    {
        /// <summary>A request/response HTTP call.</summary>
        Http = 0,

        /// <summary>A Server-Sent Events stream.</summary>
        ServerSentEvents,

        /// <summary>A WebSocket connection.</summary>
        WebSocket,

        /// <summary>A Socket.IO connection.</summary>
        SocketIO
    }

    /// <summary>
    /// Whether calling an endpoint changes server state. Drives retry eligibility and the request
    /// console's production confirmation.
    /// </summary>
    public enum NetworkMutationClass
    {
        /// <summary>Read-only. Safe to repeat and to call against production from the console.</summary>
        Safe = 0,

        /// <summary>Changes state. Not retried without an idempotency key; confirmed per send in production.</summary>
        Mutating,

        /// <summary>Irreversibly removes data. Always confirmed, never retried automatically.</summary>
        Destructive
    }

    /// <summary>Where an endpoint template came from.</summary>
    public enum NetworkEndpointSource
    {
        /// <summary>Authored by hand in the Hub or Inspector.</summary>
        Authored = 0,

        /// <summary>Produced by legacy migration from an <see cref="HttpRequestAsset"/>.</summary>
        LegacyMigration,

        /// <summary>Imported from an OpenAPI document.</summary>
        OpenApi
    }

    /// <summary>Declared shape of one endpoint parameter.</summary>
    /// <remarks>
    /// Powers the Hub's parameter editor and the request console. Runtime callers are free to ignore
    /// endpoint templates entirely and issue a route plus a relative path.
    /// </remarks>
    [Serializable]
    public class NetworkParameterDefinition
    {
        [SerializeField] private string _name = "";
        [SerializeField] private bool _required = false;

        [Tooltip("Value used when the caller supplies none. Never a credential.")]
        [SerializeField] private string _defaultValue = "";

        [SerializeField, TextArea(1, 3)] private string _description = "";

        [Tooltip("Redact this parameter's value in diagnostics and request history.")]
        [SerializeField] private bool _sensitive = false;

        /// <summary>Parameter name as it appears in the path, query, or header.</summary>
        public string Name => _name;

        /// <summary>Whether a value must be supplied.</summary>
        public bool Required => _required;

        /// <summary>Value used when the caller supplies none.</summary>
        public string DefaultValue => _defaultValue;

        /// <summary>Author description shown in the Hub.</summary>
        public string Description => _description;

        /// <summary>Whether this parameter's value is redacted in diagnostics and history.</summary>
        public bool Sensitive => _sensitive;

        /// <summary>Creates a parameter definition in code. Used by import and tests.</summary>
        /// <param name="name">Parameter name.</param>
        /// <param name="required">Whether a value must be supplied.</param>
        internal static NetworkParameterDefinition Create(string name, bool required)
        {
            return new NetworkParameterDefinition { _name = name, _required = required };
        }
    }

    /// <summary>
    /// A reusable endpoint template: method or stream kind, relative path, parameter shape, and
    /// optional policy override. Serialized inside <see cref="NetworkEndpointCollection"/>.
    /// </summary>
    /// <remarks>
    /// An endpoint does not carry an origin. It carries a service ID and a relative path; the origin
    /// arrives from the service's binding for whichever environment the call targets. That is what
    /// makes one template usable across every environment.
    /// </remarks>
    [Serializable]
    public class NetworkEndpointDefinition
    {
        [SerializeField] private string _id = "";
        [SerializeField] private string _displayName = "";

        [Tooltip("Service this endpoint belongs to. Must match a service in the catalog.")]
        [SerializeField] private string _serviceId = "";

        [SerializeField] private NetworkEndpointKind _kind = NetworkEndpointKind.Http;

        [Tooltip("HTTP method. Ignored for stream kinds.")]
        [SerializeField] private HttpMethod _method = HttpMethod.GET;

        [Tooltip("Path relative to the service origin, e.g. users/{id}/profile. Must not be absolute.")]
        [SerializeField] private string _relativePath = "";

        [Header("Parameters")]
        [SerializeField] private List<NetworkParameterDefinition> _pathParameters = new List<NetworkParameterDefinition>();
        [SerializeField] private List<NetworkParameterDefinition> _queryParameters = new List<NetworkParameterDefinition>();
        [SerializeField] private List<NetworkParameterDefinition> _headerParameters = new List<NetworkParameterDefinition>();

        [Header("Body and response")]
        [SerializeField] private BodyType _bodyType = BodyType.None;

        [Tooltip("Example request body shown in the Hub. Never include real credentials or customer data.")]
        [SerializeField, TextArea(2, 8)] private string _requestBodyExample = "";

        [SerializeField] private ResponseType _expectedResponseType = ResponseType.Json;

        [Tooltip("Response type name for documentation and future client generation.")]
        [SerializeField] private string _responseTypeName = "";

        [Header("Policy")]
        [Tooltip("Policy profile ID for this endpoint. Empty inherits the service, environment, or catalog default.")]
        [SerializeField] private string _policyProfileId = "";

        [SerializeField] private NetworkMutationClass _mutationClass = NetworkMutationClass.Safe;

        [Tooltip("Require an idempotency key before a mutating call may be retried.")]
        [SerializeField] private bool _requiresIdempotencyKey = false;

        [Header("Metadata")]
        [SerializeField] private List<string> _tags = new List<string>();
        [SerializeField, TextArea(1, 4)] private string _description = "";

        [Header("Source")]
        [SerializeField] private NetworkEndpointSource _source = NetworkEndpointSource.Authored;

        [Tooltip("Where an imported or migrated endpoint came from — an asset GUID or an OpenAPI operation ID.")]
        [SerializeField] private string _sourceReference = "";

        [Tooltip("Content hash of the imported definition, so re-import can show a diff rather than overwrite local edits.")]
        [SerializeField] private string _sourceHash = "";

        /// <summary>Stable kebab-case identifier, unique within the catalog.</summary>
        public string Id => _id;

        /// <summary>Human-readable name.</summary>
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? _id : _displayName;

        /// <summary>ID of the service that serves this endpoint.</summary>
        public string ServiceId => _serviceId;

        /// <summary>Which transport this template describes.</summary>
        public NetworkEndpointKind Kind => _kind;

        /// <summary>HTTP method. Meaningful only for <see cref="NetworkEndpointKind.Http"/>.</summary>
        public HttpMethod Method => _method;

        /// <summary>Path relative to the service's origin for the target environment.</summary>
        public string RelativePath => _relativePath;

        /// <summary>Declared path parameters.</summary>
        public IReadOnlyList<NetworkParameterDefinition> PathParameters => _pathParameters;

        /// <summary>Declared query parameters.</summary>
        public IReadOnlyList<NetworkParameterDefinition> QueryParameters => _queryParameters;

        /// <summary>Declared header parameters.</summary>
        public IReadOnlyList<NetworkParameterDefinition> HeaderParameters => _headerParameters;

        /// <summary>Request body encoding.</summary>
        public BodyType BodyType => _bodyType;

        /// <summary>Example request body for authoring. Non-secret by contract.</summary>
        public string RequestBodyExample => _requestBodyExample;

        /// <summary>How the response body should be decoded.</summary>
        public ResponseType ExpectedResponseType => _expectedResponseType;

        /// <summary>Documented response type name, for docs and future generated clients.</summary>
        public string ResponseTypeName => _responseTypeName;

        /// <summary>Endpoint-level policy override ID, or empty to inherit.</summary>
        public string PolicyProfileId => _policyProfileId;

        /// <summary>Whether this endpoint changes server state.</summary>
        public NetworkMutationClass MutationClass => _mutationClass;

        /// <summary>Whether a mutating call needs an idempotency key before it may be retried.</summary>
        public bool RequiresIdempotencyKey => _requiresIdempotencyKey;

        /// <summary>Free-text tags for search and grouping.</summary>
        public IReadOnlyList<string> Tags => _tags;

        /// <summary>Author description shown in the Hub.</summary>
        public string Description => _description;

        /// <summary>Where this template came from.</summary>
        public NetworkEndpointSource Source => _source;

        /// <summary>Origin reference for a migrated or imported template.</summary>
        public string SourceReference => _sourceReference;

        /// <summary>Content hash of the imported definition, enabling re-import diffs.</summary>
        public string SourceHash => _sourceHash;

        /// <summary>
        /// Whether this endpoint is safe to repeat: a read-only method, or a mutating one carrying an
        /// idempotency key.
        /// </summary>
        /// <remarks>
        /// Consulted by <c>NetworkPolicyProfile.RetryRequiresIdempotence</c>. A mutating call is not
        /// retried merely because it failed (plan §6.4).
        /// </remarks>
        public bool IsIdempotent =>
            _mutationClass == NetworkMutationClass.Safe ||
            (_requiresIdempotencyKey && _mutationClass != NetworkMutationClass.Destructive);

        /// <summary>
        /// Whether <paramref name="method"/> is safe to repeat by HTTP semantics alone.
        /// </summary>
        /// <param name="method">The method to classify.</param>
        /// <remarks>
        /// Matches the set the existing <c>HttpRetryPolicy</c> treats as idempotent, so routed and
        /// legacy retry decisions agree.
        /// </remarks>
        public static bool IsIdempotentMethod(HttpMethod method)
        {
            switch (method)
            {
                case HttpMethod.GET:
                case HttpMethod.HEAD:
                case HttpMethod.OPTIONS:
                case HttpMethod.PUT:
                case HttpMethod.DELETE:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The protocol this endpoint's <see cref="Kind"/> requires from its service binding.
        /// </summary>
        public NetworkProtocols RequiredProtocol
        {
            get
            {
                switch (_kind)
                {
                    case NetworkEndpointKind.ServerSentEvents: return NetworkProtocols.ServerSentEvents;
                    case NetworkEndpointKind.WebSocket: return NetworkProtocols.WebSocket;
                    case NetworkEndpointKind.SocketIO: return NetworkProtocols.SocketIO;
                    default: return NetworkProtocols.Http;
                }
            }
        }

        /// <summary>
        /// Creates an endpoint in code. Used by migration, import, and tests.
        /// </summary>
        /// <param name="id">Stable identifier; must satisfy <see cref="NetworkIds.IsValid"/>.</param>
        /// <param name="serviceId">Owning service ID.</param>
        /// <param name="method">HTTP method.</param>
        /// <param name="relativePath">Path relative to the service origin.</param>
        internal static NetworkEndpointDefinition Create(
            string id,
            string serviceId,
            HttpMethod method,
            string relativePath)
        {
            return new NetworkEndpointDefinition
            {
                _id = id,
                _displayName = id,
                _serviceId = serviceId,
                _method = method,
                _relativePath = relativePath ?? string.Empty,
                _mutationClass = IsIdempotentMethod(method) && method != HttpMethod.PUT && method != HttpMethod.DELETE
                    ? NetworkMutationClass.Safe
                    : NetworkMutationClass.Mutating
            };
        }

        /// <summary>Records import provenance. Used by migration and OpenAPI import.</summary>
        /// <param name="source">Where the definition came from.</param>
        /// <param name="reference">Asset GUID or operation ID.</param>
        /// <param name="hash">Content hash of the imported definition.</param>
        internal void SetSource(NetworkEndpointSource source, string reference, string hash)
        {
            _source = source;
            _sourceReference = reference ?? string.Empty;
            _sourceHash = hash ?? string.Empty;
        }
    }
}
