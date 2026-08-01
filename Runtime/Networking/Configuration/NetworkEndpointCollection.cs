using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// A coherent group of endpoint templates for one service or API surface. Project-owned
    /// <see cref="ScriptableObject"/>, referenced by <see cref="NetworkCatalog"/> and by the
    /// services that use it.
    /// </summary>
    /// <remarks>
    /// This asset is the merge-conflict and ownership boundary. Splitting endpoints across
    /// collections is what stops the catalog becoming a monolith, while still keeping endpoints out
    /// of one-asset-per-request territory (plan §5.2, §14).
    /// <para>
    /// Read-only configuration: nothing here is mutated at runtime.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(
        fileName = "Network Endpoints",
        menuName = "Molca/Networking/Endpoint Collection",
        order = 21)]
    public class NetworkEndpointCollection : ScriptableObject
    {
        [Tooltip("Stable kebab-case ID for this collection. Unique within the catalog.")]
        [SerializeField] private string _collectionId = "";

        [SerializeField] private string _displayName = "";

        [Tooltip("Default service for endpoints in this collection. An endpoint may name a different service.")]
        [SerializeField] private string _serviceId = "";

        [SerializeField, TextArea(1, 4)] private string _description = "";

        [Header("Endpoints")]
        [SerializeField] private List<NetworkEndpointDefinition> _endpoints = new List<NetworkEndpointDefinition>();

        [Header("Source")]
        [Tooltip("OpenAPI document or other source this collection was imported from, if any.")]
        [SerializeField] private string _sourceReference = "";

        /// <summary>Stable kebab-case identifier, unique within the catalog.</summary>
        public string CollectionId => _collectionId;

        /// <summary>Human-readable name, falling back to the asset name.</summary>
        public string DisplayName =>
            !string.IsNullOrEmpty(_displayName) ? _displayName :
            !string.IsNullOrEmpty(_collectionId) ? _collectionId : name;

        /// <summary>Default service for endpoints that do not name one.</summary>
        public string ServiceId => _serviceId;

        /// <summary>Author description shown in the Hub.</summary>
        public string Description => _description;

        /// <summary>The endpoint templates in this collection. Entries may be <c>null</c> after a bad edit.</summary>
        public IReadOnlyList<NetworkEndpointDefinition> Endpoints => _endpoints;

        /// <summary>Source document this collection was imported from, or empty.</summary>
        public string SourceReference => _sourceReference;

        /// <summary>
        /// Resolves the service an endpoint belongs to: its own <see cref="NetworkEndpointDefinition.ServiceId"/>,
        /// falling back to this collection's <see cref="ServiceId"/>.
        /// </summary>
        /// <param name="endpoint">The endpoint to resolve.</param>
        /// <returns>The service ID, or empty when neither the endpoint nor the collection names one.</returns>
        public string ResolveServiceId(NetworkEndpointDefinition endpoint)
        {
            if (endpoint == null) return _serviceId ?? string.Empty;
            return string.IsNullOrEmpty(endpoint.ServiceId) ? _serviceId ?? string.Empty : endpoint.ServiceId;
        }

        /// <summary>
        /// Finds an endpoint by ID.
        /// </summary>
        /// <param name="endpointId">The endpoint ID to look up.</param>
        /// <returns>The endpoint, or <c>null</c> when absent.</returns>
        public NetworkEndpointDefinition FindEndpoint(string endpointId)
        {
            if (_endpoints == null || string.IsNullOrEmpty(endpointId))
                return null;

            for (int i = 0; i < _endpoints.Count; i++)
            {
                var endpoint = _endpoints[i];
                if (endpoint != null && string.Equals(endpoint.Id, endpointId, StringComparison.Ordinal))
                    return endpoint;
            }
            return null;
        }

        /// <summary>Adds an endpoint. Editing entry point for migration, import, and tests.</summary>
        /// <param name="endpoint">The endpoint to append.</param>
        internal void AddEndpoint(NetworkEndpointDefinition endpoint)
        {
            _endpoints ??= new List<NetworkEndpointDefinition>();
            _endpoints.Add(endpoint);
        }

        /// <summary>Assigns identity fields on a freshly created asset.</summary>
        /// <param name="collectionId">Stable identifier.</param>
        /// <param name="displayName">Human-readable name, or <c>null</c> to reuse the ID.</param>
        /// <param name="serviceId">Default service for the collection's endpoints.</param>
        internal void Initialize(string collectionId, string displayName, string serviceId)
        {
            _collectionId = collectionId;
            _displayName = displayName ?? collectionId;
            _serviceId = serviceId ?? string.Empty;
        }
    }
}
