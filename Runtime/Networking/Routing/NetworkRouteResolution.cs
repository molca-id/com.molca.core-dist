using Molca.Networking.Configuration;

namespace Molca.Networking.Routing
{
    /// <summary>
    /// What a route resolved to — or why it did not. Immutable.
    /// </summary>
    /// <remarks>
    /// The single answer used by both the runtime pipeline and the Hub's authoring preview, so what an
    /// author sees and what a request does cannot drift.
    /// <para>
    /// <see cref="Policy"/> is populated even on failure: the effective policy for an unbound route is
    /// still meaningful, and the Hub renders it for empty binding-grid cells.
    /// </para>
    /// </remarks>
    public sealed class NetworkRouteResolution
    {
        /// <summary>The route that was resolved.</summary>
        public NetworkRouteKey Route { get; internal set; }

        /// <summary>Whether the route produced a usable absolute URI.</summary>
        public bool Resolves { get; internal set; }

        /// <summary>Why resolution failed, or empty on success.</summary>
        public string FailureMessage { get; internal set; } = string.Empty;

        /// <summary>The category of a resolution failure, or <see cref="NetworkErrorCategory.None"/>.</summary>
        public NetworkErrorCategory FailureCategory { get; internal set; } = NetworkErrorCategory.None;

        /// <summary>The protocol that was requested.</summary>
        public NetworkProtocols Protocol { get; internal set; }

        /// <summary>The resolved environment, or <c>null</c> when it does not exist.</summary>
        public NetworkEnvironmentProfile Environment { get; internal set; }

        /// <summary>The resolved service, or <c>null</c> when it does not exist.</summary>
        public NetworkServiceDefinition Service { get; internal set; }

        /// <summary>The binding used, or <c>null</c> when none applied.</summary>
        public NetworkServiceBinding Binding { get; internal set; }

        /// <summary>The endpoint template applied, or <c>null</c> for a raw relative-path send.</summary>
        public NetworkEndpointDefinition Endpoint { get; internal set; }

        /// <summary>The normalized origin, or empty when unresolved.</summary>
        public string Origin { get; internal set; } = string.Empty;

        /// <summary>The final absolute URI including the relative path, or empty when unresolved.</summary>
        public string ResolvedUri { get; internal set; } = string.Empty;

        /// <summary>The host of <see cref="ResolvedUri"/>, or empty when unresolved.</summary>
        public string Host { get; internal set; } = string.Empty;

        /// <summary>The effective policy with per-field provenance. Never <c>null</c>.</summary>
        public NetworkEffectivePolicy Policy { get; internal set; }

        /// <summary>The credential profile the service names, or <c>null</c> for anonymous.</summary>
        public NetworkCredentialProfile Credential { get; internal set; }

        /// <summary>
        /// Whether <see cref="Credential"/> is authorized for both this service and
        /// <see cref="Host"/>. <c>false</c> means the request goes out anonymous even though a
        /// credential is configured.
        /// </summary>
        public bool CredentialAppliesToHost { get; internal set; }

        /// <summary>Whether the target environment enforces production safety rules.</summary>
        public bool IsProduction => Environment != null && Environment.IsProductionSafetyEnforced;

        /// <summary>
        /// The service's effective allowed-host patterns, used to revalidate redirect targets.
        /// Never <c>null</c>.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> AllowedHosts { get; internal set; } =
            System.Array.Empty<string>();

        /// <summary>Renders the resolution for logs and findings.</summary>
        public override string ToString() =>
            Resolves ? $"{Route} → {ResolvedUri}" : $"{Route} unresolved ({FailureCategory}): {FailureMessage}";
    }
}
