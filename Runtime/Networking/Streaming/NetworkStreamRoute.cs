using System;
using Molca.Networking.Configuration;
using Molca.Networking.Routing;
using UnityEngine;

namespace Molca.Networking.Streaming
{
    /// <summary>How a streaming route picks the environment it connects under.</summary>
    public enum NetworkEnvironmentStrategy
    {
        /// <summary>Use the catalog's default environment, resolved when the session starts.</summary>
        CatalogDefault = 0,

        /// <summary>Use one named environment, whatever the catalog default is.</summary>
        Explicit,
    }

    /// <summary>
    /// Where a streaming session connects, expressed the same way an HTTP call site expresses it: a
    /// service, an environment strategy, and a relative path.
    /// </summary>
    /// <remarks>
    /// A route carries no origin, no host, and no scheme — those come from the service's binding for
    /// whichever environment the strategy selects, exactly as they do for HTTP (plan §6.7). That is what
    /// makes a provider asset stop being the place a URL is typed and start being a reference to a route
    /// the catalog owns.
    /// <para>
    /// Serializable so a provider asset can hold one, and immutable in use: the fields are written by the
    /// Inspector and read by the session, never the reverse.
    /// </para>
    /// </remarks>
    [Serializable]
    public struct NetworkStreamRoute : IEquatable<NetworkStreamRoute>
    {
        [Tooltip("The catalog service this stream connects to.")]
        [SerializeField] private string _serviceId;

        [Tooltip("Which environment to connect under.")]
        [SerializeField] private NetworkEnvironmentStrategy _environmentStrategy;

        [Tooltip("The environment ID, when the strategy is Explicit.")]
        [SerializeField] private string _environmentId;

        [Tooltip("Path relative to the service's bound origin for this protocol.")]
        [SerializeField] private string _relativePath;

        [Tooltip("An authored endpoint template ID, or empty to use the relative path alone.")]
        [SerializeField] private string _endpointId;

        /// <summary>The catalog service to connect to. Empty means this route is not configured.</summary>
        public string ServiceId => _serviceId ?? string.Empty;

        /// <summary>How the environment is chosen.</summary>
        public NetworkEnvironmentStrategy EnvironmentStrategy => _environmentStrategy;

        /// <summary>The explicit environment ID, or empty.</summary>
        public string EnvironmentId => _environmentId ?? string.Empty;

        /// <summary>The path relative to the resolved origin.</summary>
        public string RelativePath => _relativePath ?? string.Empty;

        /// <summary>The endpoint template ID, or empty.</summary>
        public string EndpointId => _endpointId ?? string.Empty;

        /// <summary>Whether this route names a service and can therefore be resolved.</summary>
        public bool IsConfigured => !string.IsNullOrEmpty(_serviceId);

        /// <summary>Creates a route.</summary>
        /// <param name="serviceId">The service to connect to.</param>
        /// <param name="relativePath">The path relative to the resolved origin.</param>
        /// <param name="strategy">How to choose the environment.</param>
        /// <param name="environmentId">The environment ID when <paramref name="strategy"/> is explicit.</param>
        /// <param name="endpointId">An endpoint template ID, or <c>null</c>.</param>
        public static NetworkStreamRoute Create(
            string serviceId,
            string relativePath = null,
            NetworkEnvironmentStrategy strategy = NetworkEnvironmentStrategy.CatalogDefault,
            string environmentId = null,
            string endpointId = null)
        {
            return new NetworkStreamRoute
            {
                _serviceId = serviceId,
                _relativePath = relativePath,
                _environmentStrategy = strategy,
                _environmentId = environmentId,
                _endpointId = endpointId,
            };
        }

        /// <summary>
        /// The concrete route key this resolves to under a snapshot.
        /// </summary>
        /// <param name="snapshot">The snapshot supplying the catalog default.</param>
        /// <param name="key">The route key on success.</param>
        /// <param name="failure">Why no key could be formed, or <c>null</c>.</param>
        /// <returns><c>false</c> when the strategy names no environment this snapshot can supply.</returns>
        /// <remarks>
        /// A <c>Try</c> rather than a throw, because "the catalog names no default environment yet" is a
        /// normal state during setup and must reach the author as a configuration failure the session can
        /// display — not an exception out of a connection loop.
        /// <para>
        /// Formed per connection attempt rather than cached, so a session started before the catalog named
        /// a default picks one up on its next attempt instead of staying broken.
        /// </para>
        /// </remarks>
        public bool TryToRouteKey(NetworkCatalogSnapshot snapshot, out NetworkRouteKey key, out string failure)
        {
            key = default;

            if (!IsConfigured)
            {
                failure = "This stream names no catalog service, so there is no route to resolve.";
                return false;
            }

            string environmentId = _environmentStrategy == NetworkEnvironmentStrategy.Explicit
                ? EnvironmentId
                : snapshot?.DefaultEnvironmentId ?? string.Empty;

            if (string.IsNullOrEmpty(environmentId))
            {
                failure = _environmentStrategy == NetworkEnvironmentStrategy.Explicit
                    ? $"This stream targets service '{ServiceId}' but names no environment."
                    : $"This stream targets service '{ServiceId}' under the catalog default environment, " +
                      "and the catalog does not name a default environment.";
                return false;
            }

            key = new NetworkRouteKey(environmentId, ServiceId);
            failure = null;
            return true;
        }

        /// <summary>The routed query for this route at a protocol.</summary>
        /// <param name="protocol">The protocol whose origin to resolve.</param>
        public NetworkRouteQuery ToQuery(NetworkProtocols protocol) =>
            new NetworkRouteQuery(protocol, string.IsNullOrEmpty(EndpointId) ? null : EndpointId, RelativePath);

        /// <inheritdoc />
        public bool Equals(NetworkStreamRoute other) =>
            string.Equals(ServiceId, other.ServiceId, StringComparison.Ordinal) &&
            _environmentStrategy == other._environmentStrategy &&
            string.Equals(EnvironmentId, other.EnvironmentId, StringComparison.Ordinal) &&
            string.Equals(RelativePath, other.RelativePath, StringComparison.Ordinal) &&
            string.Equals(EndpointId, other.EndpointId, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is NetworkStreamRoute other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = ServiceId.GetHashCode();
                hash = (hash * 397) ^ (int)_environmentStrategy;
                hash = (hash * 397) ^ EnvironmentId.GetHashCode();
                hash = (hash * 397) ^ RelativePath.GetHashCode();
                return (hash * 397) ^ EndpointId.GetHashCode();
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            IsConfigured
                ? $"{(_environmentStrategy == NetworkEnvironmentStrategy.Explicit ? EnvironmentId : "<default>")}/{ServiceId}" +
                  (string.IsNullOrEmpty(RelativePath) ? string.Empty : "/" + RelativePath)
                : "<unrouted>";
    }
}
