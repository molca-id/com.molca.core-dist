using System;
using UnityEditor;
using UnityEngine;
using Molca.Licensing;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Writes and removes the generated <c>Assets/Resources/MolcaLicenseStamp.json</c> that carries the
    /// licensee identity into a player build for <see cref="LicenseHeartbeat"/> to emit at runtime.
    /// Written during pre-process (so it is packaged) by <see cref="LicenseBuildGate"/> once the build
    /// is authorized, and deleted during post-process.
    /// </summary>
    /// <remarks>
    /// Mirrors the framework's <c>BuildInfoAsset</c> lifecycle. State is held statically; a build runs
    /// both callbacks within one domain, so no reload intervenes. Best-effort — a failure here is logged
    /// and never fails the build.
    /// </remarks>
    internal static class LicenseBuildStamp
    {
        private const string ResourcesFolder = "Assets/Resources";
        private const string AssetPath = "Assets/Resources/MolcaLicenseStamp.json";

        private static bool _createdResourcesFolder;

        /// <summary>Writes the license stamp and imports it so the build includes it.</summary>
        /// <param name="licenseeId">The authorized licensee identity.</param>
        /// <param name="coreVersion">The Core version recorded in the entitlement.</param>
        /// <param name="buildToken">
        /// Signed build token authorizing runtime usage reporting, or null/empty when the build machine
        /// could not reach the control plane. Empty simply disables reporting for this build.
        /// </param>
        /// <param name="buildId">Server-side id of the minted build token, or null.</param>
        /// <param name="appVersion">Player application version at build time.</param>
        public static void Write(string licenseeId, string coreVersion,
            string buildToken = null, string buildId = null, string appVersion = null)
        {
            try
            {
                var data = new LicenseStampData
                {
                    licenseeId = licenseeId,
                    coreVersion = coreVersion,
                    stampedAtUtc = DateTime.UtcNow.ToString("o"),
                    buildToken = buildToken ?? string.Empty,
                    buildId = buildId ?? string.Empty,
                    appVersion = appVersion ?? string.Empty,
                    // Runtime assemblies cannot reference editor-only configuration, so the endpoint
                    // travels with the stamp. Only written alongside a token that can authenticate to it.
                    serverBaseUrl = string.IsNullOrEmpty(buildToken) ? string.Empty : DevLicenseConfig.ServerBaseUrl,
                };

                _createdResourcesFolder = !AssetDatabase.IsValidFolder(ResourcesFolder);
                if (_createdResourcesFolder)
                    AssetDatabase.CreateFolder("Assets", "Resources");

                System.IO.File.WriteAllText(AssetPath, JsonUtility.ToJson(data, true));
                AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[License] Failed to write build stamp: {e.Message}");
            }
        }

        /// <summary>Deletes the generated stamp (and the Resources folder if this writer created it and it is now empty).</summary>
        public static void Cleanup()
        {
            try
            {
                if (System.IO.File.Exists(AssetPath) || AssetDatabase.LoadAssetAtPath<TextAsset>(AssetPath) != null)
                    AssetDatabase.DeleteAsset(AssetPath);

                if (_createdResourcesFolder && AssetDatabase.IsValidFolder(ResourcesFolder))
                {
                    var remaining = AssetDatabase.FindAssets(string.Empty, new[] { ResourcesFolder });
                    if (remaining == null || remaining.Length == 0)
                        AssetDatabase.DeleteAsset(ResourcesFolder);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[License] Failed to clean up build stamp: {e.Message}");
            }
            finally
            {
                _createdResourcesFolder = false;
            }
        }
    }
}
