using System;

namespace Molca.ContentPackage.Core
{
    /// <summary>
    /// Represents the runtime state of a content package, including download progress,
    /// installation status, and error information. This class is serializable for JSON persistence.
    /// </summary>
    [Serializable]
    public class PackageState
    {
        /// <summary>
        /// The unique identifier of the package.
        /// </summary>
        public string packageId;

        /// <summary>
        /// The current status of the package.
        /// </summary>
        public PackageStatus status;

        /// <summary>
        /// The download progress as a value between 0.0 and 1.0.
        /// Only meaningful when status is Downloading.
        /// </summary>
        public float downloadProgress;

        /// <summary>
        /// The number of bytes downloaded so far.
        /// Only meaningful when status is Downloading.
        /// </summary>
        public long downloadedBytes;

        /// <summary>
        /// The total number of bytes to download.
        /// Only meaningful when status is Downloading.
        /// </summary>
        public long totalBytes;

        /// <summary>
        /// The error message if the package is in Failed state.
        /// Null or empty if no error occurred.
        /// </summary>
        public string errorMessage;

        /// <summary>
        /// ISO 8601 timestamp when this state was last modified.
        /// Stored as string because <see cref="UnityEngine.JsonUtility"/> does not serialize <see cref="DateTime"/>.
        /// </summary>
        public string lastModified;

        /// <summary>
        /// The version of the package that is currently installed.
        /// Only meaningful when status is Installed or UpdateAvailable.
        /// </summary>
        public string installedVersion;

        /// <summary>
        /// Approximate on-disk size of this package's downloaded bundles, in bytes, captured at
        /// install time. Used for cache-budget accounting and LRU eviction. 0 when unknown.
        /// </summary>
        public long installedSizeBytes;

        /// <summary>
        /// Initializes a new instance of the PackageState class with the specified package ID.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package.</param>
        public PackageState(string packageId)
        {
            this.packageId = packageId;
            this.status = PackageStatus.Available;
            this.downloadProgress = 0f;
            this.downloadedBytes = 0;
            this.totalBytes = 0;
            this.errorMessage = null;
            this.lastModified = DateTime.UtcNow.ToString("O");
            this.installedVersion = null;
            this.installedSizeBytes = 0;
        }

        /// <summary>
        /// Gets a value indicating whether the package is currently installed.
        /// </summary>
        public bool IsInstalled => status == PackageStatus.Installed;

        /// <summary>
        /// Gets a value indicating whether the package is currently downloading.
        /// </summary>
        public bool IsDownloading => status == PackageStatus.Downloading;

        /// <summary>
        /// Gets a value indicating whether the package has encountered an error.
        /// </summary>
        public bool HasError => status == PackageStatus.Failed;

        /// <summary>
        /// Gets a value indicating whether the package has an update available.
        /// </summary>
        public bool HasUpdate => status == PackageStatus.UpdateAvailable;
    }
}