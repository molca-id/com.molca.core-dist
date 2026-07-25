using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Molca.Editor.Addons;
using UnityEngine;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Obtains and caches the signed build token baked into player builds so shipped players can report
    /// framework usage. Cached under <c>Library/Molca/build-token.json</c> and reused until it nears
    /// expiry, so a normal build performs no network call at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token authorizes appending usage events for one licensee — it is not a developer credential
    /// and carries no machine identity. One token is reused across this project's builds, so dashboard
    /// revocation stops reporting for the project rather than for a single artifact.
    /// </para>
    /// <para>
    /// Deliberately synchronous: <see cref="LicenseBuildGate"/> is an
    /// <c>IPreprocessBuildWithReport</c> callback with no async seam. The request is bounded by a short
    /// timeout and every failure is soft — an offline build simply ships without runtime reporting.
    /// </para>
    /// </remarks>
    internal static class BuildTokenStore
    {
        private static readonly TimeSpan RenewWithin = TimeSpan.FromDays(30);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        [Serializable]
        private sealed class BuildTokenRequest
        {
            public string coreVersion;
            public string appVersion;
        }

        [Serializable]
        private sealed class CachedToken
        {
            public string licenseeId;
            public string buildToken;
            public string buildId;
            public string expiresAt;
        }

        /// <summary>
        /// Returns a usable build token for <paramref name="licenseeId"/>, minting one when the cache is
        /// missing, stale, or belongs to another licensee.
        /// </summary>
        /// <returns>The token and its build id, or <c>(null, null)</c> when none could be obtained.</returns>
        internal static (string token, string buildId) Acquire(string licenseeId, string coreVersion, string appVersion)
        {
            CachedToken cached = Load();
            if (cached != null && cached.licenseeId == licenseeId && !string.IsNullOrEmpty(cached.buildToken) &&
                DateTime.TryParse(cached.expiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expires) &&
                expires - DateTime.UtcNow > RenewWithin)
                return (cached.buildToken, cached.buildId);

            try
            {
                CachedToken minted = Mint(coreVersion, appVersion);
                if (minted == null) return (cached?.buildToken, cached?.buildId); // Keep an older token over none.
                minted.licenseeId = licenseeId;
                Save(minted);
                return (minted.buildToken, minted.buildId);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[License] Runtime usage reporting is disabled for this build: {exception.Message}");
                return (cached?.buildToken, cached?.buildId);
            }
        }

        private static CachedToken Mint(string coreVersion, string appVersion)
        {
            string entitlement = DevEntitlementStore.LoadEffective();
            if (DevEntitlementVerifier.Evaluate(entitlement, SystemInfo.deviceUniqueIdentifier, out _) != DevLicenseStatus.Valid)
                return null;

            string url = DevLicenseConfig.ServerBaseUrl.TrimEnd('/') + "/builds/token";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !AddonDistributionConfig.IsTrustedDownloadHost(uri.Host))
                return null;

            string body = JsonUtility.ToJson(new BuildTokenRequest { coreVersion = coreVersion, appVersion = appVersion });
            string machineId = SystemInfo.deviceUniqueIdentifier; // Unity API: read before leaving the main thread.
            // Task.Run keeps the blocking wait off the editor's synchronization context.
            string response = Task.Run(async () =>
            {
                using var client = new HttpClient { Timeout = RequestTimeout };
                using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + entitlement);
                request.Headers.TryAddWithoutValidation("X-Molca-Machine-Id", machineId);
                using var result = await client.SendAsync(request).ConfigureAwait(false);
                return result.IsSuccessStatusCode ? await result.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
            }).GetAwaiter().GetResult();

            return string.IsNullOrEmpty(response) ? null : JsonUtility.FromJson<CachedToken>(response);
        }

        private static string CachePath =>
            Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? ".", "Library", "Molca", "build-token.json");

        private static CachedToken Load()
        {
            try { return File.Exists(CachePath) ? JsonUtility.FromJson<CachedToken>(File.ReadAllText(CachePath)) : null; }
            catch { return null; }
        }

        private static void Save(CachedToken token)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath) ?? ".");
                File.WriteAllText(CachePath, JsonUtility.ToJson(token));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[License] Could not cache the build token: {exception.Message}");
            }
        }
    }
}
