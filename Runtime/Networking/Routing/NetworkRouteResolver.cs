using System;
using Molca.Networking.Configuration;

namespace Molca.Networking.Routing
{
    /// <summary>
    /// What a caller wants resolved beyond the route itself: which protocol, which endpoint template,
    /// an ad-hoc relative path, and any per-send policy override.
    /// </summary>
    public readonly struct NetworkRouteQuery
    {
        /// <summary>The protocol whose origin to resolve.</summary>
        public readonly NetworkProtocols Protocol;

        /// <summary>An endpoint template ID, or <c>null</c> for a raw send.</summary>
        public readonly string EndpointId;

        /// <summary>
        /// A relative path to use instead of the endpoint's own, or <c>null</c>. Required when
        /// <see cref="EndpointId"/> is <c>null</c> and the caller wants more than the bare origin.
        /// </summary>
        public readonly string RelativePath;

        /// <summary>A per-send policy override, or <c>null</c>.</summary>
        public readonly NetworkSendPolicyOverride PolicyOverride;

        /// <summary>Creates a query.</summary>
        /// <param name="protocol">The protocol whose origin to resolve.</param>
        /// <param name="endpointId">An endpoint template ID, or <c>null</c>.</param>
        /// <param name="relativePath">A relative path override, or <c>null</c>.</param>
        /// <param name="policyOverride">A per-send policy override, or <c>null</c>.</param>
        public NetworkRouteQuery(
            NetworkProtocols protocol = NetworkProtocols.Http,
            string endpointId = null,
            string relativePath = null,
            NetworkSendPolicyOverride policyOverride = null)
        {
            Protocol = protocol;
            EndpointId = endpointId;
            RelativePath = relativePath;
            PolicyOverride = policyOverride;
        }

        /// <summary>A query for an ad-hoc HTTP path with no endpoint template.</summary>
        /// <param name="relativePath">The path relative to the service origin.</param>
        /// <param name="policyOverride">A per-send policy override, or <c>null</c>.</param>
        public static NetworkRouteQuery ForPath(string relativePath, NetworkSendPolicyOverride policyOverride = null) =>
            new NetworkRouteQuery(NetworkProtocols.Http, null, relativePath, policyOverride);

        /// <summary>A query for an authored endpoint template.</summary>
        /// <param name="endpointId">The endpoint ID.</param>
        /// <param name="policyOverride">A per-send policy override, or <c>null</c>.</param>
        public static NetworkRouteQuery ForEndpoint(string endpointId, NetworkSendPolicyOverride policyOverride = null) =>
            new NetworkRouteQuery(NetworkProtocols.Http, endpointId, null, policyOverride);
    }

    /// <summary>Resolves a route to a concrete origin, URI, policy, and credential scope.</summary>
    public interface INetworkRouteResolver
    {
        /// <summary>The snapshot this resolver reads. Never changes for the lifetime of the resolver.</summary>
        NetworkCatalogSnapshot Snapshot { get; }

        /// <summary>
        /// Resolves a route.
        /// </summary>
        /// <param name="route">The environment/service pair to resolve.</param>
        /// <param name="query">What else to resolve — protocol, endpoint, path, policy override.</param>
        /// <returns>The resolution. Never <c>null</c>; check <see cref="NetworkRouteResolution.Resolves"/>.</returns>
        NetworkRouteResolution Resolve(NetworkRouteKey route, NetworkRouteQuery query = default);

        /// <summary>
        /// Resolves a route in the snapshot's default environment.
        /// </summary>
        /// <param name="serviceId">The service to reach.</param>
        /// <param name="query">What else to resolve.</param>
        /// <returns>The resolution; a failed one when no default environment is configured.</returns>
        NetworkRouteResolution ResolveDefault(string serviceId, NetworkRouteQuery query = default);
    }

    /// <summary>
    /// The single route resolver. Pure: a function of the snapshot plus the route key, with no Unity
    /// API, no I/O, and no mutable state.
    /// </summary>
    /// <remarks>
    /// Used by the routed pipeline at runtime <em>and</em> by the Hub's effective-configuration preview
    /// in the editor, deliberately — a second resolver is how the preview and the request drift apart
    /// (plan §14).
    /// <para>
    /// Never falls back between environments. A missing or disabled binding is a typed
    /// <see cref="NetworkErrorCategory.RouteResolution"/> failure, because substituting another
    /// environment's origin is how a staging build ends up talking to production.
    /// </para>
    /// </remarks>
    public sealed class NetworkRouteResolver : INetworkRouteResolver
    {
        /// <inheritdoc />
        public NetworkCatalogSnapshot Snapshot { get; }

        /// <summary>Creates a resolver over one snapshot.</summary>
        /// <param name="snapshot">The snapshot to read; <c>null</c> is treated as <see cref="NetworkCatalogSnapshot.Empty"/>.</param>
        public NetworkRouteResolver(NetworkCatalogSnapshot snapshot)
        {
            Snapshot = snapshot ?? NetworkCatalogSnapshot.Empty;
        }

        /// <summary>Creates a resolver by capturing a snapshot of <paramref name="catalog"/>.</summary>
        /// <param name="catalog">The catalog to snapshot.</param>
        public NetworkRouteResolver(NetworkCatalog catalog)
            : this(NetworkCatalogSnapshot.Capture(catalog))
        {
        }

        /// <inheritdoc />
        public NetworkRouteResolution ResolveDefault(string serviceId, NetworkRouteQuery query = default)
        {
            if (string.IsNullOrEmpty(Snapshot.DefaultEnvironmentId))
            {
                return Fail(
                    new NetworkRouteResolution
                    {
                        Protocol = NormalizeProtocol(query.Protocol),
                        Policy = NetworkPolicyResolver.Resolve(Snapshot.Catalog, null, null, null, query.PolicyOverride)
                    },
                    NetworkErrorCategory.Configuration,
                    Snapshot.HasCatalog
                        ? "The network catalog names no default environment."
                        : "No network catalog is configured for this project.");
            }

            return Resolve(new NetworkRouteKey(Snapshot.DefaultEnvironmentId, serviceId), query);
        }

        /// <inheritdoc />
        public NetworkRouteResolution Resolve(NetworkRouteKey route, NetworkRouteQuery query = default)
        {
            var protocol = NormalizeProtocol(query.Protocol);
            var result = new NetworkRouteResolution { Route = route, Protocol = protocol };

            Snapshot.Environments.TryGetValue(route.EnvironmentId, out var environment);
            Snapshot.Services.TryGetValue(route.ServiceId, out var service);

            NetworkEndpointDefinition endpoint = null;
            if (!string.IsNullOrEmpty(query.EndpointId))
            {
                if (!Snapshot.Index.TryGetEndpoint(query.EndpointId, out var entry))
                {
                    result.Environment = environment;
                    result.Service = service;
                    result.Policy = NetworkPolicyResolver.Resolve(
                        Snapshot.Catalog, environment, service, null, query.PolicyOverride);

                    return Fail(result, NetworkErrorCategory.Configuration,
                        $"No endpoint '{query.EndpointId}' exists in the catalog.");
                }
                endpoint = entry.Endpoint;
            }

            result.Environment = environment;
            result.Service = service;
            result.Endpoint = endpoint;

            // Policy resolves even when the route does not — an unbound route still has an inherited
            // policy, and the Hub renders it for empty binding-grid cells.
            result.Policy = NetworkPolicyResolver.Resolve(
                Snapshot.Catalog, environment, service, endpoint, query.PolicyOverride);

            if (!Snapshot.HasCatalog)
                return Fail(result, NetworkErrorCategory.Configuration,
                    "No network catalog is configured for this project.");

            if (environment == null)
                return Fail(result, NetworkErrorCategory.RouteResolution,
                    $"Environment '{route.EnvironmentId}' does not exist in the catalog.");

            if (service == null)
                return Fail(result, NetworkErrorCategory.RouteResolution,
                    $"Service '{route.ServiceId}' does not exist in the catalog.");

            result.Credential = Snapshot.Catalog.FindCredentialProfile(service.CredentialProfileId);
            result.AllowedHosts = service.ResolveAllowedHosts();

            if (!service.Supports(protocol))
                return Fail(result, NetworkErrorCategory.Configuration,
                    $"Service '{service.Id}' does not declare {protocol}.");

            if (endpoint != null && !string.Equals(
                    Snapshot.Index.Endpoints[endpoint.Id].ServiceId, service.Id, StringComparison.Ordinal))
            {
                return Fail(result, NetworkErrorCategory.Configuration,
                    $"Endpoint '{endpoint.Id}' belongs to service " +
                    $"'{Snapshot.Index.Endpoints[endpoint.Id].ServiceId}', not '{service.Id}'.");
            }

            var binding = service.FindBinding(environment.Id);
            if (binding == null)
                return Fail(result, NetworkErrorCategory.RouteResolution,
                    $"Service '{service.Id}' has no binding for environment '{environment.Id}'.");

            result.Binding = binding;

            if (!binding.Enabled)
                return Fail(result, NetworkErrorCategory.RouteResolution,
                    $"Service '{service.Id}' is disabled in environment '{environment.Id}'.");

            bool wsFamily = protocol == NetworkProtocols.WebSocket;
            if (!NetworkOrigin.TryNormalize(binding.OriginFor(protocol), wsFamily, out string origin, out string originError))
                return Fail(result, NetworkErrorCategory.Configuration,
                    $"{protocol} origin for '{environment.Id}' is unusable: {originError}");

            result.Origin = origin;

            string relativePath = query.RelativePath ?? endpoint?.RelativePath ?? string.Empty;
            if (!NetworkOrigin.TryJoin(origin, relativePath, out string uri, out string joinError))
                return Fail(result, NetworkErrorCategory.Configuration, joinError);

            result.ResolvedUri = uri;
            result.Host = NetworkHostRule.HostOf(uri) ?? string.Empty;

            if (result.Policy.RequireSecureTransport.Value && !NetworkOrigin.IsSecureScheme(new Uri(origin).Scheme))
            {
                return Fail(result, NetworkErrorCategory.SecurityPolicy,
                    $"Environment '{environment.Id}' requires an encrypted scheme, but the bound " +
                    $"{protocol} origin is not encrypted.");
            }

            // The service's own allowlist gates its own bound origin too. A binding that drifted away
            // from the authored hosts is a security finding, not a working route.
            if (service.AllowedHostPatterns != null && service.AllowedHostPatterns.Count > 0 &&
                !NetworkHostRule.MatchesAny(result.AllowedHosts, result.Host))
            {
                return Fail(result, NetworkErrorCategory.SecurityPolicy,
                    $"Host '{result.Host}' is not covered by service '{service.Id}' allowed hosts.");
            }

            result.CredentialAppliesToHost = CredentialApplies(result.Credential, service.Id, result.Host);
            result.Resolves = true;
            return result;
        }

        /// <summary>
        /// Whether a credential profile is authorized for both a service and a concrete host.
        /// </summary>
        /// <param name="credential">The profile, or <c>null</c>.</param>
        /// <param name="serviceId">The service attempting to use it.</param>
        /// <param name="host">The final host, after any redirect.</param>
        /// <returns><c>false</c> for a null, anonymous, or out-of-scope credential.</returns>
        /// <remarks>
        /// Call this against the host a request is actually about to reach. Re-checking after a
        /// redirect is what stops a credential following a 302 off-domain (plan §6.6).
        /// </remarks>
        public static bool CredentialApplies(NetworkCredentialProfile credential, string serviceId, string host) =>
            credential != null &&
            !credential.IsAnonymous &&
            credential.AllowsService(serviceId) &&
            credential.AllowsHost(host);

        /// <summary>Defaults an unspecified protocol to HTTP, since <c>default(NetworkRouteQuery)</c> yields None.</summary>
        private static NetworkProtocols NormalizeProtocol(NetworkProtocols protocol) =>
            protocol == NetworkProtocols.None ? NetworkProtocols.Http : protocol;

        private static NetworkRouteResolution Fail(
            NetworkRouteResolution result,
            NetworkErrorCategory category,
            string message)
        {
            result.Resolves = false;
            result.FailureCategory = category;
            result.FailureMessage = message;
            return result;
        }
    }
}
