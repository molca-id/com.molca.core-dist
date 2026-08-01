using System;
using System.Collections.Generic;
using Molca.Networking.Configuration;
using Molca.Networking.Routing;

namespace Molca.Networking.Streaming
{
    /// <summary>
    /// What a <see cref="NetworkStreamRoute"/> resolved to for one connection attempt — or why it did
    /// not.
    /// </summary>
    /// <remarks>
    /// A projection of <see cref="NetworkRouteResolution"/>, not a second resolution. Streaming gets the
    /// destination, the effective policy, the credential profile, the allowed-host list, and the
    /// production posture from the same resolver an HTTP request uses, which is what makes "route and
    /// credential behavior matches HTTP" (plan §5 Phase 6 exit criteria) true by construction rather
    /// than by parallel implementation.
    /// </remarks>
    public sealed class NetworkStreamBinding
    {
        /// <summary>The underlying resolution. Never <c>null</c>.</summary>
        public NetworkRouteResolution Resolution { get; }

        /// <summary>The protocol this binding resolved for.</summary>
        public NetworkProtocols Protocol { get; }

        /// <summary>Whether the route produced a usable absolute URI.</summary>
        public bool Resolves => Resolution.Resolves;

        /// <summary>Why resolution failed, or empty.</summary>
        public string FailureMessage => Resolution.FailureMessage;

        /// <summary>The category of a resolution failure.</summary>
        public NetworkErrorCategory FailureCategory => Resolution.FailureCategory;

        /// <summary>The route that was resolved.</summary>
        public NetworkRouteKey Route => Resolution.Route;

        /// <summary>The absolute URI to connect to, or empty.</summary>
        public string Uri => Resolution.ResolvedUri;

        /// <summary>The host of <see cref="Uri"/>, or empty.</summary>
        public string Host => Resolution.Host;

        /// <summary>The effective policy. Never <c>null</c>.</summary>
        public NetworkEffectivePolicy Policy => Resolution.Policy;

        /// <summary>The credential profile the service names, or <c>null</c> for anonymous.</summary>
        public NetworkCredentialProfile Credential => Resolution.Credential;

        /// <summary>Whether that credential is authorized for <see cref="Host"/>.</summary>
        public bool CredentialAppliesToHost => Resolution.CredentialAppliesToHost;

        /// <summary>Whether the target environment enforces production safety.</summary>
        public bool IsProduction => Resolution.IsProduction;

        /// <summary>The service's effective allowed-host patterns.</summary>
        public IReadOnlyList<string> AllowedHosts => Resolution.AllowedHosts;

        private NetworkStreamBinding(NetworkRouteResolution resolution, NetworkProtocols protocol)
        {
            Resolution = resolution;
            Protocol = protocol;
        }

        /// <summary>
        /// Resolves a streaming route.
        /// </summary>
        /// <param name="resolver">The shared route resolver.</param>
        /// <param name="route">The route to resolve.</param>
        /// <param name="protocol">The protocol whose origin to resolve.</param>
        /// <returns>The binding; never <c>null</c>. Check <see cref="Resolves"/>.</returns>
        /// <remarks>
        /// An unconfigured route is reported as a <see cref="NetworkErrorCategory.Configuration"/>
        /// failure rather than throwing, because a provider asset with no service set is a normal state
        /// during authoring and must not take a subsystem down.
        /// </remarks>
        public static NetworkStreamBinding Resolve(
            INetworkRouteResolver resolver,
            NetworkStreamRoute route,
            NetworkProtocols protocol)
        {
            if (resolver == null)
            {
                return new NetworkStreamBinding(
                    Unconfigured(protocol, "No route resolver is available."), protocol);
            }

            if (!route.TryToRouteKey(resolver.Snapshot, out var key, out string failure))
                return new NetworkStreamBinding(Unconfigured(protocol, failure), protocol);

            return new NetworkStreamBinding(resolver.Resolve(key, route.ToQuery(protocol)), protocol);
        }

        /// <summary>
        /// A resolution that failed before a route key could even be formed.
        /// </summary>
        /// <remarks>
        /// <see cref="NetworkRouteResolution.Route"/> is left default rather than half-filled:
        /// <see cref="NetworkRouteKey"/> refuses an empty environment or service by construction, and a
        /// key naming one half of a route would read as more configured than it is.
        /// </remarks>
        /// <summary>
        /// A binding for a destination the project named directly, outside the catalog.
        /// </summary>
        /// <param name="uri">The absolute URI to connect to.</param>
        /// <param name="protocol">The protocol spoken.</param>
        /// <returns>The binding; unresolved when <paramref name="uri"/> is not absolute.</returns>
        /// <remarks>
        /// The compatibility path for a provider that still authors its own URL. It exists so the
        /// <em>session</em> can own the connection state either way — the mutable-state fix does not
        /// depend on adopting the catalog.
        /// <para>
        /// It carries library-default policy and no credential, and it is <b>not</b> covered by the
        /// catalog's allowed-host list, production scheme rule, or credential scope. That is the
        /// documented cost of a direct URL, not an oversight: there is no service to read those rules
        /// from. <see cref="Route"/> is left empty so nothing downstream mistakes it for a routed send.
        /// </para>
        /// </remarks>
        public static NetworkStreamBinding Direct(string uri, NetworkProtocols protocol)
        {
            if (string.IsNullOrWhiteSpace(uri) ||
                !System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return new NetworkStreamBinding(
                    Unconfigured(protocol, $"'{uri}' is not an absolute URI."), protocol);
            }

            var resolution = new NetworkRouteResolution
            {
                Protocol = protocol,
                Resolves = true,
                ResolvedUri = uri,
                Origin = uri,
                Host = parsed.Host,
                Policy = NetworkPolicyResolver.Resolve(null, null, null, null),
            };

            return new NetworkStreamBinding(resolution, protocol);
        }

        /// <summary>Whether this binding came from a directly authored URL rather than a catalog route.</summary>
        public bool IsDirect => Resolves && Route.IsEmpty;

        private static NetworkRouteResolution Unconfigured(NetworkProtocols protocol, string failure)
        {
            return new NetworkRouteResolution
            {
                Protocol = protocol,
                Resolves = false,
                FailureCategory = NetworkErrorCategory.Configuration,
                FailureMessage = failure,
                Policy = NetworkPolicyResolver.Resolve(null, null, null, null),
            };
        }

        /// <inheritdoc />
        public override string ToString() => Resolution.ToString();
    }
}
