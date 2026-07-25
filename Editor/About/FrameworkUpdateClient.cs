using System;
using System.Threading;
using Molca.Editor.Addons;
using Molca.Editor.Licensing;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Editor.About
{
    /// <summary>
    /// Authenticated, host-pinned client for the control plane's framework update feed.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/About/</c>. Deliberately the same trust shape as
    /// <see cref="AddonCatalogClient"/>: the cached developer entitlement authenticates the request, the URL
    /// must be HTTPS on a pinned host, and the machine id travels in a header. Reusing that shape is the
    /// reason this feed lives on the control plane rather than a package registry or a code-host API — there
    /// is exactly one trust root in the editor.
    /// Editor-only; the returned <see cref="Awaitable"/> resumes on the main thread.
    /// </remarks>
    internal sealed class FrameworkUpdateClient
    {
        /// <summary>Wire schema this client speaks; a mismatch is fatal rather than tolerated.</summary>
        internal const int SchemaVersion = 1;

        private const int RequestTimeoutSeconds = 20;

        /// <summary>
        /// Fetches the newest Core release visible to this license.
        /// </summary>
        /// <param name="channel">Requested pre-release channel; the server caps it at the license ceiling.</param>
        /// <param name="cancellationToken">Cancels the in-flight request (the About section detaching).</param>
        /// <returns>The feed response, or a failure carrying a message fit to show in the panel.</returns>
        internal async Awaitable<AddonOperationResult<FrameworkUpdateResponse>> GetLatestAsync(
            string channel, CancellationToken cancellationToken = default)
        {
            if (!TryGetEntitlement(out string token, out string entitlementError))
                return AddonOperationResult<FrameworkUpdateResponse>.Fail(entitlementError);

            string coreVersion = FrameworkVersionInfo.CoreVersion;
            if (string.IsNullOrWhiteSpace(coreVersion))
                return AddonOperationResult<FrameworkUpdateResponse>.Fail(
                    "Could not resolve the installed Core version, so there is nothing to compare against.");

            string url = LatestUrl(coreVersion, channel, FrameworkVersionInfo.UnityVersion);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !AddonDistributionConfig.IsTrustedDownloadHost(uri.Host))
                return AddonOperationResult<FrameworkUpdateResponse>.Fail(
                    "Update feed URL is not on the pinned HTTPS host allowlist.");

            using var request = UnityWebRequest.Get(uri.AbsoluteUri);
            request.SetRequestHeader("Authorization", "Bearer " + token);
            request.SetRequestHeader("X-Molca-Machine-Id", SystemInfo.deviceUniqueIdentifier);
            request.timeout = RequestTimeoutSeconds;

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(cancellationToken);
            }

            if (request.result != UnityWebRequest.Result.Success)
                return AddonOperationResult<FrameworkUpdateResponse>.Fail(Describe(request));

            return TryParse(request.downloadHandler?.text, out var response, out string parseError)
                ? AddonOperationResult<FrameworkUpdateResponse>.Ok(response)
                : AddonOperationResult<FrameworkUpdateResponse>.Fail(parseError);
        }

        /// <summary>Builds the feed URL for a given project and editor.</summary>
        /// <param name="coreVersion">Installed Core version.</param>
        /// <param name="channel">Requested channel.</param>
        /// <param name="unityVersion">Running editor version, so the server can report Unity compatibility.</param>
        internal static string LatestUrl(string coreVersion, string channel, string unityVersion) =>
            $"{DevLicenseConfig.ServerBaseUrl.TrimEnd('/')}/framework/releases/latest" +
            $"?coreVersion={Uri.EscapeDataString(coreVersion ?? string.Empty)}" +
            $"&channel={Uri.EscapeDataString(string.IsNullOrEmpty(channel) ? AddonChannels.Stable : channel)}" +
            $"&unityVersion={Uri.EscapeDataString(unityVersion ?? string.Empty)}";

        /// <summary>
        /// Parses a feed response, rejecting an unsupported schema.
        /// </summary>
        /// <param name="json">Raw response body.</param>
        /// <param name="response">The parsed response on success; otherwise <c>null</c>.</param>
        /// <param name="error">A message fit for the panel when this returns <c>false</c>.</param>
        /// <remarks>
        /// Separated from the request so the parse contract is testable without a server. A schema mismatch
        /// is fatal for the same reason it is in the add-on catalog: a client reading a payload it does not
        /// understand would render confident nonsense about which version to install.
        /// </remarks>
        internal static bool TryParse(string json, out FrameworkUpdateResponse response, out string error)
        {
            response = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "The update service returned an empty response.";
                return false;
            }

            try { response = JsonUtility.FromJson<FrameworkUpdateResponse>(json); }
            catch (Exception exception)
            {
                error = $"Could not parse the update response: {exception.Message}";
                return false;
            }

            if (response == null)
            {
                error = "Could not parse the update response.";
                return false;
            }

            if (response.schemaVersion != SchemaVersion)
            {
                int received = response.schemaVersion;
                response = null;
                error = $"The update service speaks schema v{received}; this Core reads v{SchemaVersion}. " +
                        "Update Molca to read the feed.";
                return false;
            }

            return true;
        }

        private static bool TryGetEntitlement(out string token, out string error)
        {
            token = DevEntitlementStore.LoadEffective();
            var status = DevEntitlementVerifier.Evaluate(token, SystemInfo.deviceUniqueIdentifier, out _);
            if (status == DevLicenseStatus.Valid)
            {
                error = null;
                return true;
            }

            error = status switch
            {
                DevLicenseStatus.Missing => "Sign in with an entitled developer account to check for updates.",
                DevLicenseStatus.Expired => "The developer entitlement expired. Sign in again to check for updates.",
                DevLicenseStatus.WrongMachine => "The developer entitlement belongs to another machine.",
                _ => "The stored developer entitlement is invalid.",
            };
            return false;
        }

        private static string Describe(UnityWebRequest request) => request.responseCode switch
        {
            401 => "Developer entitlement was rejected or expired.",
            403 => "This developer or machine is not entitled to the update feed.",
            0 => "Couldn't reach the update service.",
            _ => $"The update service returned HTTP {request.responseCode}: {request.error}",
        };
    }
}
