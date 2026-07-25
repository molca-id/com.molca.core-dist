using System;
using System.Threading;
using Molca.Editor.Licensing;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// Authenticated, host-pinned client for the Molca control-plane catalog and signed manifests.
    /// Authentication reuses the cached developer entitlement; Google access tokens are never persisted.
    /// </summary>
    internal sealed class AddonCatalogClient
    {
        private const int RequestTimeoutSeconds = 30;

        /// <summary>Fetches add-ons entitled and compatible with this Core/editor runtime.</summary>
        internal async Awaitable<AddonOperationResult<AddonCatalogResponse>> GetCatalogAsync(
            string channel = "stable", CancellationToken cancellationToken = default)
        {
            if (!TryGetEntitlement(out string token, out string entitlementError))
                return AddonOperationResult<AddonCatalogResponse>.Fail(entitlementError);
            string coreVersion = AddonDistributionConfig.CoreVersion();
            if (string.IsNullOrWhiteSpace(coreVersion))
                return AddonOperationResult<AddonCatalogResponse>.Fail("Could not resolve the installed Core version.");

            string url = AddonDistributionConfig.CatalogUrl(coreVersion, AddonDistributionConfig.EditorRuntime(), channel);
            var response = await GetAsync(url, token, cancellationToken);
            if (!response.Success) return AddonOperationResult<AddonCatalogResponse>.Fail(response.Error);

            try
            {
                var catalog = JsonUtility.FromJson<AddonCatalogResponse>(response.Value);
                if (catalog == null || catalog.schemaVersion != AddonDistributionConfig.CatalogSchemaVersion)
                    return AddonOperationResult<AddonCatalogResponse>.Fail("Catalog schema is unsupported.");
                catalog.packs ??= Array.Empty<AddonCatalogPackage>();
                return AddonOperationResult<AddonCatalogResponse>.Ok(catalog);
            }
            catch (Exception exception)
            {
                return AddonOperationResult<AddonCatalogResponse>.Fail($"Could not parse add-on catalog: {exception.Message}");
            }
        }

        /// <summary>Fetches and cryptographically verifies one exact add-on manifest.</summary>
        internal async Awaitable<AddonOperationResult<VerifiedAddonManifest>> GetManifestAsync(
            string id, string version, CancellationToken cancellationToken = default)
        {
            if (!TryGetEntitlement(out string token, out string entitlementError))
                return AddonOperationResult<VerifiedAddonManifest>.Fail(entitlementError);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                return AddonOperationResult<VerifiedAddonManifest>.Fail("Add-on id and version are required.");

            var response = await GetAsync(AddonDistributionConfig.ManifestUrl(id, version), token, cancellationToken);
            if (!response.Success) return AddonOperationResult<VerifiedAddonManifest>.Fail(response.Error);

            AddonManifestResponse envelope;
            try { envelope = JsonUtility.FromJson<AddonManifestResponse>(response.Value); }
            catch (Exception exception)
            { return AddonOperationResult<VerifiedAddonManifest>.Fail($"Could not parse manifest response: {exception.Message}"); }
            if (envelope == null || envelope.schemaVersion != AddonDistributionConfig.CatalogSchemaVersion)
                return AddonOperationResult<VerifiedAddonManifest>.Fail("Manifest response schema is unsupported.");
            if (!AddonManifestVerifier.TryVerify(envelope.manifestToken, id, version, out var manifest, out string error))
                return AddonOperationResult<VerifiedAddonManifest>.Fail(error);
            return AddonOperationResult<VerifiedAddonManifest>.Ok(manifest);
        }

        /// <summary>
        /// Requests a build token so a player build can report runtime usage without shipping a
        /// developer credential. Returns null when offline or unlicensed; the caller treats that as
        /// "this build simply will not report", never as a build failure.
        /// </summary>
        internal async Awaitable<AddonOperationResult<BuildTokenResponse>> RequestBuildTokenAsync(
            string coreVersion, string appVersion, CancellationToken cancellationToken = default)
        {
            if (!TryGetEntitlement(out string token, out string entitlementError))
                return AddonOperationResult<BuildTokenResponse>.Fail(entitlementError);

            string url = DevLicenseConfig.ServerBaseUrl.TrimEnd('/') + "/builds/token";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !AddonDistributionConfig.IsTrustedDownloadHost(uri.Host))
                return AddonOperationResult<BuildTokenResponse>.Fail("Build token URL is not on the pinned HTTPS host allowlist.");

            string json = JsonUtility.ToJson(new BuildTokenRequest { coreVersion = coreVersion, appVersion = appVersion });
            using var request = new UnityWebRequest(uri.AbsoluteUri, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = RequestTimeoutSeconds,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + token);
            request.SetRequestHeader("X-Molca-Machine-Id", SystemInfo.deviceUniqueIdentifier);

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(cancellationToken);
            }
            if (request.result != UnityWebRequest.Result.Success)
                return AddonOperationResult<BuildTokenResponse>.Fail(
                    $"Build token request failed: HTTP {request.responseCode} {request.error}");
            try
            {
                var response = JsonUtility.FromJson<BuildTokenResponse>(request.downloadHandler.text);
                return response != null && !string.IsNullOrEmpty(response.buildToken)
                    ? AddonOperationResult<BuildTokenResponse>.Ok(response)
                    : AddonOperationResult<BuildTokenResponse>.Fail("Build token response was empty.");
            }
            catch (Exception exception)
            {
                return AddonOperationResult<BuildTokenResponse>.Fail($"Could not parse build token: {exception.Message}");
            }
        }

        [Serializable]
        private sealed class BuildTokenRequest
        {
            public string coreVersion;
            public string appVersion;
        }

        /// <summary>Signed, revocable claim identifying the licensee a player build belongs to.</summary>
        [Serializable]
        internal sealed class BuildTokenResponse
        {
            public string buildToken;
            public string buildId;
            public string expiresAt;
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
                DevLicenseStatus.Missing => "Sign in with an entitled developer account before browsing add-ons.",
                DevLicenseStatus.Expired => "The developer entitlement expired. Sign in again.",
                DevLicenseStatus.WrongMachine => "The developer entitlement belongs to another machine.",
                _ => "The stored developer entitlement is invalid.",
            };
            return false;
        }

        private static async Awaitable<AddonOperationResult<string>> GetAsync(
            string url, string entitlementToken, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !AddonDistributionConfig.IsTrustedDownloadHost(uri.Host))
                return AddonOperationResult<string>.Fail("Add-on API URL is not on the pinned HTTPS host allowlist.");

            using var request = UnityWebRequest.Get(uri.AbsoluteUri);
            request.SetRequestHeader("Authorization", "Bearer " + entitlementToken);
            request.SetRequestHeader("X-Molca-Machine-Id", SystemInfo.deviceUniqueIdentifier);
            request.timeout = RequestTimeoutSeconds;
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(cancellationToken);
            }

            string body = request.downloadHandler?.text ?? string.Empty;
            if (request.result == UnityWebRequest.Result.Success)
                return AddonOperationResult<string>.Ok(body);
            string detail = request.responseCode switch
            {
                401 => "Developer entitlement was rejected or expired.",
                403 => "This developer or machine is not entitled to that add-on.",
                404 => "The requested add-on version is unavailable.",
                _ => $"Add-on server returned HTTP {request.responseCode}: {request.error}",
            };
            return AddonOperationResult<string>.Fail(detail);
        }
    }
}
