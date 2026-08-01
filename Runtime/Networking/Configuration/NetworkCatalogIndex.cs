using System;
using System.Collections.Generic;
using Molca.Networking.Routing;

namespace Molca.Networking.Configuration
{
    /// <summary>
    /// One endpoint together with the collection that owns it and the service it resolves to.
    /// </summary>
    public readonly struct NetworkEndpointEntry
    {
        /// <summary>The collection asset the endpoint is authored in.</summary>
        public readonly NetworkEndpointCollection Collection;

        /// <summary>The endpoint template.</summary>
        public readonly NetworkEndpointDefinition Endpoint;

        /// <summary>The resolved service ID, from the endpoint or its collection.</summary>
        public readonly string ServiceId;

        /// <summary>Creates an entry.</summary>
        /// <param name="collection">Owning collection.</param>
        /// <param name="endpoint">The endpoint template.</param>
        /// <param name="serviceId">Resolved service ID.</param>
        public NetworkEndpointEntry(
            NetworkEndpointCollection collection,
            NetworkEndpointDefinition endpoint,
            string serviceId)
        {
            Collection = collection;
            Endpoint = endpoint;
            ServiceId = serviceId;
        }
    }

    /// <summary>
    /// A built index over one <see cref="NetworkCatalog"/>: identifier lookups, duplicate detection,
    /// and reverse references.
    /// </summary>
    /// <remarks>
    /// Built once and then read-only, so validation, the Hub, and the runtime resolver all pay the
    /// scan cost once rather than doing linear searches per lookup. Building never throws on a
    /// malformed catalog — duplicates and blanks are collected into
    /// <see cref="DuplicateEnvironmentIds"/> and friends for the validator to report, because a
    /// broken catalog still has to be openable in the Hub in order to be fixed.
    /// </remarks>
    public sealed class NetworkCatalogIndex
    {
        private readonly Dictionary<string, NetworkEnvironmentProfile> _environments;
        private readonly Dictionary<string, NetworkServiceDefinition> _services;
        private readonly Dictionary<string, NetworkPolicyProfile> _policyProfiles;
        private readonly Dictionary<string, NetworkCredentialProfile> _credentialProfiles;
        private readonly Dictionary<string, NetworkEndpointCollection> _collections;
        private readonly Dictionary<string, NetworkEndpointEntry> _endpoints;

        private readonly List<string> _duplicateEnvironmentIds = new List<string>();
        private readonly List<string> _duplicateServiceIds = new List<string>();
        private readonly List<string> _duplicatePolicyProfileIds = new List<string>();
        private readonly List<string> _duplicateCredentialProfileIds = new List<string>();
        private readonly List<string> _duplicateCollectionIds = new List<string>();
        private readonly List<string> _duplicateEndpointIds = new List<string>();

        /// <summary>The catalog this index was built from.</summary>
        public NetworkCatalog Catalog { get; }

        /// <summary>Environments by ID. First occurrence wins on a duplicate.</summary>
        public IReadOnlyDictionary<string, NetworkEnvironmentProfile> Environments => _environments;

        /// <summary>Services by ID. First occurrence wins on a duplicate.</summary>
        public IReadOnlyDictionary<string, NetworkServiceDefinition> Services => _services;

        /// <summary>Policy profiles by ID.</summary>
        public IReadOnlyDictionary<string, NetworkPolicyProfile> PolicyProfiles => _policyProfiles;

        /// <summary>Credential profiles by ID.</summary>
        public IReadOnlyDictionary<string, NetworkCredentialProfile> CredentialProfiles => _credentialProfiles;

        /// <summary>Endpoint collections by collection ID.</summary>
        public IReadOnlyDictionary<string, NetworkEndpointCollection> Collections => _collections;

        /// <summary>Every endpoint in the catalog, by endpoint ID. Endpoint IDs are catalog-unique.</summary>
        public IReadOnlyDictionary<string, NetworkEndpointEntry> Endpoints => _endpoints;

        /// <summary>Environment IDs that appear more than once.</summary>
        public IReadOnlyList<string> DuplicateEnvironmentIds => _duplicateEnvironmentIds;

        /// <summary>Service IDs that appear more than once.</summary>
        public IReadOnlyList<string> DuplicateServiceIds => _duplicateServiceIds;

        /// <summary>Policy profile IDs that appear more than once.</summary>
        public IReadOnlyList<string> DuplicatePolicyProfileIds => _duplicatePolicyProfileIds;

        /// <summary>Credential profile IDs that appear more than once.</summary>
        public IReadOnlyList<string> DuplicateCredentialProfileIds => _duplicateCredentialProfileIds;

        /// <summary>Collection IDs that appear more than once.</summary>
        public IReadOnlyList<string> DuplicateCollectionIds => _duplicateCollectionIds;

        /// <summary>Endpoint IDs that appear more than once across all collections.</summary>
        public IReadOnlyList<string> DuplicateEndpointIds => _duplicateEndpointIds;

        /// <summary>Whether any duplicate identifier was found while indexing.</summary>
        public bool HasDuplicates =>
            _duplicateEnvironmentIds.Count > 0 ||
            _duplicateServiceIds.Count > 0 ||
            _duplicatePolicyProfileIds.Count > 0 ||
            _duplicateCredentialProfileIds.Count > 0 ||
            _duplicateCollectionIds.Count > 0 ||
            _duplicateEndpointIds.Count > 0;

        /// <summary>
        /// Builds an index over <paramref name="catalog"/>.
        /// </summary>
        /// <param name="catalog">The catalog to index. <c>null</c> yields an empty index.</param>
        public NetworkCatalogIndex(NetworkCatalog catalog)
        {
            Catalog = catalog;

            _environments = new Dictionary<string, NetworkEnvironmentProfile>(StringComparer.Ordinal);
            _services = new Dictionary<string, NetworkServiceDefinition>(StringComparer.Ordinal);
            _policyProfiles = new Dictionary<string, NetworkPolicyProfile>(StringComparer.Ordinal);
            _credentialProfiles = new Dictionary<string, NetworkCredentialProfile>(StringComparer.Ordinal);
            _collections = new Dictionary<string, NetworkEndpointCollection>(StringComparer.Ordinal);
            _endpoints = new Dictionary<string, NetworkEndpointEntry>(StringComparer.Ordinal);

            if (catalog == null)
                return;

            IndexById(catalog.Environments, e => e?.Id, _environments, _duplicateEnvironmentIds);
            IndexById(catalog.Services, s => s?.Id, _services, _duplicateServiceIds);
            IndexById(catalog.PolicyProfiles, p => p?.Id, _policyProfiles, _duplicatePolicyProfileIds);
            IndexById(catalog.CredentialProfiles, c => c?.Id, _credentialProfiles, _duplicateCredentialProfileIds);
            IndexById(catalog.EndpointCollections, c => c?.CollectionId, _collections, _duplicateCollectionIds);

            IndexEndpoints(catalog);
        }

        private static void IndexById<T>(
            IReadOnlyList<T> source,
            Func<T, string> readId,
            Dictionary<string, T> target,
            List<string> duplicates)
        {
            if (source == null) return;

            for (int i = 0; i < source.Count; i++)
            {
                T item = source[i];
                string id = readId(item);

                // A blank ID is its own finding, reported by the validator against the entity's
                // position. Indexing it under "" would only mask the entries that follow.
                if (string.IsNullOrEmpty(id))
                    continue;

                if (target.ContainsKey(id))
                {
                    if (!duplicates.Contains(id))
                        duplicates.Add(id);
                    continue;
                }
                target.Add(id, item);
            }
        }

        private void IndexEndpoints(NetworkCatalog catalog)
        {
            var collections = catalog.EndpointCollections;
            if (collections == null) return;

            for (int c = 0; c < collections.Count; c++)
            {
                var collection = collections[c];
                if (collection == null) continue;

                var endpoints = collection.Endpoints;
                if (endpoints == null) continue;

                for (int e = 0; e < endpoints.Count; e++)
                {
                    var endpoint = endpoints[e];
                    if (endpoint == null || string.IsNullOrEmpty(endpoint.Id))
                        continue;

                    if (_endpoints.ContainsKey(endpoint.Id))
                    {
                        if (!_duplicateEndpointIds.Contains(endpoint.Id))
                            _duplicateEndpointIds.Add(endpoint.Id);
                        continue;
                    }

                    _endpoints.Add(
                        endpoint.Id,
                        new NetworkEndpointEntry(collection, endpoint, collection.ResolveServiceId(endpoint)));
                }
            }
        }

        /// <summary>
        /// Looks up an endpoint by ID.
        /// </summary>
        /// <param name="endpointId">The endpoint ID.</param>
        /// <param name="entry">The endpoint entry on success.</param>
        /// <returns><c>true</c> when the endpoint exists.</returns>
        public bool TryGetEndpoint(string endpointId, out NetworkEndpointEntry entry)
        {
            if (string.IsNullOrEmpty(endpointId))
            {
                entry = default;
                return false;
            }
            return _endpoints.TryGetValue(endpointId, out entry);
        }

        /// <summary>
        /// Every route the catalog could resolve — the full environment × service matrix, including
        /// pairs with no binding.
        /// </summary>
        /// <returns>One key per environment/service pair, in authored order.</returns>
        /// <remarks>
        /// Includes unbound pairs on purpose: the Hub's binding grid and the validator both need to
        /// show the holes, not just the filled cells.
        /// </remarks>
        public List<NetworkRouteKey> EnumerateRouteMatrix()
        {
            var routes = new List<NetworkRouteKey>(_environments.Count * _services.Count);

            foreach (var environment in _environments.Values)
            {
                foreach (var service in _services.Values)
                    routes.Add(new NetworkRouteKey(environment.Id, service.Id));
            }
            return routes;
        }

        /// <summary>
        /// Services that reference a policy profile at any layer.
        /// </summary>
        /// <param name="policyProfileId">The profile ID to search for.</param>
        /// <returns>Matching service IDs, possibly empty.</returns>
        public List<string> FindServicesUsingPolicy(string policyProfileId)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(policyProfileId)) return result;

            foreach (var service in _services.Values)
            {
                if (string.Equals(service.PolicyProfileId, policyProfileId, StringComparison.Ordinal))
                    result.Add(service.Id);
            }
            return result;
        }

        /// <summary>
        /// Services that reference a credential profile.
        /// </summary>
        /// <param name="credentialProfileId">The profile ID to search for.</param>
        /// <returns>Matching service IDs, possibly empty.</returns>
        public List<string> FindServicesUsingCredential(string credentialProfileId)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(credentialProfileId)) return result;

            foreach (var service in _services.Values)
            {
                if (string.Equals(service.CredentialProfileId, credentialProfileId, StringComparison.Ordinal))
                    result.Add(service.Id);
            }
            return result;
        }

        /// <summary>
        /// Endpoints assigned to a service.
        /// </summary>
        /// <param name="serviceId">The service ID to search for.</param>
        /// <returns>Matching endpoint entries, possibly empty.</returns>
        public List<NetworkEndpointEntry> FindEndpointsForService(string serviceId)
        {
            var result = new List<NetworkEndpointEntry>();
            if (string.IsNullOrEmpty(serviceId)) return result;

            foreach (var entry in _endpoints.Values)
            {
                if (string.Equals(entry.ServiceId, serviceId, StringComparison.Ordinal))
                    result.Add(entry);
            }
            return result;
        }
    }
}
