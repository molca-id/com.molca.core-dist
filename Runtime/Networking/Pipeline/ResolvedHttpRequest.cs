using System;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;
using Molca.Networking.Routing;

namespace Molca.Networking.Pipeline
{
    /// <summary>
    /// A request frozen before it is queued: final URI, method, headers, encoded body, effective
    /// policy, credential scope, correlation ID, and deadline.
    /// </summary>
    /// <remarks>
    /// This is the boundary plan §6.2 requires. Once a request is resolved, nothing downstream reads
    /// <c>GlobalSettings</c>, a mutable request asset, the catalog, or the Hub's current selection — so
    /// a configuration edit or an environment switch cannot change what an in-flight request does.
    /// <para>
    /// The credential <em>scope</em> travels here; the credential <em>value</em> never does. It is
    /// fetched per attempt against the attempt's final host, so a redirect cannot carry a token
    /// off-domain.
    /// </para>
    /// </remarks>
    public sealed class ResolvedHttpRequest
    {
        /// <summary>The route this request targets.</summary>
        public NetworkRouteKey Route { get; }

        /// <summary>The endpoint template applied, or <c>null</c> for a raw path send.</summary>
        public NetworkEndpointDefinition Endpoint { get; }

        /// <summary>The endpoint ID, or empty. Convenience for diagnostics.</summary>
        public string EndpointId => Endpoint?.Id ?? string.Empty;

        /// <summary>The final absolute URI, query string included.</summary>
        public string Uri { get; }

        /// <summary>The host of <see cref="Uri"/>.</summary>
        public string Host { get; }

        /// <summary>The HTTP method.</summary>
        public HttpMethod Method { get; }

        /// <summary>Headers to send, case-insensitive. Excludes the credential header.</summary>
        public NetworkHeaderCollection Headers { get; }

        /// <summary>The effective policy, with per-field provenance.</summary>
        public NetworkEffectivePolicy Policy { get; }

        /// <summary>
        /// The credential profile authorized for this request, or <c>null</c> when the request is
        /// anonymous — either because no credential is configured or because scope validation rejected
        /// the resolved host.
        /// </summary>
        public NetworkCredentialProfile Credential { get; }

        /// <summary>
        /// Host patterns a redirect target must match before credentials may follow, and before an
        /// <see cref="NetworkRedirectMode.AllowedHosts"/> redirect is followed at all.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> AllowedHosts { get; }

        /// <summary>Correlation ID tying diagnostics, attempts, and logs together for this send.</summary>
        public string CorrelationId { get; }

        /// <summary>When the request was resolved.</summary>
        public DateTime CreatedUtc { get; }

        /// <summary>
        /// When the overall budget expires. Includes time spent queued, authenticating, waiting to
        /// retry, and transferring (plan §6.4).
        /// </summary>
        public DateTime DeadlineUtc { get; }

        /// <summary>Whether the target environment enforces production safety rules.</summary>
        public bool IsProduction { get; }

        /// <summary>The prepared request handed to the transport. Treated as read-only.</summary>
        /// <remarks>
        /// Carries <c>useFullUrl = true</c> and no query parameters, so
        /// <see cref="HttpRequest.FullUrl"/> returns <see cref="Uri"/> verbatim and never consults
        /// <c>GlobalSettings</c> for a base URL. Redirect following is disabled on it unconditionally:
        /// the pipeline follows redirects itself so it can revalidate the target host and strip
        /// credentials, which <c>UnityWebRequest</c>'s internal following gives no opportunity to do.
        /// </remarks>
        public HttpRequest TransportRequest { get; }

        /// <summary>Creates a resolved request. Built by the routed client's preparation step.</summary>
        /// <param name="route">The target route.</param>
        /// <param name="endpoint">The endpoint template, or <c>null</c>.</param>
        /// <param name="uri">The final absolute URI.</param>
        /// <param name="method">The HTTP method.</param>
        /// <param name="headers">Headers to send, excluding the credential header.</param>
        /// <param name="policy">The effective policy.</param>
        /// <param name="credential">The authorized credential profile, or <c>null</c>.</param>
        /// <param name="allowedHosts">Host patterns for redirect revalidation.</param>
        /// <param name="correlationId">Correlation ID for diagnostics.</param>
        /// <param name="createdUtc">Resolution time.</param>
        /// <param name="isProduction">Whether production safety rules apply.</param>
        /// <param name="transportRequest">The prepared transport request.</param>
        internal ResolvedHttpRequest(
            NetworkRouteKey route,
            NetworkEndpointDefinition endpoint,
            string uri,
            HttpMethod method,
            NetworkHeaderCollection headers,
            NetworkEffectivePolicy policy,
            NetworkCredentialProfile credential,
            System.Collections.Generic.IReadOnlyList<string> allowedHosts,
            string correlationId,
            DateTime createdUtc,
            bool isProduction,
            HttpRequest transportRequest)
        {
            Route = route;
            Endpoint = endpoint;
            Uri = uri;
            Host = NetworkHostRule.HostOf(uri) ?? string.Empty;
            Method = method;
            Headers = headers ?? NetworkHeaderCollection.Empty;
            Policy = policy;
            Credential = credential;
            AllowedHosts = allowedHosts ?? Array.Empty<string>();
            CorrelationId = correlationId;
            CreatedUtc = createdUtc;
            IsProduction = isProduction;
            TransportRequest = transportRequest;

            float budget = policy.OverallTimeoutSeconds.Value;
            DeadlineUtc = budget > 0f
                ? createdUtc.AddSeconds(budget)
                : DateTime.MaxValue;
        }

        /// <summary>Whether this request sends a credential.</summary>
        public bool IsAuthenticated => Credential != null && !Credential.IsAnonymous;

        /// <summary>
        /// Seconds left in the overall budget at <paramref name="nowUtc"/>. Zero once elapsed;
        /// <see cref="float.PositiveInfinity"/> when no budget is configured.
        /// </summary>
        /// <param name="nowUtc">The current UTC time.</param>
        public float RemainingBudgetSeconds(DateTime nowUtc)
        {
            if (DeadlineUtc == DateTime.MaxValue)
                return float.PositiveInfinity;

            double remaining = (DeadlineUtc - nowUtc).TotalSeconds;
            return remaining <= 0d ? 0f : (float)remaining;
        }

        /// <summary>Whether the overall budget has elapsed at <paramref name="nowUtc"/>.</summary>
        /// <param name="nowUtc">The current UTC time.</param>
        public bool HasExpired(DateTime nowUtc) => RemainingBudgetSeconds(nowUtc) <= 0f;

        /// <summary>
        /// Whether this request may be retried under its policy, accounting for the endpoint's
        /// mutation class as well as the method.
        /// </summary>
        public bool AllowsRetry =>
            Policy.AllowsRetryFor(Method, Endpoint?.IsIdempotent ?? true);

        /// <summary>A redacted single-line projection safe for logs and export.</summary>
        /// <remarks>
        /// Names the credential profile, never a value, and does not include headers or body — both are
        /// the likeliest places for secrets, and diagnostics capture them only under an explicit
        /// <see cref="NetworkEffectivePolicy.CaptureBodies"/> opt-in.
        /// </remarks>
        public string ToRedactedString()
        {
            string credential = IsAuthenticated ? $" cred={Credential.Id}" : " anonymous";
            string endpoint = string.IsNullOrEmpty(EndpointId) ? string.Empty : $" endpoint={EndpointId}";
            return $"{Method} {Uri} route={Route}{endpoint}{credential} correlation={CorrelationId}";
        }
    }
}
