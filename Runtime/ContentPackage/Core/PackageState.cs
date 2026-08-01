using System;

namespace Molca.ContentPackage.Core
{
    /// <summary>
    /// What the service is currently doing to a package. Orthogonal to whether the package is
    /// installed: a failed update is <see cref="PackageOperation.None"/> with an error, and the
    /// previous install is still present.
    /// </summary>
    public enum PackageOperation
    {
        /// <summary>No operation is in flight.</summary>
        None,

        /// <summary>First-time install of a package that is not present.</summary>
        Installing,

        /// <summary>Replacing a present install with a newer version.</summary>
        Updating,

        /// <summary>Removing a present install.</summary>
        Uninstalling
    }

    /// <summary>
    /// Represents the runtime state of a content package.
    ///
    /// State is deliberately split into three orthogonal records, because the single-enum model it
    /// replaces could not express the states that actually occur. <c>UpdateAvailable</c> is
    /// installed content, but the enum made it mutually exclusive with <c>Installed</c>, so
    /// <c>IsInstalled</c> returned false for a package the player can still use; dependency
    /// accounting, cache retention, and the UI all inherited that answer. A failed update was worse:
    /// the status moved to <c>Failed</c> and the fact that a working previous version was still on
    /// disk was gone.
    ///
    /// <see cref="Install"/> is presence, <see cref="Operation"/> is what is happening, and
    /// <see cref="Update"/> is what is available. <see cref="status"/> remains as a projection so
    /// existing callers and saved manifests keep working.
    /// </summary>
    [Serializable]
    public class PackageState
    {
        /// <summary>Presence of installed content. Independent of any operation or available update.</summary>
        [Serializable]
        public class InstallRecord
        {
            /// <summary>True when usable content for this package is on disk.</summary>
            public bool isPresent;

            /// <summary>The release this content came from. Empty when unknown (pre-migration installs).</summary>
            public string releaseId;

            /// <summary>
            /// The package version actually installed, taken from the verified release manifest
            /// rather than from the app-baked package definition — those disagree the moment content
            /// ships independently of the app, which is the entire point of remote content.
            /// </summary>
            public string packageVersion;

            /// <summary>On-disk size in bytes at install time. 0 when unknown.</summary>
            public long sizeBytes;

            /// <summary>ISO 8601. String because <see cref="UnityEngine.JsonUtility"/> cannot serialize DateTime.</summary>
            public string installedAt;
        }

        /// <summary>The in-flight operation, if any, and the outcome of the last one.</summary>
        [Serializable]
        public class OperationRecord
        {
            /// <summary>What is currently happening. <see cref="PackageOperation.None"/> when idle.</summary>
            public PackageOperation kind;

            /// <summary>Progress of the current operation, 0..1.</summary>
            public float progress;

            /// <summary>Bytes transferred by the current operation.</summary>
            public long downloadedBytes;

            /// <summary>Total bytes the current operation expects to transfer.</summary>
            public long totalBytes;

            /// <summary>
            /// Why the last operation failed. Survives the operation ending, because a failed update
            /// must report the failure *and* leave the previous install visible.
            /// </summary>
            public string errorMessage;
        }

        /// <summary>A newer version known to be available. Never implies the package is uninstalled.</summary>
        [Serializable]
        public class UpdateRecord
        {
            /// <summary>True when a newer version than <see cref="InstallRecord.packageVersion"/> exists.</summary>
            public bool available;

            /// <summary>The release offering the newer version.</summary>
            public string targetReleaseId;

            /// <summary>The newer package version on offer.</summary>
            public string targetPackageVersion;
        }

        /// <summary>The unique identifier of the package.</summary>
        public string packageId;

        /// <summary>
        /// Projection of the three records onto the legacy enum, for existing callers, saved
        /// manifests, and UI. Recomputed on every mutation; never the source of truth. A package
        /// that is both installed and updatable projects to <see cref="PackageStatus.UpdateAvailable"/>,
        /// which is why <see cref="IsInstalled"/> must not be derived from it.
        /// </summary>
        public PackageStatus status;

        /// <summary>Download progress 0..1. Mirrors <see cref="OperationRecord.progress"/>.</summary>
        public float downloadProgress;

        /// <summary>Bytes downloaded so far. Mirrors <see cref="OperationRecord.downloadedBytes"/>.</summary>
        public long downloadedBytes;

        /// <summary>Total bytes to download. Mirrors <see cref="OperationRecord.totalBytes"/>.</summary>
        public long totalBytes;

        /// <summary>Error message from the last failed operation. Mirrors <see cref="OperationRecord.errorMessage"/>.</summary>
        public string errorMessage;

        /// <summary>ISO 8601 timestamp when this state was last modified.</summary>
        public string lastModified;

        /// <summary>The installed package version. Mirrors <see cref="InstallRecord.packageVersion"/>.</summary>
        public string installedVersion;

        /// <summary>On-disk size in bytes. Mirrors <see cref="InstallRecord.sizeBytes"/>.</summary>
        public long installedSizeBytes;

        /// <summary>Installed presence. The source of truth for <see cref="IsInstalled"/>.</summary>
        public InstallRecord install = new InstallRecord();

        /// <summary>The in-flight or last-completed operation.</summary>
        public OperationRecord operation = new OperationRecord();

        /// <summary>The available update, if any.</summary>
        public UpdateRecord update = new UpdateRecord();

        /// <summary>Initializes a new state for the specified package, not installed and idle.</summary>
        /// <param name="packageId">The unique identifier of the package.</param>
        public PackageState(string packageId)
        {
            this.packageId = packageId;
            this.lastModified = DateTime.UtcNow.ToString("O");
            Recompute();
        }

        /// <summary>
        /// True when usable content is on disk. Unlike the enum it replaces, this stays true while an
        /// update is available, while an update is downloading, and after an update fails.
        /// </summary>
        public bool IsInstalled => install != null && install.isPresent;

        /// <summary>True while content is being transferred for this package.</summary>
        public bool IsDownloading =>
            operation != null &&
            (operation.kind == PackageOperation.Installing || operation.kind == PackageOperation.Updating);

        /// <summary>True when the last operation failed. Independent of whether content is installed.</summary>
        public bool HasError => operation != null && !string.IsNullOrEmpty(operation.errorMessage);

        /// <summary>True when a newer version is available. Independent of installed presence.</summary>
        public bool HasUpdate => update != null && update.available;

        /// <summary>True when an operation is in flight, so a second one must not start.</summary>
        public bool IsBusy => operation != null && operation.kind != PackageOperation.None;

        /// <summary>
        /// Records that content is present on disk. Called only after the install has been durably
        /// committed — a package that claims to be installed in memory but not on disk reappears as
        /// installed-but-missing on the next launch.
        /// </summary>
        /// <param name="releaseId">The release the content came from.</param>
        /// <param name="packageVersion">The version from the verified release manifest.</param>
        /// <param name="sizeBytes">On-disk size, or 0 when unknown.</param>
        public void MarkInstalled(string releaseId, string packageVersion, long sizeBytes)
        {
            install ??= new InstallRecord();
            install.isPresent = true;
            install.releaseId = releaseId ?? string.Empty;
            install.packageVersion = packageVersion ?? string.Empty;
            install.sizeBytes = sizeBytes;
            install.installedAt = DateTime.UtcNow.ToString("O");

            // Installing the offered version consumes the offer; anything else leaves it standing.
            if (update != null && update.available &&
                string.Equals(update.targetPackageVersion, install.packageVersion, StringComparison.Ordinal))
            {
                ClearUpdate();
            }

            EndOperation(null);
        }

        /// <summary>Records that content is no longer on disk.</summary>
        public void MarkUninstalled()
        {
            install ??= new InstallRecord();
            install.isPresent = false;
            install.releaseId = string.Empty;
            install.packageVersion = string.Empty;
            install.sizeBytes = 0;
            install.installedAt = null;
            ClearUpdate();
            EndOperation(null);
        }

        /// <summary>Marks an operation as started. Does not touch installed presence.</summary>
        /// <param name="kind">The operation beginning.</param>
        /// <param name="totalBytes">Expected transfer size, or 0 when unknown.</param>
        public void BeginOperation(PackageOperation kind, long totalBytes = 0)
        {
            operation ??= new OperationRecord();
            operation.kind = kind;
            operation.progress = 0f;
            operation.downloadedBytes = 0;
            operation.totalBytes = totalBytes;
            // A new attempt clears the previous failure; the install record is untouched.
            operation.errorMessage = null;
            Recompute();
        }

        /// <summary>Updates progress for the in-flight operation. Ignored when idle.</summary>
        /// <param name="progress">Progress 0..1.</param>
        /// <param name="downloaded">Bytes transferred so far.</param>
        /// <param name="total">Total bytes expected, or 0 to keep the current total.</param>
        public void ReportProgress(float progress, long downloaded, long total)
        {
            if (operation == null || operation.kind == PackageOperation.None) return;
            operation.progress = progress < 0f ? 0f : progress > 1f ? 1f : progress;
            operation.downloadedBytes = downloaded;
            if (total > 0) operation.totalBytes = total;
            Recompute();
        }

        /// <summary>
        /// Ends the in-flight operation, recording an error when one occurred. Installed presence is
        /// deliberately untouched: a failed update leaves the previous version usable and reports the
        /// failure separately, which the single-enum model could not represent.
        /// </summary>
        /// <param name="error">The failure message, or null on success.</param>
        public void EndOperation(string error)
        {
            operation ??= new OperationRecord();
            operation.kind = PackageOperation.None;
            operation.progress = 0f;
            operation.downloadedBytes = 0;
            operation.totalBytes = 0;
            operation.errorMessage = string.IsNullOrEmpty(error) ? null : error;
            Recompute();
        }

        /// <summary>Records that a newer version is available. Does not change installed presence.</summary>
        /// <param name="targetReleaseId">The release offering the newer version.</param>
        /// <param name="targetPackageVersion">The newer version.</param>
        public void MarkUpdateAvailable(string targetReleaseId, string targetPackageVersion)
        {
            update ??= new UpdateRecord();
            update.available = true;
            update.targetReleaseId = targetReleaseId ?? string.Empty;
            update.targetPackageVersion = targetPackageVersion ?? string.Empty;
            Recompute();
        }

        /// <summary>Clears any recorded available update.</summary>
        public void ClearUpdate()
        {
            update ??= new UpdateRecord();
            update.available = false;
            update.targetReleaseId = string.Empty;
            update.targetPackageVersion = string.Empty;
            Recompute();
        }

        /// <summary>
        /// Rebuilds the legacy <see cref="status"/> projection and the mirrored progress fields from
        /// the records. Ordering matters: an in-flight operation is the most useful thing to show, an
        /// available update outranks a plain install, and an error only reaches the projection when
        /// nothing is installed — otherwise a failed update would present as an uninstalled package.
        /// </summary>
        public void Recompute()
        {
            install ??= new InstallRecord();
            operation ??= new OperationRecord();
            update ??= new UpdateRecord();

            if (operation.kind == PackageOperation.Installing || operation.kind == PackageOperation.Updating)
                status = PackageStatus.Downloading;
            else if (install.isPresent && update.available)
                status = PackageStatus.UpdateAvailable;
            else if (install.isPresent)
                status = PackageStatus.Installed;
            else if (!string.IsNullOrEmpty(operation.errorMessage))
                status = PackageStatus.Failed;
            else
                status = PackageStatus.Available;

            downloadProgress = operation.progress;
            downloadedBytes = operation.downloadedBytes;
            totalBytes = operation.totalBytes;
            errorMessage = operation.errorMessage;
            installedVersion = string.IsNullOrEmpty(install.packageVersion) ? null : install.packageVersion;
            installedSizeBytes = install.sizeBytes;
            lastModified = DateTime.UtcNow.ToString("O");
        }

        /// <summary>
        /// Rebuilds the records from a manifest written before they existed, where the enum was the
        /// only truth. Idempotent, and a no-op once records are populated.
        ///
        /// The lossy case is <see cref="PackageStatus.Failed"/>: the old model could not say whether
        /// a previous install survived the failure, so this assumes it did not. That under-reports
        /// rather than claiming content is present when it is not — the safe direction, since a
        /// re-install repairs it and a false "installed" produces missing-asset errors at runtime.
        /// </summary>
        public void MigrateFromLegacyStatus()
        {
            install ??= new InstallRecord();
            operation ??= new OperationRecord();
            update ??= new UpdateRecord();

            // Records already carry the truth; nothing to migrate.
            if (install.isPresent || update.available || operation.kind != PackageOperation.None) return;

            switch (status)
            {
                case PackageStatus.Installed:
                    install.isPresent = true;
                    install.packageVersion = installedVersion ?? string.Empty;
                    install.sizeBytes = installedSizeBytes;
                    install.releaseId = string.Empty;
                    break;

                case PackageStatus.UpdateAvailable:
                    // The defect this whole model exists to fix: this *is* installed content.
                    install.isPresent = true;
                    install.packageVersion = installedVersion ?? string.Empty;
                    install.sizeBytes = installedSizeBytes;
                    install.releaseId = string.Empty;
                    update.available = true;
                    break;

                case PackageStatus.Failed:
                    operation.errorMessage = string.IsNullOrEmpty(errorMessage) ? "unknown_error" : errorMessage;
                    break;

                case PackageStatus.Downloading:
                    // A persisted download means the app was killed mid-transfer. Nothing resumes it.
                    break;
            }

            Recompute();
        }
    }
}
