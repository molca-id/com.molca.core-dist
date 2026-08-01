using Molca.Networking.Routing;

namespace Molca.Networking.Compatibility
{
    /// <summary>How a legacy <c>HttpRequest</c> relates to the project's network catalog.</summary>
    public enum LegacyRouteKind
    {
        /// <summary>
        /// The catalog says nothing about this request. It executes exactly as it did before the catalog
        /// existed, and process-wide credentials still apply.
        /// </summary>
        /// <remarks>
        /// The state a project with no catalog is always in, and the state a catalogued project is in for
        /// a relative URL when no legacy default service was migrated. Not a failure — the compatibility
        /// contract requires unconfigured projects to keep working unchanged.
        /// </remarks>
        Unrouted = 0,

        /// <summary>
        /// The request maps onto a catalog route, so the routed pipeline can execute it and the route's
        /// own policy and credential scope apply.
        /// </summary>
        Routed,

        /// <summary>
        /// A <c>useFullUrl</c> request to a host no catalog service claims. It still executes, but
        /// process-wide credentials are withheld unless the catalog opts back into the legacy behaviour.
        /// </summary>
        External
    }

    /// <summary>
    /// What the legacy client should do with one request: whether it maps to a route, and whether
    /// process-wide credentials may travel with it.
    /// </summary>
    /// <remarks>
    /// Immutable and cheap. Produced by <see cref="LegacyRouteMapper"/> per send, from an immutable
    /// catalog snapshot — nothing here reads live configuration.
    /// </remarks>
    public readonly struct LegacyRouteDecision
    {
        /// <summary>How the request relates to the catalog.</summary>
        public readonly LegacyRouteKind Kind;

        /// <summary>The route to send on. Meaningful only for <see cref="LegacyRouteKind.Routed"/>.</summary>
        public readonly NetworkRouteKey Route;

        /// <summary>
        /// The path relative to the resolved service origin, for <see cref="LegacyRouteKind.Routed"/>.
        /// Empty when the request targets the origin itself.
        /// </summary>
        public readonly string RelativePath;

        /// <summary>
        /// Whether process-wide credentials — <c>HttpModule</c> default headers that name a credential
        /// header, and every registered <see cref="Http.IHttpCredentialInterceptor"/> — may be applied.
        /// </summary>
        public readonly bool AllowsGlobalCredentials;

        /// <summary>
        /// Why credentials are withheld, or why the request could not be routed. Empty when there is
        /// nothing to report. Suitable for a one-time warning; contains no credential value.
        /// </summary>
        public readonly string Reason;

        /// <summary>The destination host, lowercased, or empty when the request has no absolute URL yet.</summary>
        public readonly string Host;

        private LegacyRouteDecision(
            LegacyRouteKind kind,
            NetworkRouteKey route,
            string relativePath,
            bool allowsGlobalCredentials,
            string reason,
            string host)
        {
            Kind = kind;
            Route = route;
            RelativePath = relativePath ?? string.Empty;
            AllowsGlobalCredentials = allowsGlobalCredentials;
            Reason = reason ?? string.Empty;
            Host = host ?? string.Empty;
        }

        /// <summary>Creates a decision to leave the request on the legacy path unchanged.</summary>
        /// <param name="reason">Why it was not routed, or <c>null</c>.</param>
        /// <param name="host">The destination host, or <c>null</c> when not known.</param>
        public static LegacyRouteDecision Unrouted(string reason = null, string host = null) =>
            new LegacyRouteDecision(LegacyRouteKind.Unrouted, default, null, true, reason, host);

        /// <summary>Creates a decision to send on a catalog route.</summary>
        /// <param name="route">The route the request maps to.</param>
        /// <param name="relativePath">Path relative to the service origin.</param>
        /// <param name="host">The destination host.</param>
        public static LegacyRouteDecision Routed(NetworkRouteKey route, string relativePath, string host) =>
            new LegacyRouteDecision(LegacyRouteKind.Routed, route, relativePath, true, null, host);

        /// <summary>Creates a decision for a full URL the catalog does not claim.</summary>
        /// <param name="host">The unclaimed host.</param>
        /// <param name="allowsGlobalCredentials">
        /// Whether the catalog's legacy transition flag permits credentials to travel anyway.
        /// </param>
        /// <param name="reason">What the caller should be told.</param>
        public static LegacyRouteDecision External(string host, bool allowsGlobalCredentials, string reason) =>
            new LegacyRouteDecision(LegacyRouteKind.External, default, null, allowsGlobalCredentials, reason, host);

        /// <summary>Renders the decision for logs. Contains no credential value.</summary>
        public override string ToString()
        {
            string suffix = string.IsNullOrEmpty(Reason) ? string.Empty : $" — {Reason}";
            return Kind == LegacyRouteKind.Routed
                ? $"{Kind} {Route} '{RelativePath}'{suffix}"
                : $"{Kind} {Host}{suffix}";
        }
    }
}
