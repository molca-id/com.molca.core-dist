using System;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Molca.Networking;
using Molca.Networking.Http.Models;
using Molca.Networking.Routing;

namespace Molca.ContentPackage.Release
{
    /// <summary>
    /// The default <see cref="IContentReleaseClient"/>, speaking to <c>/content/v1</c> through the
    /// routed HTTP pipeline.
    /// </summary>
    /// <remarks>
    /// Every request here carries the project-scoped build token, so every request here must reach a
    /// Molca host and nothing else. Routing through <see cref="IRoutedHttpClient"/> is what makes
    /// that structural: the origin comes from the network catalog rather than from a string in a
    /// response, so a compromised or merely wrong <c>manifestUrl</c> cannot redirect a credential
    /// anywhere. <see cref="RelativePathWithin"/> enforces the same rule for the one URL the server
    /// does hand us.
    ///
    /// Object <em>bytes</em> are not fetched here. Those go to the gateway with a ticket and no
    /// build token at all (contract §6.3), which is <see cref="ReleaseAccessProvider"/>'s job.
    /// </remarks>
    public sealed class ContentReleaseClient : IContentReleaseClient
    {
        private readonly IRoutedHttpClient _http;
        private readonly string _serviceId;
        private readonly Func<string> _buildTokenProvider;
        private readonly string _pathPrefix;

        /// <summary>Builds a client.</summary>
        /// <param name="http">The routed pipeline. Required.</param>
        /// <param name="serviceId">Network catalog service id for the Molca content host.</param>
        /// <param name="buildTokenProvider">
        /// Supplies the current build token. A delegate rather than a value so a rotated token is
        /// picked up without rebuilding the client, and so the token is never held in a field longer
        /// than one request needs it.
        /// </param>
        /// <param name="pathPrefix">Path prefix of the content API on that service.</param>
        /// <exception cref="ArgumentNullException">The pipeline or token provider is null.</exception>
        public ContentReleaseClient(
            IRoutedHttpClient http,
            string serviceId,
            Func<string> buildTokenProvider,
            string pathPrefix = "/content/v1")
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _serviceId = string.IsNullOrWhiteSpace(serviceId)
                ? throw new ArgumentException("A content service id is required.", nameof(serviceId))
                : serviceId;
            _buildTokenProvider = buildTokenProvider ?? throw new ArgumentNullException(nameof(buildTokenProvider));
            _pathPrefix = (pathPrefix ?? "").TrimEnd('/');
        }

        /// <inheritdoc/>
        public async Awaitable<ContentReleaseResponse<ContentReleaseDescriptor>> ResolveActiveAsync(
            string platform, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(platform))
                return ContentReleaseResponse<ContentReleaseDescriptor>.Fail(
                    ContentReleaseReason.PlatformUnsupported, "No platform was supplied.");

            var request = Authorized(HttpMethod.GET, out string tokenError);
            if (request == null)
                return ContentReleaseResponse<ContentReleaseDescriptor>.Fail(ContentReleaseReason.Unauthorized, tokenError);

            string path = $"{_pathPrefix}/active?platform={UnityWebRequest.EscapeURL(platform)}";
            var outcome = await _http.SendToServiceAsync(
                _serviceId, request, NetworkRouteQuery.ForPath(path), cancellationToken);

            // A 4xx here still carries a contract reason in the body, and that reason is the whole
            // point -- "unauthorized" and "no_release" need opposite responses from an operator.
            var descriptor = ContentReleaseDescriptor.Parse(outcome.Text);
            if (!outcome.IsSuccess)
            {
                string reason = !string.IsNullOrEmpty(descriptor?.reason)
                    ? descriptor.reason
                    : ReasonForStatus(outcome.StatusCode);
                return ContentReleaseResponse<ContentReleaseDescriptor>.Fail(
                    reason, $"HTTP {outcome.StatusCode} resolving the active release.");
            }

            if (descriptor == null)
                return ContentReleaseResponse<ContentReleaseDescriptor>.Fail(
                    ContentReleaseReason.ManifestUntrusted, "Active response was not readable.");

            // `none` is a successful call reporting a normal state, not a failed call.
            return ContentReleaseResponse<ContentReleaseDescriptor>.Ok(descriptor);
        }

        /// <inheritdoc/>
        public async Awaitable<ContentReleaseResponse<ReleaseManifestPayload>> FetchManifestAsync(
            ContentReleaseDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            if (descriptor == null || string.IsNullOrEmpty(descriptor.manifestUrl))
                return ContentReleaseResponse<ReleaseManifestPayload>.Fail(
                    ContentReleaseReason.NoRelease, "Descriptor names no manifest.");

            // The server told us where the manifest is, and we are about to send it a credential.
            // Reduce that URL to a path within the routed service, or refuse: a manifestUrl pointing
            // at another host is either a misconfiguration or an attempt to collect a build token,
            // and following it would be indistinguishable either way.
            string path = RelativePathWithin(descriptor.manifestUrl, out string containmentError);
            if (path == null)
                return ContentReleaseResponse<ReleaseManifestPayload>.Fail(
                    ContentReleaseReason.ManifestUntrusted, containmentError);

            var request = Authorized(HttpMethod.GET, out string tokenError);
            if (request == null)
                return ContentReleaseResponse<ReleaseManifestPayload>.Fail(ContentReleaseReason.Unauthorized, tokenError);
            request.expectedResponseType = ResponseType.Binary;

            var outcome = await _http.SendToServiceAsync(
                _serviceId, request, NetworkRouteQuery.ForPath(path), cancellationToken);

            if (!outcome.IsSuccess)
                return ContentReleaseResponse<ReleaseManifestPayload>.Fail(
                    outcome.StatusCode == 410 ? ContentReleaseReason.ReleaseRevoked : ReasonForStatus(outcome.StatusCode),
                    $"HTTP {outcome.StatusCode} fetching the release manifest.");

            byte[] bytes = outcome.Response?.rawData;
            if (bytes == null || bytes.Length == 0)
                return ContentReleaseResponse<ReleaseManifestPayload>.Fail(
                    ContentReleaseReason.ManifestUntrusted, "Manifest body was empty.");

            // Prefer the signature carried with the bytes; fall back to the descriptor's copy. They
            // must agree, and if they do not, the digest check in the verifier is what catches it.
            string signature = outcome.Headers?["X-Molca-Release-Signature"];
            if (string.IsNullOrEmpty(signature)) signature = descriptor.signature;

            return ContentReleaseResponse<ReleaseManifestPayload>.Ok(
                new ReleaseManifestPayload { Bytes = bytes, Signature = signature });
        }

        /// <inheritdoc/>
        public async Awaitable<ContentReleaseResponse<ContentReleaseDescriptor.AccessMaterial>> RequestAccessAsync(
            string releaseId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(releaseId))
                return ContentReleaseResponse<ContentReleaseDescriptor.AccessMaterial>.Fail(
                    ContentReleaseReason.NoRelease, "No release id was supplied.");

            var request = Authorized(HttpMethod.POST, out string tokenError);
            if (request == null)
                return ContentReleaseResponse<ContentReleaseDescriptor.AccessMaterial>.Fail(
                    ContentReleaseReason.Unauthorized, tokenError);
            request.SetJsonBody("{}");

            var outcome = await _http.SendToServiceAsync(
                _serviceId, request,
                NetworkRouteQuery.ForPath($"{_pathPrefix}/releases/{Uri.EscapeDataString(releaseId)}/access"),
                cancellationToken);

            if (!outcome.IsSuccess)
                return ContentReleaseResponse<ContentReleaseDescriptor.AccessMaterial>.Fail(
                    outcome.StatusCode == 410 ? ContentReleaseReason.ReleaseRevoked : ReasonForStatus(outcome.StatusCode),
                    $"HTTP {outcome.StatusCode} requesting access material.");

            ContentReleaseDescriptor.AccessMaterial access;
            try { access = JsonUtility.FromJson<ContentReleaseDescriptor.AccessMaterial>(outcome.Text); }
            catch { access = null; }

            if (access == null || string.IsNullOrEmpty(access.mode))
                return ContentReleaseResponse<ContentReleaseDescriptor.AccessMaterial>.Fail(
                    ContentReleaseReason.AccessModeUnsupported, "Access response was not readable.");

            return ContentReleaseResponse<ContentReleaseDescriptor.AccessMaterial>.Ok(access);
        }

        private HttpRequest Authorized(HttpMethod method, out string error)
        {
            error = "";
            string token = "";
            try { token = _buildTokenProvider() ?? ""; }
            catch (Exception exception) { error = $"Build token could not be read: {exception.Message}"; return null; }

            if (string.IsNullOrWhiteSpace(token))
            {
                error = "No build token is available; this player is not provisioned for remote content.";
                return null;
            }

            var request = new HttpRequest { method = method, url = "" };
            request.AddHeader("Authorization", $"Bearer {token}");
            return request;
        }

        /// <summary>
        /// Reduces an absolute URL to a path, or returns null when it leaves the content API.
        /// </summary>
        /// <remarks>
        /// Compares the path prefix rather than the host, because the host is the routed service's
        /// business and may legitimately differ per environment. What must not vary is that the URL
        /// stays inside <c>/content/v1</c>: a relative path is resolved against the catalog origin,
        /// so anything that survives this check is sent to the configured Molca host by construction.
        /// </remarks>
        internal string RelativePathWithin(string absoluteUrl, out string error)
        {
            error = "";
            if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var uri))
            {
                error = "Manifest URL is not absolute.";
                return null;
            }
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            {
                error = "Manifest URL is not HTTPS.";
                return null;
            }
            if (!uri.AbsolutePath.StartsWith(_pathPrefix + "/", StringComparison.Ordinal))
            {
                error = "Manifest URL falls outside the content API and was not followed.";
                return null;
            }
            return uri.PathAndQuery;
        }

        private static string ReasonForStatus(int statusCode) => statusCode switch
        {
            401 => ContentReleaseReason.Unauthorized,
            403 => ContentReleaseReason.Unauthorized,
            404 => ContentReleaseReason.NoRelease,
            410 => ContentReleaseReason.ReleaseRevoked,
            _ => ContentReleaseReason.NoRelease,
        };
    }
}
