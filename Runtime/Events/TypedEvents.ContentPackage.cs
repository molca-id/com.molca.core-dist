namespace Molca.Events
{
    /// <summary>
    /// Content Package management events
    /// </summary>
    public static partial class TypedEvents
    {
        // Content Package Operation Events
        public static readonly Event<string> PackageDownloadStarted = new Event<string>(EventConstants.ContentPackage.DownloadStarted);
        public static readonly Event<string> PackageDownloadCompleted = new Event<string>(EventConstants.ContentPackage.DownloadCompleted);
        public static readonly Event<PackageOperationErrorEventData> PackageDownloadFailed = new Event<PackageOperationErrorEventData>(EventConstants.ContentPackage.DownloadFailed);

        public static readonly Event<string> PackageInstallStarted = new Event<string>(EventConstants.ContentPackage.InstallStarted);
        public static readonly Event<string> PackageInstallCompleted = new Event<string>(EventConstants.ContentPackage.InstallCompleted);
        public static readonly Event<PackageOperationErrorEventData> PackageInstallFailed = new Event<PackageOperationErrorEventData>(EventConstants.ContentPackage.InstallFailed);

        public static readonly Event<string> PackageUninstallStarted = new Event<string>(EventConstants.ContentPackage.UninstallStarted);
        public static readonly Event<string> PackageUninstallCompleted = new Event<string>(EventConstants.ContentPackage.UninstallCompleted);
        public static readonly Event<PackageOperationErrorEventData> PackageUninstallFailed = new Event<PackageOperationErrorEventData>(EventConstants.ContentPackage.UninstallFailed);

        public static readonly Event<string> PackageUpdateStarted = new Event<string>(EventConstants.ContentPackage.UpdateStarted);
        public static readonly Event<string> PackageUpdateCompleted = new Event<string>(EventConstants.ContentPackage.UpdateCompleted);
        public static readonly Event<PackageOperationErrorEventData> PackageUpdateFailed = new Event<PackageOperationErrorEventData>(EventConstants.ContentPackage.UpdateFailed);

        public static readonly Event<string> PackageValidationStarted = new Event<string>(EventConstants.ContentPackage.ValidationStarted);
        public static readonly Event<PackageValidationResultEventData> PackageValidationCompleted = new Event<PackageValidationResultEventData>(EventConstants.ContentPackage.ValidationCompleted);

        public static readonly Event<long> PackageCacheCleanupStarted = new Event<long>(EventConstants.ContentPackage.CacheCleanupStarted);
        public static readonly Event<long> PackageCacheCleanupCompleted = new Event<long>(EventConstants.ContentPackage.CacheCleanupCompleted);
        public static readonly Event<PackageOperationErrorEventData> PackageCacheCleanupFailed = new Event<PackageOperationErrorEventData>(EventConstants.ContentPackage.CacheCleanupFailed);

        // Content Package Progress Events
        public static readonly Event<PackageProgressEventData> PackageDownloadProgress = new Event<PackageProgressEventData>(EventConstants.ContentPackage.DownloadProgress);
        public static readonly Event<PackageProgressEventData> PackageInstallProgress = new Event<PackageProgressEventData>(EventConstants.ContentPackage.InstallProgress);
        public static readonly Event<PackageProgressEventData> PackageUpdateProgress = new Event<PackageProgressEventData>(EventConstants.ContentPackage.UpdateProgress);
        public static readonly Event<PackageValidationProgressEventData> PackageValidationProgress = new Event<PackageValidationProgressEventData>(EventConstants.ContentPackage.ValidationProgress);
        public static readonly Event<StorageCleanupProgressEventData> PackageCacheCleanupProgress = new Event<StorageCleanupProgressEventData>(EventConstants.ContentPackage.CacheCleanupProgress);
    }
}
