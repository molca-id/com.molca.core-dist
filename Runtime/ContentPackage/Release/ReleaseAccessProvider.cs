using System;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Molca.ContentPackage.Release
{
    /// <summary>
    /// Holds the release-scoped access ticket and attaches it to Addressables requests.
    /// </summary>
    /// <remarks>
    /// This is the whole of <c>access.mode = "gateway"</c> on the client (contract §6.1). Catalog
    /// internal ids point at stable Molca object routes; this appends a short-lived ticket, and the
    /// gateway answers with a presigned storage redirect.
    ///
    /// Only the gateway mode is implemented. A server offering <c>presigned-map</c> is refused with
    /// <see cref="ContentReleaseReason.AccessModeUnsupported"/> rather than guessed at — §6 requires
    /// exactly that, and a client that guessed would build URLs a bucket rejects and report it as a
    /// download failure.
    ///
    /// Two properties matter more than anything else here:
    ///
    /// <list type="bullet">
    /// <item>The build token never touches this path. The ticket is the only credential on an object
    /// request, so nothing that reaches the storage host can be replayed against the control
    /// plane.</item>
    /// <item><see cref="Transform"/> is synchronous and allocation-light, because Addressables calls
    /// it on the request path for every object. It therefore cannot refresh anything; keeping the
    /// ticket fresh is <see cref="EnsureFreshAsync"/>'s job, called by the activation coordinator
    /// around the download loop.</item>
    /// </list>
    /// </remarks>
    public sealed class ReleaseAccessProvider : IDisposable
    {
        /// <summary>
        /// How long before expiry the ticket is treated as stale.
        /// </summary>
        /// <remarks>
        /// A large bundle can be in flight for minutes, and expiry is measured server-side when the
        /// request <em>arrives</em>. Refreshing only at expiry would hand out a ticket that dies
        /// mid-transfer, and the failure surfaces as a truncated download rather than as anything
        /// naming a ticket.
        /// </remarks>
        public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

        private readonly IContentReleaseClient _client;
        private readonly Func<DateTime> _clock;
        private readonly object _gate = new object();

        private string _releaseId = "";
        private string _ticket = "";
        private string _baseUrl = "";
        private DateTime _expiresAtUtc = DateTime.MinValue;
        private bool _installed;
        private Func<IResourceLocation, string> _previousTransform;

        /// <summary>Builds a provider.</summary>
        /// <param name="client">Used to refresh access material. Required.</param>
        /// <param name="clock">UTC clock, overridable so expiry is testable without waiting.</param>
        /// <exception cref="ArgumentNullException">The client is null.</exception>
        public ReleaseAccessProvider(IContentReleaseClient client, Func<DateTime> clock = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>The release the current ticket is scoped to, or empty.</summary>
        public string ReleaseId { get { lock (_gate) return _releaseId; } }

        /// <summary>True when a usable, unexpired ticket is held.</summary>
        public bool HasTicket
        {
            get { lock (_gate) return !string.IsNullOrEmpty(_ticket) && _clock() < _expiresAtUtc; }
        }

        /// <summary>True when the ticket is absent, expired, or inside <see cref="RefreshSkew"/>.</summary>
        public bool NeedsRefresh
        {
            get { lock (_gate) return string.IsNullOrEmpty(_ticket) || _clock() + RefreshSkew >= _expiresAtUtc; }
        }

        /// <summary>
        /// Adopts access material for a release.
        /// </summary>
        /// <param name="releaseId">The release the material is scoped to.</param>
        /// <param name="access">The material returned by the server.</param>
        /// <returns>A contract reason on rejection, or null when adopted.</returns>
        public string Bind(string releaseId, ContentReleaseDescriptor.AccessMaterial access)
        {
            if (string.IsNullOrEmpty(releaseId)) return ContentReleaseReason.NoRelease;
            if (access == null) return ContentReleaseReason.AccessModeUnsupported;

            if (!string.Equals(access.mode, ContentAccessMode.Gateway, StringComparison.Ordinal))
                return ContentReleaseReason.AccessModeUnsupported;
            if (string.IsNullOrEmpty(access.ticket))
                return ContentReleaseReason.TicketScopeInvalid;

            // Refuse a base URL that is not HTTPS. Production rejects plain HTTP outright (§6.3);
            // loopback is allowed so a local server can be developed against.
            string baseUrl = (access.baseUrl ?? "").TrimEnd('/');
            if (!string.IsNullOrEmpty(baseUrl))
            {
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
                    return ContentReleaseReason.TicketScopeInvalid;
            }

            lock (_gate)
            {
                _releaseId = releaseId;
                _ticket = access.ticket;
                _baseUrl = baseUrl;
                _expiresAtUtc = access.ExpiresAtUtc;
            }
            return null;
        }

        /// <summary>
        /// Refreshes the ticket when it is stale, leaving the existing one in place on failure.
        /// </summary>
        /// <remarks>
        /// A failed refresh is not fatal by itself: the current ticket may still have minutes left,
        /// and the caller can retry on the next loop. What must not happen is discarding a working
        /// ticket because a refresh failed, which would turn a transient network blip into a stalled
        /// activation.
        /// </remarks>
        /// <param name="cancellationToken">Cancels the refresh.</param>
        /// <returns>True when a usable ticket is held afterwards.</returns>
        public async Awaitable<bool> EnsureFreshAsync(CancellationToken cancellationToken = default)
        {
            string releaseId;
            lock (_gate)
            {
                if (string.IsNullOrEmpty(_releaseId)) return false;
                if (!NeedsRefresh) return true;
                releaseId = _releaseId;
            }

            var response = await _client.RequestAccessAsync(releaseId, cancellationToken);
            if (!response.Success)
            {
                Debug.LogWarning($"[ContentRelease] Access refresh failed ({response.Reason}); keeping the current ticket.");
                return HasTicket;
            }

            string rejection = Bind(releaseId, response.Value);
            if (rejection != null)
            {
                Debug.LogWarning($"[ContentRelease] Refreshed access material was rejected ({rejection}).");
                return HasTicket;
            }
            return true;
        }

        /// <summary>
        /// Appends the ticket to an object-route URL, and leaves every other URL untouched.
        /// </summary>
        /// <remarks>
        /// Scoped to <c>baseUrl</c> deliberately. Addressables resolves plenty of internal ids that
        /// are not gateway objects — local paths, StreamingAssets, other hosts — and appending a
        /// credential to any of them would leak it somewhere it was never meant to go.
        /// </remarks>
        /// <param name="internalId">The internal id Addressables is about to request.</param>
        /// <returns>The id to actually request.</returns>
        public string Transform(string internalId)
        {
            if (string.IsNullOrEmpty(internalId)) return internalId;

            string ticket, baseUrl;
            lock (_gate) { ticket = _ticket; baseUrl = _baseUrl; }

            if (string.IsNullOrEmpty(ticket) || string.IsNullOrEmpty(baseUrl)) return internalId;
            if (!internalId.StartsWith(baseUrl, StringComparison.Ordinal)) return internalId;
            // Already carries one -- a re-request of an id we transformed earlier.
            if (internalId.IndexOf("ticket=", StringComparison.Ordinal) >= 0) return internalId;

            char separator = internalId.IndexOf('?') >= 0 ? '&' : '?';
            return $"{internalId}{separator}ticket={UnityWebRequestEscape(ticket)}";
        }

        /// <summary>
        /// Installs <see cref="Transform"/> as the Addressables internal id transform.
        /// </summary>
        /// <remarks>
        /// Addressables exposes exactly one transform slot, so the previous one is captured and
        /// chained rather than dropped. Silently replacing it would break whatever installed it —
        /// most likely a CDN signer or a platform path fixup — in a way that looks like a content
        /// bug, and only for the packages that other system owned.
        /// </remarks>
        public void Install()
        {
            if (_installed) return;
            _previousTransform = Addressables.InternalIdTransformFunc;
            Addressables.InternalIdTransformFunc = location =>
            {
                string id = _previousTransform != null ? _previousTransform(location) : location?.InternalId;
                return Transform(id);
            };
            _installed = true;
        }

        /// <summary>Restores the transform that was installed before this provider.</summary>
        public void Uninstall()
        {
            if (!_installed) return;
            Addressables.InternalIdTransformFunc = _previousTransform;
            _previousTransform = null;
            _installed = false;
        }

        /// <summary>Clears the held ticket without touching the installed transform.</summary>
        public void ClearTicket()
        {
            lock (_gate)
            {
                _ticket = "";
                _expiresAtUtc = DateTime.MinValue;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Uninstall();
            ClearTicket();
            lock (_gate) { _releaseId = ""; _baseUrl = ""; }
        }

        // UnityWebRequest.EscapeURL is not available to every assembly this type may be linked into,
        // and the ticket is a compact token whose alphabet is nearly URL-safe already. Escaping the
        // handful of characters that are not is enough and keeps this allocation-light.
        private static string UnityWebRequestEscape(string value) =>
            Uri.EscapeDataString(value);
    }
}
