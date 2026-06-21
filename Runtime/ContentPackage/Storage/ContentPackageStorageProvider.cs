using UnityEngine;

namespace Molca.ContentPackage
{
    /// <summary>
    /// Abstract base for content package storage backends.
    /// Subclass this ScriptableObject to implement a new deploy target (S3, Azure Blob, GCS, etc.).
    /// </summary>
    /// <remarks>
    /// Create assets via <b>Assets &gt; Create &gt; Molca &gt; Content Package &gt; Storage &gt; …</b>
    /// and assign them to <see cref="ContentPackageBuildConfig.storageProvider"/>.
    /// </remarks>
    public abstract class ContentPackageStorageProvider : ScriptableObject
    {
        /// <summary>Human-readable name shown in the Build &amp; Deploy panel.</summary>
        public abstract string DisplayName { get; }

        /// <summary>
        /// CLI executable name (e.g. <c>aws</c>, <c>gsutil</c>, <c>azcopy</c>).
        /// Used as the <c>FileName</c> when launching the deploy process.
        /// </summary>
        public abstract string ExecutableName { get; }

        /// <summary>
        /// Constructs the full argument string passed to <see cref="ExecutableName"/> for deployment.
        /// </summary>
        /// <param name="localPath">Absolute local path to the Addressables build output folder.</param>
        /// <param name="buildTarget">Unity build target string (e.g. <c>StandaloneWindows64</c>).</param>
        public abstract string BuildDeployArguments(string localPath, string buildTarget);

        /// <summary>
        /// Returns the full shell command string for display in the inspector.
        /// Default implementation concatenates <see cref="ExecutableName"/> and <see cref="BuildDeployArguments"/>.
        /// </summary>
        public virtual string BuildDeployCommand(string localPath, string buildTarget)
            => $"{ExecutableName} {BuildDeployArguments(localPath, buildTarget)}";

        /// <summary>
        /// Human-readable description of the remote destination (e.g. the S3 URI).
        /// Shown in the inspector for confirmation before deploying.
        /// </summary>
        /// <param name="buildTarget">Unity build target string.</param>
        public abstract string GetDestinationDescription(string buildTarget);

        /// <summary>
        /// Checks whether the required CLI tool or SDK is present and accessible.
        /// </summary>
        /// <param name="errorMessage">Set to a user-facing error string when the check fails.</param>
        /// <returns><c>true</c> if the provider is ready to deploy.</returns>
        public abstract bool CheckAvailability(out string errorMessage);
    }
}
