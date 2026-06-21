using System;
using System.Threading;
using Molca.ContentPackage.Core;
using Molca.Events;
using Molca.Telemetry;
using UnityEngine;

namespace Molca.ContentPackage.Services
{
    /// <summary>
    /// RuntimeSubsystem integration for the redesigned Content Package DLC System.
    /// Initializes and manages the PackageService with proper integration into the RuntimeManager subsystem pattern.
    /// 
    /// Note: The initialization priority should be set in the Unity Inspector on the GameObject
    /// that has this component. Recommended priority: 150 (after core systems, before app-specific systems).
    /// </summary>
    public class PackageSubsystem : RuntimeSubsystem
    {
        #region Private Fields

        /// <summary>
        /// The main package service instance that handles all package operations.
        /// </summary>
        private PackageService _packageService;

        /// <summary>
        /// Configuration settings loaded from GlobalSettings.
        /// </summary>
        private ContentPackageSettings _settings;

        /// <summary>
        /// Bounded-concurrency download scheduler over <see cref="_packageService"/>.
        /// </summary>
        private PackageDownloadQueue _downloadQueue;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the package service instance.
        /// Provides access to all package management operations including installation, uninstallation, and state queries.
        /// </summary>
        /// <returns>The PackageService instance, or null if not initialized.</returns>
        public PackageService PackageService => _packageService;

        /// <summary>
        /// Gets the download queue for scheduling installs with bounded concurrency, pause/resume,
        /// and aggregate progress. Null until initialization completes.
        /// </summary>
        public PackageDownloadQueue DownloadQueue => _downloadQueue;

        #endregion

        #region RuntimeSubsystem Implementation

        /// <summary>
        /// Initializes the PackageSubsystem asynchronously during bootstrap.
        /// Loads configuration settings, creates the <see cref="PackageService"/>, and warms up
        /// Addressables + the remote catalog. Required-package downloads are intentionally
        /// <em>not</em> awaited here — see <see cref="AutoInstallRequiredPackagesAsync"/>.
        /// </summary>
        /// <param name="cancellationToken">
        /// Bootstrap-lifetime token, cancelled on teardown or the per-subsystem init timeout.
        /// </param>
        /// <remarks>
        /// A required package can be arbitrarily large; awaiting its download here would block
        /// bootstrap and trip the per-subsystem init timeout, booting the app without the
        /// content and no signal to the caller. Instead the catalog warm-up stays on the
        /// critical path (so the content system is queryable when bootstrap completes) while
        /// the actual required-package installs run as a deferred phase keyed on
        /// <see cref="RuntimeSubsystem.ShutdownToken"/>.
        /// </remarks>
        public override async Awaitable InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                Log("[PackageSubsystem] Starting initialization...");

                _settings = GlobalSettings.GetModule<ContentPackageSettings>();
                if (_settings == null)
                {
                    Debug.LogError("[PackageSubsystem] ContentPackageSettings not found in GlobalSettings! " +
                                 "Please ensure ContentPackageSettings is added to the GlobalSettings module list.");
                    return;
                }

                // Optional telemetry: resolve the subsystem if present so package operations are
                // instrumented. Null when no TelemetrySubsystem is on the RuntimeManager prefab.
                var telemetry = RuntimeManager.GetSubsystem<TelemetrySubsystem>();

                _packageService = new PackageService(_settings, telemetry);
                await _packageService.InitializeAsync(cancellationToken);

                // Download scheduler with bounded concurrency; surfaces queue status via EventDispatcher.
                var events = RuntimeManager.GetService<EventDispatcher>();
                _downloadQueue = new PackageDownloadQueue(_packageService, _settings.MaxConcurrentDownloads, events);

                // Required-package downloads run off the bootstrap critical path. Fire-and-forget
                // is deliberate (async-contract rule 5): the callee owns its exceptions and is
                // keyed on ShutdownToken so teardown cancels it.
                _ = AutoInstallRequiredPackagesAsync(ShutdownToken);

                Log("[PackageSubsystem] Initialization completed successfully");
            }
            catch (OperationCanceledException)
            {
                Log("[PackageSubsystem] Initialization cancelled");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PackageSubsystem] Initialization failed with error: {ex.Message}");
                Debug.LogException(ex);
            }
        }

        /// <summary>
        /// Releases resources on shutdown. Package states are persisted eagerly by
        /// <see cref="Core.PackageManifest"/> on every change, so no explicit flush is needed;
        /// in-flight required-package installs are cancelled via <see cref="RuntimeSubsystem.ShutdownToken"/>
        /// (cancelled by the base before this runs).
        /// </summary>
        public override void Teardown()
        {
            Log("[PackageSubsystem] Teardown");
            _downloadQueue?.Dispose();
            _downloadQueue = null;
            base.Teardown();
        }

        #endregion

        #region Private Methods

        /// <summary>Verbose-gated info log; mirrors <see cref="PackageService"/> logging policy.</summary>
        private void Log(string msg)
        {
            if (_settings == null || _settings.EnableVerboseLogging)
                Debug.Log(msg);
        }

        /// <summary>
        /// Installs every <see cref="ContentPackageSettings.PackageConfig.isRequired"/> package
        /// that is not already installed. Runs off the bootstrap critical path; owns its own
        /// exceptions and exits quietly on cancellation.
        /// </summary>
        /// <param name="cancellationToken">Subsystem lifetime token (<see cref="RuntimeSubsystem.ShutdownToken"/>).</param>
        private async Awaitable AutoInstallRequiredPackagesAsync(CancellationToken cancellationToken)
        {
            if (_settings?.packageConfigs == null) return;

            try
            {
                foreach (var cfg in _settings.packageConfigs)
                {
                    if (cfg == null || !cfg.isRequired) continue;
                    cancellationToken.ThrowIfCancellationRequested();

                    var state = _packageService.GetPackageState(cfg.packageId);
                    if (state?.status == PackageStatus.Installed) continue;

                    Log($"[PackageSubsystem] Auto-installing required package: {cfg.packageId}");
                    var result = await _packageService.InstallPackageAsync(cfg.packageId, null, cancellationToken);
                    if (!result.Success && !result.WasCancelled)
                        Debug.LogWarning($"[PackageSubsystem] Failed to auto-install required package '{cfg.packageId}': {result.ErrorMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                Log("[PackageSubsystem] Required-package auto-install cancelled");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PackageSubsystem] Error auto-installing required packages: {ex.Message}");
            }
        }

        #endregion
    }
}