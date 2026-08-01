using System;
using System.Collections.Generic;
using UnityEngine;
using Molca.Networking.Http.Models;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// A logical backend — the service half of a <c>NetworkRouteKey</c>. Carries its per-environment
    /// bindings, host allowlist, credential scope, default policy, and default headers.
    /// Serialized inside <see cref="NetworkCatalog"/>.
    /// </summary>
    /// <remarks>
    /// The binding list is the central multi-environment authoring surface. A missing entry is
    /// meaningful: it means "this service does not exist in that environment", and the resolver
    /// reports it rather than substituting another environment's origin.
    /// </remarks>
    [Serializable]
    public class NetworkServiceDefinition
    {
        [SerializeField] private string _id = "";
        [SerializeField] private string _displayName = "";

        [Tooltip("Protocols this service speaks. A binding must supply an origin for each declared protocol.")]
        [SerializeField] private NetworkProtocols _protocols = NetworkProtocols.Http;

        [Tooltip("Owner or team notes shown in the Hub. Never include credentials.")]
        [SerializeField, TextArea(1, 4)] private string _ownerNotes = "";

        [Header("Bindings")]
        [SerializeField] private List<NetworkServiceBinding> _bindings = new List<NetworkServiceBinding>();

        [Header("Safety")]
        [Tooltip("Hosts this service may talk to, e.g. api.example.com or *.example.com. Empty derives the allowlist from the bound origins.")]
        [SerializeField] private List<string> _allowedHostPatterns = new List<string>();

        [Tooltip("Credential profile ID applied to this service. Empty means anonymous.")]
        [SerializeField] private string _credentialProfileId = "";

        [Tooltip("Policy profile ID for this service. Empty inherits the environment or catalog default.")]
        [SerializeField] private string _policyProfileId = "";

        [Header("Defaults")]
        [Tooltip("Non-secret headers added to every request to this service. Request headers win.")]
        [SerializeField] private List<HttpHeader> _defaultHeaders = new List<HttpHeader>();

        [Header("Endpoints")]
        [Tooltip("Endpoint collections authored for this service.")]
        [SerializeField] private List<NetworkEndpointCollection> _endpointCollections = new List<NetworkEndpointCollection>();

        [Tooltip("Endpoint ID used for health checks, if any.")]
        [SerializeField] private string _healthEndpointId = "";

        /// <summary>Stable kebab-case identifier.</summary>
        public string Id => _id;

        /// <summary>Human-readable name.</summary>
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? _id : _displayName;

        /// <summary>Protocols this service declares.</summary>
        public NetworkProtocols Protocols => _protocols;

        /// <summary>Owner or team notes. Non-secret by contract.</summary>
        public string OwnerNotes => _ownerNotes;

        /// <summary>Per-environment bindings. May omit environments the service is absent from.</summary>
        public IReadOnlyList<NetworkServiceBinding> Bindings => _bindings;

        /// <summary>
        /// Authored host allowlist. When empty, <see cref="ResolveAllowedHosts"/> derives the
        /// allowlist from the bound origins instead.
        /// </summary>
        public IReadOnlyList<string> AllowedHostPatterns => _allowedHostPatterns;

        /// <summary>Credential profile ID, or empty for anonymous.</summary>
        public string CredentialProfileId => _credentialProfileId;

        /// <summary>Service-level policy override ID, or empty to inherit.</summary>
        public string PolicyProfileId => _policyProfileId;

        /// <summary>Non-secret default headers merged into every request. Request headers win.</summary>
        public IReadOnlyList<HttpHeader> DefaultHeaders => _defaultHeaders;

        /// <summary>Endpoint collections authored for this service. Entries may be <c>null</c> if an asset was deleted.</summary>
        public IReadOnlyList<NetworkEndpointCollection> EndpointCollections => _endpointCollections;

        /// <summary>Endpoint ID used for health checks, or empty.</summary>
        public string HealthEndpointId => _healthEndpointId;

        /// <summary>Whether this service declares <paramref name="protocol"/>.</summary>
        /// <param name="protocol">A single protocol flag.</param>
        public bool Supports(NetworkProtocols protocol) => (_protocols & protocol) == protocol && protocol != NetworkProtocols.None;

        /// <summary>
        /// Finds the binding for an environment.
        /// </summary>
        /// <param name="environmentId">The environment to look up.</param>
        /// <returns>The binding, or <c>null</c> when this service has none for that environment.</returns>
        /// <remarks>
        /// Returns a disabled binding when one is authored — the caller distinguishes
        /// "not configured" from "deliberately turned off", which are different findings.
        /// </remarks>
        public NetworkServiceBinding FindBinding(string environmentId)
        {
            if (_bindings == null || string.IsNullOrEmpty(environmentId))
                return null;

            for (int i = 0; i < _bindings.Count; i++)
            {
                var binding = _bindings[i];
                if (binding != null && string.Equals(binding.EnvironmentId, environmentId, StringComparison.Ordinal))
                    return binding;
            }
            return null;
        }

        /// <summary>
        /// The effective host allowlist: the authored patterns, or — when none are authored — the
        /// hosts of every origin bound to this service.
        /// </summary>
        /// <returns>A list of host patterns. Never <c>null</c>; may be empty when nothing is bound.</returns>
        /// <remarks>
        /// Deriving from the bound origins keeps the common case ergonomic without ever defaulting to
        /// "any host": if nothing is bound, nothing is allowed.
        /// </remarks>
        public List<string> ResolveAllowedHosts()
        {
            if (_allowedHostPatterns != null && _allowedHostPatterns.Count > 0)
                return new List<string>(_allowedHostPatterns);

            var hosts = new List<string>();
            if (_bindings == null)
                return hosts;

            foreach (var binding in _bindings)
            {
                if (binding == null) continue;

                AddHost(hosts, binding.HttpOrigin);
                AddHost(hosts, binding.SseOrigin);
                AddHost(hosts, binding.WebSocketOrigin);
                AddHost(hosts, binding.SocketIoOrigin);
            }
            return hosts;
        }

        private static void AddHost(List<string> hosts, string origin)
        {
            string host = NetworkHostRule.HostOf(origin);
            if (host != null && !hosts.Contains(host))
                hosts.Add(host);
        }

        /// <summary>
        /// Creates a service in code. Used by migration, import, and tests.
        /// </summary>
        /// <param name="id">Stable identifier; must satisfy <see cref="NetworkIds.IsValid"/>.</param>
        /// <param name="displayName">Human-readable name, or <c>null</c> to reuse <paramref name="id"/>.</param>
        /// <param name="protocols">Protocols the service declares.</param>
        internal static NetworkServiceDefinition Create(
            string id,
            string displayName,
            NetworkProtocols protocols)
        {
            return new NetworkServiceDefinition
            {
                _id = id,
                _displayName = displayName ?? id,
                _protocols = protocols
            };
        }

        /// <summary>Adds a binding. Editing entry point for migration and tests.</summary>
        /// <param name="binding">The binding to append.</param>
        internal void AddBinding(NetworkServiceBinding binding)
        {
            _bindings ??= new List<NetworkServiceBinding>();
            _bindings.Add(binding);
        }
    }
}
