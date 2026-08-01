namespace Molca.Networking.Configuration
{
    /// <summary>
    /// The precedence layers that contribute to an effective configuration, ordered from lowest to
    /// highest (plan §5.6).
    /// </summary>
    /// <remarks>
    /// The numeric order is load-bearing: <see cref="NetworkPolicyResolver"/> compares layers to pick
    /// a winner, and the Hub's effective-policy inspector renders them left to right in this order.
    /// </remarks>
    public enum NetworkConfigurationLayer
    {
        /// <summary>Built-in conservative defaults from <see cref="NetworkPolicyProfile.CreateLibraryDefault"/>.</summary>
        LibraryDefault = 0,

        /// <summary>The catalog's <see cref="NetworkCatalog.DefaultPolicyProfileId"/>.</summary>
        CatalogDefault = 1,

        /// <summary>The environment's <see cref="NetworkEnvironmentProfile.PolicyProfileId"/>.</summary>
        Environment = 2,

        /// <summary>The service's <see cref="NetworkServiceDefinition.PolicyProfileId"/>.</summary>
        Service = 3,

        /// <summary>The endpoint's <see cref="NetworkEndpointDefinition.PolicyProfileId"/>.</summary>
        Endpoint = 4,

        /// <summary>An explicit per-send override supplied by the caller.</summary>
        SendOverride = 5
    }

    /// <summary>
    /// One resolved configuration value together with the layer that supplied it.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <remarks>
    /// Carrying provenance alongside the value is what lets the Hub answer "why is this timeout 12
    /// seconds?" without re-running resolution, and what lets a security finding name the layer that
    /// tried to weaken a rule.
    /// </remarks>
    public readonly struct NetworkEffectiveValue<T>
    {
        /// <summary>The resolved value.</summary>
        public readonly T Value;

        /// <summary>The layer that supplied <see cref="Value"/>.</summary>
        public readonly NetworkConfigurationLayer Source;

        /// <summary>Creates a resolved value.</summary>
        /// <param name="value">The resolved value.</param>
        /// <param name="source">The layer it came from.</param>
        public NetworkEffectiveValue(T value, NetworkConfigurationLayer source)
        {
            Value = value;
            Source = source;
        }

        /// <summary>Renders the value and its provenance, e.g. <c>30 (Service)</c>.</summary>
        public override string ToString() => $"{Value} ({Source})";

        /// <summary>Reads the value, discarding provenance.</summary>
        /// <param name="effective">The resolved value to unwrap.</param>
        public static implicit operator T(NetworkEffectiveValue<T> effective) => effective.Value;
    }
}
