namespace Molca.ContentPackage.Core
{
    /// <summary>
    /// Represents the current state of a content package in the DLC system.
    /// </summary>
    public enum PackageStatus
    {
        /// <summary>
        /// The package is not installed but is available for download.
        /// This is the default state for packages that have not been installed yet.
        /// </summary>
        Available,

        /// <summary>
        /// The package is currently being downloaded from the remote server.
        /// Progress information is available during this state.
        /// </summary>
        Downloading,

        /// <summary>
        /// The package has been successfully downloaded and installed.
        /// All content is available for use in the application.
        /// </summary>
        Installed,

        /// <summary>
        /// The package installation or download has failed.
        /// Error information is available to determine the cause of failure.
        /// </summary>
        Failed,

        /// <summary>
        /// The package is installed but a newer version is available for download.
        /// The current version remains functional until updated.
        /// </summary>
        UpdateAvailable
    }
}