using System;
using System.Collections.Generic;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;
using Molca.Networking.Routing;

namespace Molca.Networking.Compatibility
{
    /// <summary>
    /// Maps a legacy <see cref="HttpRequest"/> onto the network catalog: which route it belongs to, and
    /// whether process-wide credentials may travel with it.
    /// </summary>
    /// <remarks>
    /// Pure — a function of an immutable <see cref="NetworkCatalogSnapshot"/>, the legacy base URL, and
    /// the request. No Unity API, no I/O, no mutable state, so the editor's migration preview and the
    /// runtime client reach identical conclusions.
    /// <para>
    /// The mapping rules, in the order they are tried:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>No catalog</b> → <see cref="LegacyRouteKind.Unrouted"/> with credentials allowed. A project
    /// that has not adopted the catalog keeps working exactly as before; that is the compatibility
    /// contract (plan §10.1). A foreign-host full URL still reports a <see cref="LegacyRouteDecision.Reason"/>
    /// so the leak is visible without being silently changed.
    /// </description></item>
    /// <item><description>
    /// <b>Relative URL</b> → the legacy default service in the catalog's default environment, which is
    /// what migration synthesizes from <c>HttpModule.BaseUrl</c>. Absent that service, unrouted.
    /// </description></item>
    /// <item><description>
    /// <b>Full URL under a bound service origin</b> → that service's route, with the remainder as the
    /// relative path. The longest matching origin wins, so a service bound to
    /// <c>https://api.example.com/v1</c> is preferred over one bound to <c>https://api.example.com</c>.
    /// </description></item>
    /// <item><description>
    /// <b>Full URL to a host a service claims, but under no bound origin's path</b> → unrouted with
    /// credentials allowed. The host is the project's own, so nothing leaks, but the path cannot be
    /// attributed to a route deterministically and guessing one would send traffic somewhere the author
    /// did not author.
    /// </description></item>
    /// <item><description>
    /// <b>Anything else</b> → <see cref="LegacyRouteKind.External"/>, credentials withheld unless
    /// <see cref="NetworkCatalog.AllowLegacyGlobalAuthOnExternalUrls"/> is set.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed class LegacyRouteMapper
    {
        /// <summary>
        /// Headers treated as carrying a credential regardless of what the catalog declares.
        /// </summary>
        /// <remarks>
        /// The catalog's own credential profiles contribute their <c>HeaderName</c> on top of these, so a
        /// project using a bespoke header (<c>X-Api-Key</c>) is covered by authoring it as a credential
        /// profile rather than by this list growing.
        /// </remarks>
        private static readonly string[] WellKnownCredentialHeaders =
        {
            "Authorization",
            "Proxy-Authorization",
            "Cookie",
        };

        private readonly NetworkCatalogSnapshot _snapshot;
        private readonly string _legacyBaseHost;
        private readonly HashSet<string> _credentialHeaders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Bound HTTP origins in the default environment, longest first so prefix matching prefers the
        // most specific service. Built once because the snapshot never changes.
        private readonly List<OriginBinding> _origins = new List<OriginBinding>();

        // Every host any service is bound to in any environment. A request to one of these is talking to
        // the project's own infrastructure even when its path cannot be attributed to a route.
        private readonly HashSet<string> _knownHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly struct OriginBinding
        {
            public readonly string Origin;
            public readonly string ServiceId;

            public OriginBinding(string origin, string serviceId)
            {
                Origin = origin;
                ServiceId = serviceId;
            }
        }

        /// <summary>The snapshot this mapper reads.</summary>
        public NetworkCatalogSnapshot Snapshot => _snapshot;

        /// <summary>Whether a catalog was present when this mapper was built.</summary>
        public bool HasCatalog => _snapshot.HasCatalog;

        /// <summary>
        /// Creates a mapper.
        /// </summary>
        /// <param name="snapshot">The catalog snapshot; <c>null</c> is treated as no catalog.</param>
        /// <param name="legacyBaseUrl">
        /// The legacy <c>HttpModule.BaseUrl</c>, used only to recognize the project's own host when no
        /// catalog exists. May be <c>null</c> or empty.
        /// </param>
        public LegacyRouteMapper(NetworkCatalogSnapshot snapshot, string legacyBaseUrl)
        {
            _snapshot = snapshot ?? NetworkCatalogSnapshot.Empty;
            _legacyBaseHost = HostOfLoose(legacyBaseUrl);

            foreach (string header in WellKnownCredentialHeaders)
                _credentialHeaders.Add(header);

            if (!_snapshot.HasCatalog)
                return;

            foreach (var profile in _snapshot.Catalog.CredentialProfiles)
            {
                if (profile != null && !string.IsNullOrEmpty(profile.HeaderName))
                    _credentialHeaders.Add(profile.HeaderName);
            }

            IndexBindings();
        }

        private void IndexBindings()
        {
            string defaultEnvironment = _snapshot.DefaultEnvironmentId;

            foreach (var pair in _snapshot.Services)
            {
                var service = pair.Value;
                if (service?.Bindings == null) continue;

                foreach (var binding in service.Bindings)
                {
                    if (binding == null || !binding.Enabled) continue;

                    if (!NetworkOrigin.TryNormalize(binding.HttpOrigin, false, out string origin, out _))
                        continue;

                    string host = NetworkHostRule.HostOf(origin);
                    if (host != null)
                        _knownHosts.Add(host);

                    // Only the default environment's bindings can be routed to: a legacy call site names
                    // no environment, and picking a non-default one would silently retarget it.
                    if (string.Equals(binding.EnvironmentId, defaultEnvironment, StringComparison.Ordinal))
                        _origins.Add(new OriginBinding(origin, service.Id));
                }
            }

            // Longest origin first: prefix matching must prefer the most specific service.
            _origins.Sort((left, right) =>
            {
                int byLength = right.Origin.Length.CompareTo(left.Origin.Length);
                return byLength != 0
                    ? byLength
                    : string.Compare(left.ServiceId, right.ServiceId, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// Whether a header name is treated as carrying a credential.
        /// </summary>
        /// <param name="headerName">The header name; compared case-insensitively.</param>
        /// <returns><c>true</c> for a well-known auth header or one a catalog credential profile names.</returns>
        public bool IsCredentialHeader(string headerName) =>
            !string.IsNullOrEmpty(headerName) && _credentialHeaders.Contains(headerName);

        /// <summary>
        /// Decides what to do with one legacy request.
        /// </summary>
        /// <param name="request">The caller's request. Never mutated.</param>
        /// <returns>The decision; always usable, never <c>null</c>-like.</returns>
        public LegacyRouteDecision Map(HttpRequest request)
        {
            if (request == null)
                return LegacyRouteDecision.Unrouted("The request is null.");

            if (!request.useFullUrl)
                return MapRelative(request);

            return MapFullUrl(request.url);
        }

        private LegacyRouteDecision MapRelative(HttpRequest request)
        {
            if (!_snapshot.HasCatalog)
                return LegacyRouteDecision.Unrouted("No network catalog is configured.", _legacyBaseHost);

            string environmentId = _snapshot.DefaultEnvironmentId;
            if (string.IsNullOrEmpty(environmentId))
            {
                return LegacyRouteDecision.Unrouted(
                    "The catalog names no default environment, so a relative URL cannot be routed.",
                    _legacyBaseHost);
            }

            if (!_snapshot.Services.ContainsKey(NetworkIds.LegacyDefaultServiceId))
            {
                return LegacyRouteDecision.Unrouted(
                    $"The catalog has no '{NetworkIds.LegacyDefaultServiceId}' service, so relative URLs " +
                    "still resolve against HttpModule.BaseUrl. Run the legacy migration to create one.",
                    _legacyBaseHost);
            }

            var route = new NetworkRouteKey(environmentId, NetworkIds.LegacyDefaultServiceId);
            string host = HostOfService(NetworkIds.LegacyDefaultServiceId, environmentId);

            return LegacyRouteDecision.Routed(route, request.url ?? string.Empty, host);
        }

        private LegacyRouteDecision MapFullUrl(string url)
        {
            string host = HostOfLoose(url);

            if (!_snapshot.HasCatalog)
            {
                // Without a catalog there is nothing that could authorize or refuse this host, so
                // behaviour is unchanged. The reason still names the leak so it can be surfaced.
                bool foreign = !string.IsNullOrEmpty(host) &&
                               !string.IsNullOrEmpty(_legacyBaseHost) &&
                               !string.Equals(host, _legacyBaseHost, StringComparison.OrdinalIgnoreCase);

                return LegacyRouteDecision.Unrouted(
                    foreign
                        ? $"'{host}' is not the base-URL host and no catalog exists to authorize it, so " +
                          "process-wide credentials still travel with this request. Create a network " +
                          "catalog to scope them."
                        : null,
                    host);
            }

            if (string.IsNullOrEmpty(host))
            {
                return LegacyRouteDecision.External(
                    string.Empty, _snapshot.AllowLegacyGlobalAuthOnExternalUrls,
                    $"'{url}' has no resolvable host, so no service can claim it.");
            }

            foreach (var candidate in _origins)
            {
                if (!TryTrimOrigin(url, candidate.Origin, out string relativePath))
                    continue;

                var route = new NetworkRouteKey(_snapshot.DefaultEnvironmentId, candidate.ServiceId);
                return LegacyRouteDecision.Routed(route, relativePath, host);
            }

            if (_knownHosts.Contains(host))
            {
                return LegacyRouteDecision.Unrouted(
                    $"'{url}' is on '{host}', which the catalog binds, but under no bound origin's path. " +
                    "Bind the origin that covers this path to route it.",
                    host);
            }

            bool allowed = _snapshot.AllowLegacyGlobalAuthOnExternalUrls;
            return LegacyRouteDecision.External(
                host,
                allowed,
                allowed
                    ? $"No catalog service claims '{host}', but AllowLegacyGlobalAuthOnExternalUrls is on, " +
                      "so process-wide credentials still travel with this request. Turn the flag off once " +
                      "every external host is authored as a service."
                    : $"No catalog service claims '{host}', so process-wide credentials are withheld. " +
                      "Author it as a service, or set AllowLegacyGlobalAuthOnExternalUrls for the " +
                      "transition window.");
        }

        /// <summary>
        /// Whether <paramref name="url"/> sits under <paramref name="origin"/>, and what remains.
        /// </summary>
        /// <remarks>
        /// A prefix test alone would let <c>https://api.example.com/v1x</c> match an origin of
        /// <c>https://api.example.com/v1</c>, so the character after the origin must be a path or query
        /// boundary — or absent, meaning the URL is the origin itself.
        /// </remarks>
        private static bool TryTrimOrigin(string url, string origin, out string relativePath)
        {
            relativePath = string.Empty;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(origin))
                return false;

            if (url.Length < origin.Length ||
                string.Compare(url, 0, origin, 0, origin.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            if (url.Length == origin.Length)
                return true;

            char boundary = url[origin.Length];
            if (boundary != '/' && boundary != '?' && boundary != '#')
                return false;

            relativePath = url.Substring(origin.Length).TrimStart('/');
            return true;
        }

        private string HostOfService(string serviceId, string environmentId)
        {
            if (!_snapshot.Services.TryGetValue(serviceId, out var service))
                return string.Empty;

            var binding = service.FindBinding(environmentId);
            if (binding == null)
                return string.Empty;

            return NetworkOrigin.TryNormalize(binding.HttpOrigin, false, out string origin, out _)
                ? NetworkHostRule.HostOf(origin) ?? string.Empty
                : string.Empty;
        }

        /// <summary>
        /// The lowercased host of a URL that may be relative, malformed, or empty.
        /// </summary>
        /// <returns>The host, or empty when there is none to read.</returns>
        private static string HostOfLoose(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            return Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri uri)
                ? uri.Host.ToLowerInvariant()
                : string.Empty;
        }
    }
}
