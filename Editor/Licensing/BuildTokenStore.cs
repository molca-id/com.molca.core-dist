using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Molca.Editor.Addons;
using Molca.Editor.Projects;
using UnityEngine;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Obtains the signed, project-scoped build token baked into player builds so shipped players can report
    /// framework usage. The latest response is recorded under <c>Library/Molca/build-token.json</c> for
    /// diagnostics, but every build mints a fresh token so current access and revocation are enforced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token authorizes appending usage events for one licensee — it is not a developer credential
    /// and carries no machine identity. Each successful build receives its own revocable token.
    /// </para>
    /// <para>
    /// Deliberately synchronous: <see cref="LicenseBuildGate"/> is an
    /// <c>IPreprocessBuildWithReport</c> callback with no async seam. The request is bounded by a short
    /// timeout. Project-connected builds require a successful online authorization.
    /// </para>
    /// </remarks>
    internal static class BuildTokenStore
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        [Serializable]
        private sealed class BuildTokenRequest
        {
            public string coreVersion;
            public string appVersion;
            public string projectBinding;
        }

        [Serializable]
        private sealed class CachedToken
        {
            public string licenseeId;
            public string buildToken;
            public string buildId;
            public string expiresAt;
            public string projectId;
            public string projectBindingId;
        }

        /// <summary>
        /// Returns a freshly minted build token for the connected project.
        /// </summary>
        /// <returns>The token and its build id, or <c>(null, null)</c> when none could be obtained.</returns>
        internal static (string token, string buildId) Acquire(string licenseeId, string coreVersion, string appVersion)
        {
            string projectBinding = MolcaProjectSettings.Instance?.ProjectBinding;
            string projectId = null;
            string projectBindingId = null;
            if (!string.IsNullOrWhiteSpace(projectBinding))
            {
                var settings = MolcaProjectSettings.Instance;
                if (!ProjectBindingVerifier.TryVerify(projectBinding, settings.ProjectId, settings.ProjectCode,
                        licenseeId, out var bindingPayload, out var bindingError))
                {
                    Debug.LogWarning($"[License] Runtime usage reporting is disabled for this build: {bindingError}");
                    return (null, null);
                }
                projectId = bindingPayload.projectId;
                projectBindingId = bindingPayload.bindingId;
            }

            try
            {
                CachedToken minted = Mint(coreVersion, appVersion, projectBinding);
                if (minted == null) return (null, null);
                minted.licenseeId = licenseeId;
                minted.projectId = projectId;
                minted.projectBindingId = projectBindingId;
                Save(minted);
                return (minted.buildToken, minted.buildId);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[License] Runtime usage reporting is disabled for this build: {exception.Message}");
                return (null, null);
            }
        }

        private static CachedToken Mint(string coreVersion, string appVersion, string projectBinding)
        {
            string entitlement = DevEntitlementStore.LoadEffective();
            if (DevEntitlementVerifier.Evaluate(entitlement, SystemInfo.deviceUniqueIdentifier, out _) != DevLicenseStatus.Valid)
                return null;

            string url = DevLicenseConfig.ServerBaseUrl.TrimEnd('/') + "/builds/token";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !AddonDistributionConfig.IsTrustedDownloadHost(uri.Host))
                return null;

            string body = JsonUtility.ToJson(new BuildTokenRequest
            {
                coreVersion = coreVersion,
                appVersion = appVersion,
                projectBinding = projectBinding,
            });
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
