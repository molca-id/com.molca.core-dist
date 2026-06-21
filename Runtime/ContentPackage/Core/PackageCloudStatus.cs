using System;

namespace Molca.ContentPackage.Core
{
    /// <summary>
    /// Describes the connectivity state between the application and the remote content CDN.
    /// Updated on every <see cref="Services.PackageService.RefreshCatalogAsync"/> attempt.
    /// </summary>
    public enum CloudConnectionState
    {
        /// <summary>
        /// No catalog refresh has been attempted yet this session.
        /// </summary>
        Unknown,

        /// <summary>
        /// The remote <c>packages.json</c> manifest was fetched successfully on the last attempt.
        /// </summary>
        Connected,

        /// <summary>
        /// The last fetch attempt failed (network error, HTTP error, or malformed response).
        /// The <see cref="PackageCloudStatus.ErrorMessage"/> field contains the reason.
        /// </summary>
        Unreachable,

        /// <summary>
        /// <see cref="ContentPackageSettings.RemotePackagesManifestUrl"/> is not configured.
        /// No fetch is attempted until a URL is set.
        /// </summary>
        NotConfigured
    }

    /// <summary>
    /// Snapshot of the cloud connectivity state for the content package system.
    /// Exposed via <see cref="Services.PackageService.CloudStatus"/> and updated
    /// each time <see cref="Services.PackageService.RefreshCatalogAsync"/> runs.
    /// </summary>
    public class PackageCloudStatus
    {
        /// <summary>
        /// Result of the last remote manifest fetch attempt.
        /// </summary>
        public CloudConnectionState State { get; internal set; } = CloudConnectionState.Unknown;

        /// <summary>
        /// UTC time of the last <em>successful</em> manifest fetch. <c>null</c> if never
        /// successfully fetched this session.
        /// </summary>
        public DateTime? LastSyncTime { get; internal set; }

        /// <summary>
        /// The <c>generatedAt</c> timestamp from the remote <c>packages.json</c>, indicating
        /// when the CDN content was last built and deployed. <c>null</c> until a successful fetch.
        /// </summary>
        public string ManifestGeneratedAt { get; internal set; }

        /// <summary>
        /// Number of packages reported in the remote manifest. <c>0</c> until a successful fetch.
        /// </summary>
        public int RemotePackageCount { get; internal set; }

        /// <summary>
        /// Human-readable error from the last failed fetch. <c>null</c> when
        /// <see cref="State"/> is <see cref="CloudConnectionState.Connected"/> or
        /// <see cref="CloudConnectionState.Unknown"/>.
        /// </summary>
        public string ErrorMessage { get; internal set; }

        /// <summary>
        /// Returns a shallow copy of this snapshot. Use when passing to event subscribers
        /// so they receive a stable value rather than a reference to the live mutable object.
        /// </summary>
        internal PackageCloudStatus Clone() => new PackageCloudStatus
        {
            State               = State,
            LastSyncTime        = LastSyncTime,
            ManifestGeneratedAt = ManifestGeneratedAt,
            RemotePackageCount  = RemotePackageCount,
            ErrorMessage        = ErrorMessage,
        };
    }
}
