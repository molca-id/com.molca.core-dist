using System;

namespace Molca.Networking.Routing
{
    /// <summary>
    /// The unit of request targeting: an environment paired with a service. Replaces the
    /// process-wide base URL as the thing a call site selects.
    /// </summary>
    /// <remarks>
    /// Immutable and comparable, so it can key per-service pipeline state (queues, circuit
    /// breakers, caches) without allocating. Both halves are stable IDs validated by
    /// <c>NetworkIds</c>; neither may be empty.
    /// </remarks>
    [Serializable]
    public readonly struct NetworkRouteKey : IEquatable<NetworkRouteKey>
    {
        /// <summary>The environment half of the route, e.g. <c>staging-eu</c>.</summary>
        public readonly string EnvironmentId;

        /// <summary>The service half of the route, e.g. <c>identity</c>.</summary>
        public readonly string ServiceId;

        /// <summary>Creates a route key.</summary>
        /// <param name="environmentId">Environment ID; must be non-empty.</param>
        /// <param name="serviceId">Service ID; must be non-empty.</param>
        /// <exception cref="ArgumentException">Either identifier is <c>null</c> or empty.</exception>
        public NetworkRouteKey(string environmentId, string serviceId)
        {
            if (string.IsNullOrEmpty(environmentId))
                throw new ArgumentException("A route needs an explicit environment ID.", nameof(environmentId));
            if (string.IsNullOrEmpty(serviceId))
                throw new ArgumentException("A route needs an explicit service ID.", nameof(serviceId));

            EnvironmentId = environmentId;
            ServiceId = serviceId;
        }

        /// <summary>Whether this key was default-constructed and carries no identifiers.</summary>
        public bool IsEmpty => string.IsNullOrEmpty(EnvironmentId) || string.IsNullOrEmpty(ServiceId);

        /// <inheritdoc />
        public bool Equals(NetworkRouteKey other) =>
            string.Equals(EnvironmentId, other.EnvironmentId, StringComparison.Ordinal) &&
            string.Equals(ServiceId, other.ServiceId, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is NetworkRouteKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = EnvironmentId == null ? 0 : StringComparer.Ordinal.GetHashCode(EnvironmentId);
                hash = (hash * 397) ^ (ServiceId == null ? 0 : StringComparer.Ordinal.GetHashCode(ServiceId));
                return hash;
            }
        }

        /// <summary>Renders the key as <c>(environment, service)</c> for logs and findings.</summary>
        public override string ToString() => $"({EnvironmentId}, {ServiceId})";

        /// <summary>Value equality.</summary>
        public static bool operator ==(NetworkRouteKey left, NetworkRouteKey right) => left.Equals(right);

        /// <summary>Value inequality.</summary>
        public static bool operator !=(NetworkRouteKey left, NetworkRouteKey right) => !left.Equals(right);
    }
}
