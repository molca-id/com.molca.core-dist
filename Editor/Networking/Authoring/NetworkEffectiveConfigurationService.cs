using System;
using Molca.Networking.Configuration;
using Molca.Networking.Routing;

namespace Molca.Editor.Networking.Authoring
{
    /// <summary>
    /// What one route resolves to, projected for authoring preview: the origin, the effective policy
    /// with provenance, the credential profile name, and any reason it cannot resolve.
    /// </summary>
    /// <remarks>
    /// A thin projection of <see cref="NetworkRouteResolution"/>, not a second resolution. It exists so
    /// Hub views bind to a stable shape without depending on runtime routing types directly.
    /// <para>
    /// Deliberately carries the credential profile <em>name</em> and never a value. Nothing on this type
    /// is safe to treat as a secret because nothing on it is one.
    /// </para>
    /// </remarks>
    public sealed class NetworkEffectiveRoute
    {
        /// <summary>The underlying resolution. Never <c>null</c>.</summary>
        public NetworkRouteResolution Resolution { get; }

        /// <summary>Creates a projection over a resolution.</summary>
        /// <param name="resolution">The resolution to project.</param>
        /// <exception cref="ArgumentNullException"><paramref name="resolution"/> is <c>null</c>.</exception>
        internal NetworkEffectiveRoute(NetworkRouteResolution resolution)
        {
            Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
        }

        /// <summary>The route this preview describes.</summary>
        public NetworkRouteKey Route => Resolution.Route;

        /// <summary>Whether the route resolves to a usable origin.</summary>
        public bool Resolves => Resolution.Resolves;

        /// <summary>Why the route does not resolve, or empty when it does.</summary>
        public string FailureReason => Resolution.FailureMessage;

        /// <summary>The category of a resolution failure, or <see cref="NetworkErrorCategory.None"/>.</summary>
        public NetworkErrorCategory FailureCategory => Resolution.FailureCategory;

        /// <summary>The normalized origin for the requested protocol, or empty when unresolved.</summary>
        public string Origin => Resolution.Origin;

        /// <summary>The full URI including the endpoint's relative path, or empty when unresolved.</summary>
        public string ResolvedUri => Resolution.ResolvedUri;

        /// <summary>The effective policy, with the layer that supplied each field. Never <c>null</c>.</summary>
        public NetworkEffectivePolicy Policy => Resolution.Policy;

        /// <summary>The credential profile ID that would be used, or empty for anonymous.</summary>
        public string CredentialProfileId => Resolution.Credential?.Id ?? string.Empty;

        /// <summary>
        /// Whether the resolved host is inside the credential's scope. <c>false</c> when the request
        /// would be sent anonymously despite a credential being configured.
        /// </summary>
        public bool CredentialAppliesToHost => Resolution.CredentialAppliesToHost;

        /// <summary>Whether the target environment enforces production safety rules.</summary>
        public bool IsProduction => Resolution.IsProduction;
    }

    /// <summary>
    /// Computes effective configuration previews for the Hub, MCP tools, and tests. Read-only:
    /// nothing here mutates a catalog.
    /// </summary>
    /// <remarks>
    /// Delegates to the runtime <see cref="NetworkRouteResolver"/> rather than reimplementing
    /// resolution, so an authoring preview and a live request cannot disagree about where a route goes
    /// or which policy applies.
    /// <para>
    /// Takes a fresh <see cref="NetworkCatalogSnapshot"/> at construction. Construct a new service after
    /// editing the catalog — the snapshot is deliberately stable for the instance's lifetime.
    /// </para>
    /// </remarks>
    public sealed class NetworkEffectiveConfigurationService
    {
        private readonly NetworkRouteResolver _resolver;

        /// <summary>The catalog being previewed.</summary>
        public NetworkCatalog Catalog { get; }

        /// <summary>The index built over <see cref="Catalog"/>.</summary>
        public NetworkCatalogIndex Index => _resolver.Snapshot.Index;

        /// <summary>Creates a service over one catalog.</summary>
        /// <param name="catalog">The catalog to preview.</param>
        /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <c>null</c>.</exception>
        public NetworkEffectiveConfigurationService(NetworkCatalog catalog)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _resolver = new NetworkRouteResolver(catalog);
        }

        /// <summary>
        /// Resolves a route for preview.
        /// </summary>
        /// <param name="route">The environment/service pair to resolve.</param>
        /// <param name="protocol">Which protocol's origin to resolve.</param>
        /// <param name="endpointId">
        /// An endpoint whose relative path and policy override to apply, or <c>null</c> for the bare
        /// origin.
        /// </param>
        /// <param name="sendOverride">A per-send override to fold in, or <c>null</c>.</param>
        /// <returns>The preview. Never <c>null</c>; check <see cref="NetworkEffectiveRoute.Resolves"/>.</returns>
        public NetworkEffectiveRoute Resolve(
            NetworkRouteKey route,
            NetworkProtocols protocol = NetworkProtocols.Http,
            string endpointId = null,
            NetworkSendPolicyOverride sendOverride = null)
        {
            var query = new NetworkRouteQuery(protocol, endpointId, null, sendOverride);
            return new NetworkEffectiveRoute(_resolver.Resolve(route, query));
        }

        /// <summary>
        /// Resolves a route using the catalog's default environment.
        /// </summary>
        /// <param name="serviceId">The service to resolve.</param>
        /// <param name="protocol">Which protocol's origin to resolve.</param>
        /// <returns>The preview, or a failed preview when the catalog has no usable default environment.</returns>
        public NetworkEffectiveRoute ResolveDefaultEnvironment(
            string serviceId,
            NetworkProtocols protocol = NetworkProtocols.Http)
        {
            var query = new NetworkRouteQuery(protocol);
            return new NetworkEffectiveRoute(_resolver.ResolveDefault(serviceId, query));
        }
    }
}
