using UnityEngine;

namespace Molca.ContentPackage
{
    /// <summary>
    /// Editor-time build and deployment configuration for the content package system.
    /// Create one asset per project via <b>Assets &gt; Create &gt; Molca &gt; Content Package &gt; Build Config</b>
    /// and assign it in the Content Package Manager's Build &amp; Deploy panel.
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-content.png")]
    [CreateAssetMenu(fileName = "ContentPackageBuildConfig",
                     menuName  = "Molca/Content Package/Build Config", order = 50)]
    public class ContentPackageBuildConfig : ScriptableObject
    {
        // ── Addressables paths ───────────────────────────────────────────────

        /// <summary>
        /// Local folder where Addressables writes bundles and the catalog.
        /// Corresponds to the <c>RemoteBuildPath</c> profile variable.
        /// Use <c>[BuildTarget]</c> to insert the platform name automatically.
        /// </summary>
        [Tooltip("Local output folder for built bundles. Use [BuildTarget] token.")]
        public string localBuildPath = "ServerData/[BuildTarget]";

        /// <summary>
        /// Runtime URL from which the application loads remote content.
        /// Corresponds to the <c>RemoteLoadPath</c> profile variable.
        /// Must match the public URL of your storage bucket/CDN path.
        /// </summary>
        [Tooltip("Runtime URL the app uses to load remote bundles. Must match your CDN/bucket public URL.")]
        public string remoteLoadURL = "https://your-bucket.s3.amazonaws.com/content/[BuildTarget]";

        // ── Storage provider ─────────────────────────────────────────────────

        /// <summary>
        /// The storage backend used to deploy build artifacts.
        /// Assign an <see cref="ContentPackageStorageProvider"/> asset (e.g. <c>AWSS3StorageProvider</c>).
        /// </summary>
        [Tooltip("Storage backend for deployment. Create a provider asset via Molca > Content Package > Storage > …")]
        public ContentPackageStorageProvider storageProvider;

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves <see cref="localBuildPath"/> with the current build target token substituted.
        /// </summary>
        public string ResolvedLocalBuildPath(string buildTarget)
            => localBuildPath.Replace("[BuildTarget]", buildTarget);

        /// <summary>
        /// Derives the public URL of <c>packages.json</c> from <see cref="remoteLoadURL"/>.
        /// Returns an empty string if <see cref="remoteLoadURL"/> is not configured.
        /// </summary>
        public string GetPackagesManifestUrl(string buildTarget)
        {
            if (string.IsNullOrEmpty(remoteLoadURL)) return "";
            return ResolvedRemoteUrl(buildTarget) + "/packages.json";
        }

        /// <summary>
        /// Derives the public URL of the Addressables catalog file given its filename on disk.
        /// Returns an empty string if <see cref="remoteLoadURL"/> is not configured.
        /// </summary>
        public string GetCatalogUrl(string buildTarget, string catalogFileName)
        {
            if (string.IsNullOrEmpty(remoteLoadURL) || string.IsNullOrEmpty(catalogFileName)) return "";
            return ResolvedRemoteUrl(buildTarget) + "/" + catalogFileName;
        }

        private string ResolvedRemoteUrl(string buildTarget)
            => remoteLoadURL.Replace("[BuildTarget]", buildTarget).TrimEnd('/');
    }
}
