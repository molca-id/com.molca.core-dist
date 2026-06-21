using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.ContentPackage.Core;
using Molca.Telemetry;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Molca.ContentPackage.Services
{
    /// <summary>
    /// Core service providing all package management operations including installation, uninstallation,
    /// updates, and state tracking. Integrates directly with Unity Addressables for content delivery.
    /// </summary>
    public class PackageService
    {
        #region Events

        /// <summary>
        /// Fired when a package state changes (Available, Downloading, Installed, Failed, UpdateAvailable).
        /// </summary>
        public event Action<string, PackageStatus> OnPackageStateChanged;

        /// <summary>
        /// Fired when download progress changes during package installation.
        /// Progress is reported as a value between 0.0 and 1.0.
        /// </summary>
        public event Action<string, float> OnDownloadProgress;

        /// <summary>
        /// Fired when a package operation encounters an error.
        /// </summary>
        public event Action<string, string> OnPackageError;

        /// <summary>
        /// Fired when the Addressables catalog is refreshed from remote sources.
        /// </summary>
        public event Action OnCatalogRefreshed;

        /// <summary>
        /// Fired whenever <see cref="CloudStatus"/> changes — on every
        /// <see cref="RefreshCatalogAsync"/> attempt regardless of outcome.
        /// </summary>
        public event Action<PackageCloudStatus> OnCloudStatusChanged;

        #endregion

        #region Private Fields

        /// <summary>
        /// Configuration settings for the package system.
        /// </summary>
        private readonly ContentPackageSettings _settings;

        /// <summary>
        /// Handles JSON persistence of package states to PlayerPrefs.
        /// </summary>
        private readonly PackageManifest _manifest;

        /// <summary>
        /// Optional telemetry sink for operation outcomes. Null when no telemetry is configured;
        /// all emission is guarded so the service behaves identically with or without it.
        /// </summary>
        private readonly TelemetrySubsystem _telemetry;

        /// <summary>
        /// All valid package configs keyed by package ID, including non-visible (hidden dependency) packages.
        /// Visibility filtering for UI is the caller's responsibility.
        /// </summary>
        private readonly Dictionary<string, ContentPackageSettings.PackageConfig> _definitions;

        /// <summary>
        /// Dictionary of current package states, keyed by package ID.
        /// </summary>
        private readonly Dictionary<string, PackageState> _states;

        /// <summary>
        /// Dictionary of active operation cancellation tokens, keyed by package ID.
        /// Used to cancel ongoing downloads and installations.
        /// </summary>
        private readonly Dictionary<string, CancellationTokenSource> _activeOperations;

        /// <summary>
        /// Remote package manifest fetched from CDN on catalog refresh.
        /// Null until <see cref="RefreshCatalogAsync"/> has successfully fetched it.
        /// </summary>
        private RemotePackageManifest _remoteManifest;

        /// <summary>
        /// Top-level version index fetched when the CDN serves a schema v2 <c>packages.json</c>.
        /// Null when the CDN is on schema v1 or has not been fetched yet.
        /// </summary>
        private ContentVersionIndex _versionIndex;

        #endregion

        #region Public Properties

        /// <summary>
        /// Live snapshot of cloud connectivity state. Updated on every
        /// <see cref="RefreshCatalogAsync"/> attempt. Subscribe to
        /// <see cref="OnCloudStatusChanged"/> to react to transitions.
        /// </summary>
        public PackageCloudStatus CloudStatus { get; } = new PackageCloudStatus();

        /// <summary>
        /// The <see cref="ContentVersionEntry"/> currently installed on this device,
        /// or <c>null</c> if no versioned content has been installed or the CDN is on schema v1.
        /// </summary>
        public ContentVersionEntry InstalledContentVersion
        {
            get
            {
                var v = _manifest.InstalledContentVersion;
                if (string.IsNullOrEmpty(v) || _versionIndex == null) return null;
                return _versionIndex.versions?.Find(e => e.version == v);
            }
        }

        #endregion

        #region Logging Helpers

        private void Log(string msg)        { if (_settings.EnableVerboseLogging) Debug.Log(msg); }
        private void LogWarning(string msg) { Debug.LogWarning(msg); }
        private void LogError(string msg)   { Debug.LogError(msg); }

        #endregion

        #region Telemetry Helpers

        /// <summary>
        /// Emits a package operation outcome to telemetry (no-op when telemetry is absent).
        /// </summary>
        /// <param name="eventName">Event name, e.g. <c>"content_package.install"</c>.</param>
        /// <param name="packageId">The package the operation acted on.</param>
        /// <param name="result">The operation result (success/cancelled/error are derived from it).</param>
        /// <param name="durationSeconds">Wall-clock duration of the operation.</param>
        /// <param name="bytes">Bytes transferred, or a negative value to omit.</param>
        /// <param name="extra">Optional additional properties merged into the event.</param>
        private void TrackOperation(string eventName, string packageId, OperationResult result,
            double durationSeconds, long bytes = -1, IReadOnlyDictionary<string, object> extra = null)
        {
            if (_telemetry == null) return;

            var props = new Dictionary<string, object>
            {
                { "packageId", packageId },
                { "success", result.Success },
                { "cancelled", result.WasCancelled },
                { "durationSeconds", durationSeconds },
            };
            if (bytes >= 0) props["bytes"] = bytes;
            if (!result.Success && !result.WasCancelled && !string.IsNullOrEmpty(result.ErrorMessage))
                props["error"] = result.ErrorMessage;
            if (extra != null)
                foreach (var kv in extra) props[kv.Key] = kv.Value;

            _telemetry.Track(eventName, props);
        }

        /// <summary>
        /// Convenience wrapper: emits <paramref name="result"/> as a telemetry event and returns it,
        /// so a method can both report and return at a single return site.
        /// </summary>
        private OperationResult TrackReturn(string eventName, string packageId, OperationResult result,
            System.Diagnostics.Stopwatch stopwatch, long bytes = -1, IReadOnlyDictionary<string, object> extra = null)
        {
            TrackOperation(eventName, packageId, result, stopwatch.Elapsed.TotalSeconds, bytes, extra);
            return result;
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the PackageService class with the specified settings.
        /// </summary>
        /// <param name="settings">The configuration settings for the package system.</param>
        /// <param name="telemetry">
        /// Optional telemetry subsystem; when supplied, install/uninstall/update/version-switch
        /// outcomes are emitted to it. Pass <c>null</c> to disable telemetry.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when settings is null.</exception>
        public PackageService(ContentPackageSettings settings, TelemetrySubsystem telemetry = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _telemetry = telemetry;
            _manifest = new PackageManifest();
            _definitions = new Dictionary<string, ContentPackageSettings.PackageConfig>();
            _states = new Dictionary<string, PackageState>();
            _activeOperations = new Dictionary<string, CancellationTokenSource>();

            LoadDefinitions();
            LoadStates();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the PackageService asynchronously.
        /// Initializes Addressables, optionally refreshes catalog, and validates installed packages.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancelled when the owning subsystem is torn down or the bootstrap init timeout elapses;
        /// threaded through the catalog refresh so a slow network fetch cannot outlive bootstrap.
        /// </param>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        public async Awaitable InitializeAsync(CancellationToken cancellationToken = default)
        {
            Log("[PackageService] Starting initialization...");

            try
            {
                // Initialize Addressables if needed
                Log("[PackageService] Initializing Addressables...");
                await Addressables.InitializeAsync().Task;
                Log("[PackageService] Addressables initialized successfully");

                // Refresh catalog if configured
                if (_settings.CheckForCatalogUpdates)
                {
                    Log("[PackageService] Automatic catalog updates enabled, refreshing catalog...");
                    var refreshResult = await RefreshCatalogAsync(cancellationToken);
                    
                    if (refreshResult.Success)
                    {
                        Log("[PackageService] Catalog refresh completed successfully during initialization");
                    }
                    else
                    {
                        LogWarning($"[PackageService] Catalog refresh failed during initialization: {refreshResult.ErrorMessage}");
                        // Don't fail initialization if catalog refresh fails
                    }
                }
                else
                {
                    Log("[PackageService] Automatic catalog updates disabled, skipping catalog refresh");
                }

                // Always validate installed packages on startup
                Log("[PackageService] Validating installed packages...");
                await ValidateInstalledPackagesAsync();
                Log("[PackageService] Package validation completed");

                Log("[PackageService] Initialization completed successfully");
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Error during initialization: {ex.Message}");
                // Don't throw - allow the service to continue with limited functionality
            }
        }

        /// <summary>
        /// Validates installed packages by checking if their definitions still exist and if their
        /// Addressables bundles are still present in the local cache.
        /// Marks packages as Available if the definition is missing or the cache was evicted.
        /// </summary>
        /// <returns>A task representing the asynchronous validation operation.</returns>
        private async Awaitable ValidateInstalledPackagesAsync()
        {
            Log("[PackageService] Validating installed packages...");

            try
            {
                int validatedCount = 0;
                int invalidatedCount = 0;

                foreach (var state in _states.Values.Where(s => s.IsInstalled).ToList())
                {
                    if (!_definitions.TryGetValue(state.packageId, out var definition))
                    {
                        LogWarning($"[PackageService] Package '{state.packageId}' is installed but definition not found, marking as available");
                        UpdateState(state.packageId, PackageStatus.Available);
                        invalidatedCount++;
                        continue;
                    }

                    // Verify the bundles are actually present in the Addressables cache.
                    // GetDownloadSizeAsync returns 0 when everything is cached; > 0 means the
                    // OS evicted the bundles and they need to be re-downloaded.
                    var validLabels = definition.addressableLabels?
                        .Where(l => !string.IsNullOrEmpty(l))
                        .Cast<object>()
                        .ToList();

                    if (validLabels != null && validLabels.Count > 0)
                    {
                        var handle = Addressables.GetDownloadSizeAsync(validLabels);
                        await handle.Task;

                        if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result > 0)
                        {
                            LogWarning($"[PackageService] Package '{state.packageId}' is marked installed but its bundles are not cached (evicted). Marking as available.");
                            UpdateState(state.packageId, PackageStatus.Available);
                            invalidatedCount++;
                        }
                        else
                        {
                            validatedCount++;
                        }

                        if (handle.IsValid())
                            Addressables.Release(handle);
                    }
                    else
                    {
                        validatedCount++;
                    }
                }

                Log($"[PackageService] Package validation completed: {validatedCount} valid, {invalidatedCount} invalidated");
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Error during package validation: {ex.Message}");
            }
        }

        #endregion

        #region Private Methods

        #region State Management

        /// <summary>
        /// Gets an existing package state or creates a new one if it doesn't exist.
        /// If the state doesn't exist in memory, it attempts to load it from the manifest.
        /// If it doesn't exist in the manifest either, creates a new state with Available status.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package.</param>
        /// <returns>The PackageState for the specified package.</returns>
        private PackageState GetOrCreateState(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                LogWarning("[PackageService] GetOrCreateState called with null or empty packageId");
                return null;
            }

            // Check if state is already in memory
            if (_states.TryGetValue(packageId, out var state))
            {
                return state;
            }

            // Try to load from manifest
            state = _manifest.GetState(packageId);
            if (state != null)
            {
                _states[packageId] = state;
                return state;
            }

            // Create new state
            state = new PackageState(packageId);
            _states[packageId] = state;
            
            Log($"[PackageService] Created new state for package: {packageId}");
            return state;
        }

        /// <summary>
        /// Updates the state of a package and persists the change immediately.
        /// Dispatches appropriate events to notify listeners of the state change.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package.</param>
        /// <param name="status">The new status to set.</param>
        /// <param name="errorMessage">Optional error message if the status is Failed.</param>
        private void UpdateState(string packageId, PackageStatus status, string errorMessage = null)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                LogWarning("[PackageService] UpdateState called with null or empty packageId");
                return;
            }

            var state = GetOrCreateState(packageId);
            if (state == null)
            {
                LogError($"[PackageService] Failed to get or create state for package: {packageId}");
                return;
            }

            // Store previous status for logging
            var previousStatus = state.status;

            // Update state properties
            state.status = status;
            state.errorMessage = errorMessage;
            state.lastModified = DateTime.UtcNow.ToString("O");

            // Clear error message if status is not Failed
            if (status != PackageStatus.Failed)
            {
                state.errorMessage = null;
            }

            // Reset progress if not downloading
            if (status != PackageStatus.Downloading)
            {
                state.downloadProgress = 0f;
                state.downloadedBytes = 0;
                state.totalBytes = 0;
            }

            try
            {
                // Persist state change immediately
                _manifest.SetState(state);
                
                Log($"[PackageService] Package '{packageId}' state changed: {previousStatus} → {status}");

                // Dispatch state change event
                OnPackageStateChanged?.Invoke(packageId, status);

                // Dispatch error event if there's an error
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    LogError($"[PackageService] Package '{packageId}' error: {errorMessage}");
                    OnPackageError?.Invoke(packageId, errorMessage);
                }
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Failed to persist state change for package '{packageId}': {ex.Message}");
            }
        }

        #endregion

        #region Definition and State Loading

        /// <summary>
        /// Populates the definitions dictionary from settings, including only visible, valid configs.
        /// </summary>
        private void LoadDefinitions()
        {
            _definitions.Clear();

            if (_settings.packageConfigs == null)
            {
                LogWarning("[PackageService] No package configurations found in settings");
                return;
            }

            foreach (var config in _settings.packageConfigs)
            {
                if (config == null || string.IsNullOrEmpty(config.packageId))
                    continue;

                _definitions[config.packageId] = config;
                Log($"[PackageService] Loaded package config: {config.packageId}");
            }

            Log($"[PackageService] Loaded {_definitions.Count} package configs");
        }

        /// <summary>Returns the dependency package IDs for a config, filtering out empty entries.</summary>
        private static string[] GetDependencyIds(ContentPackageSettings.PackageConfig config)
        {
            if (config.dependencies == null || config.dependencies.Length == 0)
                return Array.Empty<string>();

            var ids = new List<string>();
            foreach (var dep in config.dependencies)
            {
                if (dep != null && !string.IsNullOrEmpty(dep.packageId))
                    ids.Add(dep.packageId);
            }
            return ids.ToArray();
        }

        /// <summary>
        /// Loads package states from the persistent manifest.
        /// Initializes the states dictionary with previously saved package states.
        /// </summary>
        private void LoadStates()
        {
            _states.Clear();

            try
            {
                var savedStates = _manifest.GetAllStates();
                foreach (var state in savedStates)
                {
                    if (state != null && !string.IsNullOrEmpty(state.packageId))
                    {
                        // A persisted Downloading state means the app was killed mid-download.
                        // No operation will resume it, so reset to Available so the user can retry.
                        if (state.status == PackageStatus.Downloading)
                        {
                            state.status          = PackageStatus.Available;
                            state.downloadProgress = 0f;
                            state.downloadedBytes  = 0;
                            state.totalBytes       = 0;
                        }

                        _states[state.packageId] = state;
                    }
                }

                Log($"[PackageService] Loaded {_states.Count} package states from manifest");
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Failed to load package states: {ex.Message}");
            }
        }

        #endregion

        #region Dependency Resolution

        /// <summary>
        /// Resolves the complete dependency graph for a package, returning dependencies in topological order.
        /// Dependencies are returned in the order they should be installed (dependencies first).
        /// </summary>
        /// <param name="packageId">The package ID to resolve dependencies for.</param>
        /// <returns>An OperationResult containing the list of package IDs in installation order, or an error if resolution fails.</returns>
        public OperationResult<List<string>> ResolveDependencies(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                return OperationResult<List<string>>.CreateFailure("Package ID cannot be null or empty");
            }

            if (!_definitions.ContainsKey(packageId))
            {
                return OperationResult<List<string>>.CreateFailure($"Package '{packageId}' not found");
            }

            var resolved = new List<string>();
            var visited = new HashSet<string>();
            var stack = new HashSet<string>();

            bool success = ResolveDependenciesRecursive(packageId, resolved, visited, stack, out string error);

            if (!success)
            {
                LogError($"[PackageService] Dependency resolution failed for '{packageId}': {error}");
                return OperationResult<List<string>>.CreateFailure(error);
            }

            Log($"[PackageService] Resolved dependencies for '{packageId}': [{string.Join(", ", resolved)}]");
            return OperationResult<List<string>>.CreateSuccess(resolved);
        }

        /// <summary>
        /// Recursively resolves dependencies using depth-first search with cycle detection.
        /// Uses topological sorting to ensure dependencies are returned in correct installation order.
        /// </summary>
        /// <param name="packageId">The current package being processed.</param>
        /// <param name="resolved">List of resolved packages in topological order.</param>
        /// <param name="visited">Set of packages that have been fully processed.</param>
        /// <param name="stack">Set of packages currently being processed (for cycle detection).</param>
        /// <param name="error">Output parameter for error message if resolution fails.</param>
        /// <returns>True if resolution succeeds, false if there's an error.</returns>
        private bool ResolveDependenciesRecursive(
            string packageId,
            List<string> resolved,
            HashSet<string> visited,
            HashSet<string> stack,
            out string error)
        {
            error = null;

            // Check for circular dependency
            if (stack.Contains(packageId))
            {
                var cycle = string.Join(" → ", stack) + " → " + packageId;
                error = $"Circular dependency detected: {cycle}";
                return false;
            }

            // Skip if already processed
            if (visited.Contains(packageId))
            {
                return true;
            }

            // Validate package exists
            if (!_definitions.TryGetValue(packageId, out var definition))
            {
                error = $"Package '{packageId}' not found in definitions";
                return false;
            }

            // Add to processing stack for cycle detection
            stack.Add(packageId);

            // Process dependencies first (depth-first)
            foreach (var dependencyId in GetDependencyIds(definition))
            {
                if (!ResolveDependenciesRecursive(dependencyId, resolved, visited, stack, out error))
                    return false;
            }

            // Remove from processing stack
            stack.Remove(packageId);

            // Mark as visited and add to resolved list (topological order)
            visited.Add(packageId);
            resolved.Add(packageId);

            return true;
        }

        #endregion

        #region Package Installation

        /// <summary>
        /// Installs a package and all its dependencies asynchronously.
        /// Validates package existence, checks if already installed, resolves dependencies,
        /// and downloads content using Unity Addressables.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package to install.</param>
        /// <param name="progress">Optional progress reporter for download progress (0.0 to 1.0).</param>
        /// <param name="cancellationToken">Token to cancel the installation operation.</param>
        /// <returns>An OperationResult indicating success or failure of the installation.</returns>
        public async Awaitable<OperationResult> InstallPackageAsync(
            string packageId, 
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            // 1. Validate package exists in definitions
            if (string.IsNullOrEmpty(packageId))
            {
                return OperationResult.CreateFailure("Package ID cannot be null or empty");
            }

            if (!_definitions.TryGetValue(packageId, out var definition))
            {
                return OperationResult.CreateFailure($"Package '{packageId}' not found in definitions");
            }

            // Guard against concurrent installs of the same package. Without this, two callers
            // racing on a shared dependency would overwrite each other's CancellationTokenSource,
            // orphaning the first download handle.
            if (_activeOperations.ContainsKey(packageId))
            {
                LogWarning($"[PackageService] Package '{packageId}' is already being installed");
                return OperationResult.CreateFailure($"Package '{packageId}' is already being installed");
            }

            Log($"[PackageService] Starting installation of package: {packageId}");

            // 2. Check if already installed (return success)
            var state = GetOrCreateState(packageId);
            if (state.IsInstalled)
            {
                Log($"[PackageService] Package '{packageId}' is already installed");
                return OperationResult.CreateSuccess();
            }

            // Register the operation up-front (before dependency resolution) so the re-entrancy
            // guard above catches a second concurrent install of the same package during the
            // dependency phase too — not only once the content download starts. The linked CTS
            // is the cancellation handle returned by CancelPackageInstall(packageId).
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeOperations[packageId] = cts;

            try
            {
                // 3. Resolve dependencies using ResolveDependencies()
                var dependencyResult = ResolveDependencies(packageId);
                if (!dependencyResult.Success)
                {
                    return OperationResult.CreateFailure($"Failed to resolve dependencies for '{packageId}': {dependencyResult.ErrorMessage}");
                }

                var dependenciesToInstall = dependencyResult.Data;
                Log($"[PackageService] Resolved {dependenciesToInstall.Count} dependencies for '{packageId}': [{string.Join(", ", dependenciesToInstall)}]");

                // 4. Install dependencies recursively before main package.
                // Dependencies are returned in topological order, so we install them sequentially.
                // Pass cts.Token so cancelling this package also cancels its in-progress dependencies.
                for (int i = 0; i < dependenciesToInstall.Count - 1; i++) // Exclude the main package (last item)
                {
                    var dependencyId = dependenciesToInstall[i];
                    var dependencyState = GetOrCreateState(dependencyId);

                    if (!dependencyState.IsInstalled)
                    {
                        Log($"[PackageService] Installing dependency: {dependencyId}");
                        var dependencyInstallResult = await InstallPackageAsync(dependencyId, null, cts.Token);

                        if (!dependencyInstallResult.Success)
                        {
                            return OperationResult.CreateFailure($"Failed to install dependency '{dependencyId}': {dependencyInstallResult.ErrorMessage}");
                        }
                    }
                    else
                    {
                        Log($"[PackageService] Dependency '{dependencyId}' is already installed, skipping");
                    }
                }

                // 5. Install the main package
                return await InstallPackageContentAsync(packageId, definition, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log($"[PackageService] Installation of package '{packageId}' was cancelled");
                UpdateState(packageId, PackageStatus.Available);
                return OperationResult.CreateCancelled();
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Unexpected error during installation of package '{packageId}': {ex.Message}");
                UpdateState(packageId, PackageStatus.Failed, ex.Message);
                return OperationResult.CreateFailure($"Unexpected error: {ex.Message}");
            }
            finally
            {
                _activeOperations.Remove(packageId);
                cts.Dispose();
            }
        }

        /// <summary>
        /// Cancels an in-progress install/update for the given package, if one is active.
        /// The operation unwinds to <see cref="PackageStatus.Available"/> via its own cancellation
        /// handling. No-op when the package is not currently installing.
        /// </summary>
        /// <param name="packageId">The package whose active operation should be cancelled.</param>
        /// <returns><c>true</c> if an active operation was found and cancellation was requested.</returns>
        public bool CancelPackageInstall(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            if (_activeOperations.TryGetValue(packageId, out var cts))
            {
                Log($"[PackageService] Cancelling active operation for package: {packageId}");
                cts.Cancel();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Downloads and installs the content for a specific package using Addressables.
        /// This method handles the actual Addressables download logic.
        /// </summary>
        /// <param name="packageId">The package ID to install.</param>
        /// <param name="definition">The package definition containing Addressables labels.</param>
        /// <param name="progress">Optional progress reporter.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>OperationResult indicating success or failure.</returns>
        private async Awaitable<OperationResult> InstallPackageContentAsync(
            string packageId,
            ContentPackageSettings.PackageConfig definition,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            Log($"[PackageService] Starting content download for package: {packageId}");

            // Validate that the package has Addressables labels before announcing Downloading.
            var validLabels = definition.addressableLabels?
                .Where(l => !string.IsNullOrEmpty(l))
                .Cast<object>()
                .ToList();

            if (validLabels == null || validLabels.Count == 0)
            {
                return OperationResult.CreateFailure($"Package '{packageId}' has no Addressables labels defined");
            }

            // Make room for the download: honour the cache cap and available disk space,
            // evicting LRU non-required packages as needed. Fails only when disk is genuinely full.
            var spaceResult = await EnsureInstallSpaceAsync(packageId, cancellationToken);
            if (!spaceResult.Success)
            {
                UpdateState(packageId, PackageStatus.Failed, spaceResult.ErrorMessage);
                return spaceResult;
            }

            // Update state to Downloading
            UpdateState(packageId, PackageStatus.Downloading);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Retry transient download failures with exponential backoff, bounded by the
                // configured attempt count and per-attempt timeout. A genuine caller cancellation
                // propagates as OperationCanceledException and is handled by InstallPackageAsync.
                var result = await RetryAsync(
                    ct => DownloadOnceAsync(packageId, validLabels, progress, ct),
                    $"Download '{packageId}'",
                    cancellationToken);

                if (result.WasCancelled)
                {
                    UpdateState(packageId, PackageStatus.Available);
                    return TrackReturn("content_package.install", packageId, result, stopwatch);
                }

                if (!result.Success)
                {
                    LogError($"[PackageService] Download failed for package '{packageId}': {result.ErrorMessage}");
                    // Capture bytes before UpdateState resets the download counters.
                    long failedBytes = GetOrCreateState(packageId).totalBytes;
                    UpdateState(packageId, PackageStatus.Failed, result.ErrorMessage);
                    return TrackReturn("content_package.install", packageId, result, stopwatch, failedBytes);
                }

                // Fire 100 % progress before transitioning — UpdateState resets downloadProgress to 0.
                progress?.Report(1f);
                OnDownloadProgress?.Invoke(packageId, 1f);

                // Mark as installed. Capture bytes before UpdateState clears the counters.
                var finalState = GetOrCreateState(packageId);
                long installedBytes = finalState.totalBytes;
                finalState.installedVersion = definition.metadata?.version ?? "1.0.0";
                finalState.installedSizeBytes = installedBytes; // for cache-budget accounting / LRU
                UpdateState(packageId, PackageStatus.Installed);

                Log($"[PackageService] Successfully installed package: {packageId}");
                return TrackReturn("content_package.install", packageId, OperationResult.CreateSuccess(), stopwatch, installedBytes);
            }
            catch (OperationCanceledException)
            {
                // Caller cancellation — let InstallPackageAsync record the Available transition.
                throw;
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Error during content download for package '{packageId}': {ex.Message}");
                UpdateState(packageId, PackageStatus.Failed, ex.Message);
                return TrackReturn("content_package.install", packageId, OperationResult.CreateFailure(ex.Message), stopwatch);
            }
        }

        /// <summary>
        /// Performs a single Addressables download attempt for the given labels, polling progress
        /// until done. Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/>
        /// fires (caller cancellation or per-attempt timeout); returns a failure result if the
        /// Addressables operation itself fails. The download handle is always released.
        /// </summary>
        private async Awaitable<OperationResult> DownloadOnceAsync(
            string packageId,
            List<object> validLabels,
            IProgress<float> progress,
            CancellationToken cancellationToken)
        {
            // Download all labels in a single MergeMode.Union call so that bundles shared
            // across labels are only downloaded once, and progress reflects actual bytes.
            Log($"[PackageService] Starting download for package '{packageId}' ({validLabels.Count} label(s))");
            var downloadHandle = Addressables.DownloadDependenciesAsync(validLabels, Addressables.MergeMode.Union, false);

            try
            {
                while (!downloadHandle.IsDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Use byte-accurate progress when the download status is available;
                    // fall back to PercentComplete before the first status report arrives.
                    var dlStatus = downloadHandle.GetDownloadStatus();
                    float pct = dlStatus.TotalBytes > 0
                        ? (float)dlStatus.DownloadedBytes / dlStatus.TotalBytes
                        : downloadHandle.PercentComplete;

                    var currentState = GetOrCreateState(packageId);
                    currentState.downloadProgress  = pct;
                    currentState.downloadedBytes   = dlStatus.DownloadedBytes;
                    currentState.totalBytes        = dlStatus.TotalBytes;

                    progress?.Report(pct);
                    OnDownloadProgress?.Invoke(packageId, pct);

                    await Awaitable.NextFrameAsync(cancellationToken);
                }

                if (downloadHandle.Status != AsyncOperationStatus.Succeeded)
                {
                    string error = downloadHandle.OperationException?.Message ?? "Unknown download error";
                    return OperationResult.CreateFailure(error);
                }

                return OperationResult.CreateSuccess();
            }
            finally
            {
                if (downloadHandle.IsValid())
                    Addressables.Release(downloadHandle);
            }
        }

        /// <summary>
        /// Runs <paramref name="attempt"/> with retry-on-failure and exponential backoff, honouring
        /// the retry/timeout values from <see cref="ContentPackageSettings"/>
        /// (<see cref="ContentPackageSettings.MaxRetryAttempts"/>,
        /// <see cref="ContentPackageSettings.InitialRetryDelay"/>,
        /// <see cref="ContentPackageSettings.MaxRetryDelay"/>,
        /// <see cref="ContentPackageSettings.DownloadTimeoutSeconds"/>).
        /// </summary>
        /// <remarks>
        /// Each attempt receives a token linked to <paramref name="cancellationToken"/> plus a
        /// per-attempt timeout. A timeout is a retryable failure; a genuine caller cancellation
        /// is re-thrown immediately and never retried. A successful or cancelled result short-circuits.
        /// </remarks>
        private async Awaitable<OperationResult> RetryAsync(
            Func<CancellationToken, Awaitable<OperationResult>> attempt,
            string operationLabel,
            CancellationToken cancellationToken)
        {
            int maxAttempts = Mathf.Max(1, _settings.MaxRetryAttempts);
            float delay = ContentPackageSettings.InitialRetryDelay;
            var last = OperationResult.CreateFailure($"{operationLabel} was not attempted");

            for (int attemptNo = 1; attemptNo <= maxAttempts; attemptNo++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Layer a per-attempt timeout on top of the caller's token.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(ContentPackageSettings.DownloadTimeoutSeconds));

                try
                {
                    last = await attempt(timeoutCts.Token);
                    if (last.Success || last.WasCancelled)
                        return last;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // genuine caller cancellation — do not retry
                }
                catch (OperationCanceledException)
                {
                    // Per-attempt timeout — treat as a retryable failure.
                    last = OperationResult.CreateFailure(
                        $"{operationLabel} timed out after {ContentPackageSettings.DownloadTimeoutSeconds}s");
                }
                catch (Exception ex)
                {
                    last = OperationResult.CreateFailure(ex.Message);
                }

                if (attemptNo < maxAttempts)
                {
                    LogWarning($"[PackageService] {operationLabel} attempt {attemptNo}/{maxAttempts} failed: " +
                               $"{last.ErrorMessage}. Retrying in {delay:0.#}s");
                    await Awaitable.WaitForSecondsAsync(delay, cancellationToken);
                    delay = Mathf.Min(delay * 2f, ContentPackageSettings.MaxRetryDelay);
                }
                else if (maxAttempts > 1)
                {
                    LogWarning($"[PackageService] {operationLabel} failed after {maxAttempts} attempts: {last.ErrorMessage}");
                }
            }

            return last;
        }

        #endregion

        #region Package Uninstallation

        /// <summary>
        /// Uninstalls a package asynchronously after validating that it can be safely removed.
        /// Validates package existence, checks installation status, verifies no installed dependents exist,
        /// and clears Addressables cache for the package content.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package to uninstall.</param>
        /// <param name="cancellationToken">Token to cancel the uninstallation operation.</param>
        /// <returns>An OperationResult indicating success or failure of the uninstallation.</returns>
        public async Awaitable<OperationResult> UninstallPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default)
        {
            // 1. Validate package exists
            if (string.IsNullOrEmpty(packageId))
            {
                return OperationResult.CreateFailure("Package ID cannot be null or empty");
            }

            if (!_definitions.TryGetValue(packageId, out var definition))
            {
                return OperationResult.CreateFailure($"Package '{packageId}' not found in definitions");
            }

            Log($"[PackageService] Starting uninstallation of package: {packageId}");

            // 2. Check if installed
            var state = GetOrCreateState(packageId);
            if (state == null || !state.IsInstalled)
            {
                Log($"[PackageService] Package '{packageId}' is not installed, uninstallation is a no-op");
                return OperationResult.CreateSuccess();
            }

            // 3. Check for dependents using existing validation method
            var validationResult = ValidatePackageUninstallation(packageId);
            if (!validationResult.Success)
            {
                LogWarning($"[PackageService] Cannot uninstall package '{packageId}': {validationResult.ErrorMessage}");
                return validationResult; // Return the detailed error message from validation
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // 4. Clear Addressables bundle cache
            Log($"[PackageService] Clearing Addressables cache for package: {packageId}");
            var clearResult = await ClearPackageCacheAsync(packageId, definition, cancellationToken);
            if (!clearResult.Success)
            {
                if (clearResult.WasCancelled)
                {
                    Log($"[PackageService] Uninstallation of package '{packageId}' was cancelled");
                    return TrackReturn("content_package.uninstall", packageId, clearResult, stopwatch);
                }
                LogError($"[PackageService] Error during uninstallation of package '{packageId}': {clearResult.ErrorMessage}");
                UpdateState(packageId, PackageStatus.Failed, $"Uninstallation failed: {clearResult.ErrorMessage}");
                return TrackReturn("content_package.uninstall", packageId,
                    OperationResult.CreateFailure($"Uninstallation failed: {clearResult.ErrorMessage}"), stopwatch);
            }

            // 5. Update state to Available
            UpdateState(packageId, PackageStatus.Available);
            Log($"[PackageService] Successfully uninstalled package: {packageId}");
            return TrackReturn("content_package.uninstall", packageId, OperationResult.CreateSuccess(), stopwatch);
        }

        /// <summary>
        /// Updates an installed package by clearing its cached bundles and re-downloading the latest
        /// content from the remote catalog.
        /// Only bundles whose hash has changed since the last install are re-downloaded; unchanged
        /// bundles are served from the Addressables cache.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package to update.</param>
        /// <param name="progress">Optional progress reporter for download progress (0.0 to 1.0).</param>
        /// <param name="cancellationToken">Token to cancel the update operation.</param>
        /// <returns>An OperationResult indicating success or failure of the update.</returns>
        public async Awaitable<OperationResult> UpdatePackageAsync(
            string packageId,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(packageId))
                return OperationResult.CreateFailure("Package ID cannot be null or empty");

            if (!_definitions.TryGetValue(packageId, out var definition))
                return OperationResult.CreateFailure($"Package '{packageId}' not found in definitions");

            var state = GetOrCreateState(packageId);
            if (state == null || (!state.IsInstalled && state.status != PackageStatus.UpdateAvailable))
            {
                return OperationResult.CreateFailure(
                    $"Package '{packageId}' is not installed. Use InstallPackageAsync instead.");
            }

            Log($"[PackageService] Starting update for package: {packageId}");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Clear cached bundles so Addressables fetches the latest versions.
                // Bundles that haven't changed will be re-cached from CDN; unchanged ones may
                // already be present under the new hash from the catalog refresh.
                var clearResult = await ClearPackageCacheAsync(packageId, definition, cancellationToken);
                if (!clearResult.Success)
                    return TrackReturn("content_package.update", packageId, clearResult, stopwatch);

                // Reset to Available so InstallPackageAsync proceeds past the IsInstalled guard.
                UpdateState(packageId, PackageStatus.Available);

                // The inner install also emits a content_package.install event; this update event
                // records the overall update outcome (clear + re-download) and its total duration.
                var installResult = await InstallPackageAsync(packageId, progress, cancellationToken);
                long bytes = GetOrCreateState(packageId).totalBytes;
                return TrackReturn("content_package.update", packageId, installResult, stopwatch, bytes);
            }
            catch (OperationCanceledException)
            {
                Log($"[PackageService] Update of package '{packageId}' was cancelled");
                return TrackReturn("content_package.update", packageId, OperationResult.CreateCancelled(), stopwatch);
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Unexpected error during update of package '{packageId}': {ex.Message}");
                UpdateState(packageId, PackageStatus.Failed, ex.Message);
                return TrackReturn("content_package.update", packageId,
                    OperationResult.CreateFailure($"Unexpected error: {ex.Message}"), stopwatch);
            }
        }

        /// <summary>Clears the Addressables bundle cache for every label in <paramref name="definition"/>.</summary>
        private async Awaitable<OperationResult> ClearPackageCacheAsync(
            string packageId,
            ContentPackageSettings.PackageConfig definition,
            CancellationToken cancellationToken)
        {
            if (definition.addressableLabels == null || definition.addressableLabels.Length == 0)
            {
                LogWarning($"[PackageService] Package '{packageId}' has no Addressables labels, skipping cache clear");
                return OperationResult.CreateSuccess();
            }

            try
            {
                foreach (var label in definition.addressableLabels)
                {
                    if (string.IsNullOrEmpty(label)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    Log($"[PackageService] Clearing cache for label: {label}");
                    await Addressables.ClearDependencyCacheAsync(label, false).Task;
                }
                return OperationResult.CreateSuccess();
            }
            catch (OperationCanceledException)
            {
                return OperationResult.CreateCancelled();
            }
            catch (Exception ex)
            {
                return OperationResult.CreateFailure($"Cache clear failed: {ex.Message}");
            }
        }

        #endregion

        #region Public Query Methods

        /// <summary>
        /// Gets the current state of a package.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package.</param>
        /// <returns>The PackageState for the specified package, or null if the package ID is invalid.</returns>
        /// <summary>
        /// Returns the remote metadata entry for a package fetched from the CDN manifest,
        /// or <c>null</c> if the manifest has not been loaded or does not contain this package.
        /// Use this for display values (version, description, changelog, bundle size) that
        /// are updated on CDN without requiring an app binary update.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package.</param>
        public RemotePackageEntry GetRemoteMetadata(string packageId)
            => _remoteManifest?.FindPackage(packageId);

        public PackageState GetPackageState(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                LogWarning("[PackageService] GetPackageState called with null or empty packageId");
                return null;
            }

            return GetOrCreateState(packageId);
        }

        /// <summary>
        /// Gets a list of all installed packages.
        /// </summary>
        /// <returns>A list of PackageState objects for all packages that are currently installed.</returns>
        public List<PackageState> GetInstalledPackages()
        {
            try
            {
                // Enumerate definitions (not _states) so packages whose state has never been
                // materialized are still considered. GetOrCreateState is idempotent.
                var installedPackages = _definitions.Keys
                    .Select(GetOrCreateState)
                    .Where(state => state != null && state.IsInstalled)
                    .ToList();

                Log($"[PackageService] Found {installedPackages.Count} installed packages");
                return installedPackages;
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Error while getting installed packages: {ex.Message}");
                return new List<PackageState>();
            }
        }

        /// <summary>
        /// Gets a list of all available packages (not installed).
        /// </summary>
        /// <returns>A list of PackageState objects for all packages that are available for installation.</returns>
        public List<PackageState> GetAvailablePackages()
        {
            try
            {
                // Enumerate definitions so never-touched packages appear as Available, not just
                // those that happen to have a persisted/materialized state.
                var availablePackages = _definitions.Keys
                    .Select(GetOrCreateState)
                    .Where(state => state != null && state.status == PackageStatus.Available)
                    .ToList();

                Log($"[PackageService] Found {availablePackages.Count} available packages");
                return availablePackages;
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Error while getting available packages: {ex.Message}");
                return new List<PackageState>();
            }
        }

        /// <summary>
        /// Checks if a specific package is currently installed.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package to check.</param>
        /// <returns>True if the package is installed, false otherwise.</returns>
        public bool IsPackageInstalled(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                LogWarning("[PackageService] IsPackageInstalled called with null or empty packageId");
                return false;
            }

            try
            {
                var state = GetOrCreateState(packageId);
                bool isInstalled = state?.IsInstalled ?? false;
                
                Log($"[PackageService] Package '{packageId}' installed status: {isInstalled}");
                return isInstalled;
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Error while checking if package '{packageId}' is installed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Calculates the total download size for a package and all its uninstalled dependencies.
        /// Merges all labels from the package and uninstalled dependencies into a single
        /// <see cref="Addressables.GetDownloadSizeAsync"/> call so that bundles shared across
        /// packages are counted only once.
        /// </summary>
        /// <param name="packageId">The unique identifier of the package.</param>
        /// <returns>The total download size in bytes, or the manifest's estimated size if the query fails.</returns>
        public async Awaitable<long> GetDownloadSizeAsync(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                LogWarning("[PackageService] GetDownloadSizeAsync called with null or empty packageId");
                return 0;
            }

            if (!_definitions.ContainsKey(packageId))
            {
                LogWarning($"[PackageService] Package '{packageId}' not found in definitions");
                return 0;
            }

            Log($"[PackageService] Calculating download size for package: {packageId}");

            try
            {
                // Collect labels from this package + every uninstalled dependency in one pass
                // so Addressables can deduplicate shared bundles across the whole set.
                var dependencyResult = ResolveDependencies(packageId);
                var allIds = dependencyResult.Success
                    ? dependencyResult.Data
                    : new List<string> { packageId };

                var mergedLabels = new List<object>();
                foreach (var id in allIds)
                {
                    if (!_definitions.TryGetValue(id, out var def)) continue;
                    var state = GetOrCreateState(id);
                    if (state != null && state.IsInstalled) continue; // already cached — skip

                    if (def.addressableLabels != null)
                        foreach (var label in def.addressableLabels)
                            if (!string.IsNullOrEmpty(label) && !mergedLabels.Contains(label))
                                mergedLabels.Add(label);
                }

                if (mergedLabels.Count == 0)
                    return 0;

                Log($"[PackageService] Querying download size with {mergedLabels.Count} merged label(s) for '{packageId}'");
                var handle = Addressables.GetDownloadSizeAsync(mergedLabels);
                await handle.Task;

                long size = 0;
                if (handle.Status == AsyncOperationStatus.Succeeded)
                {
                    size = handle.Result;
                    Log($"[PackageService] Download size for '{packageId}' (incl. deps): {size} bytes");
                }
                else
                {
                    LogWarning($"[PackageService] Failed to get download size for '{packageId}': {handle.OperationException?.Message}");
                    var remoteEntry = _remoteManifest?.FindPackage(packageId);
                    size = remoteEntry?.bundleSizeBytes ?? 0;
                }

                if (handle.IsValid()) Addressables.Release(handle);
                return size;
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Error calculating download size for package '{packageId}': {ex.Message}");
                var remoteEntry = _remoteManifest?.FindPackage(packageId);
                return remoteEntry?.bundleSizeBytes ?? 0;
            }
        }

        #endregion

        #region Cache Budget Management

        /// <summary>
        /// Total approximate on-disk size of all installed packages, in bytes, from each package's
        /// <see cref="PackageState.installedSizeBytes"/> captured at install time.
        /// </summary>
        public long GetCacheUsageBytes()
        {
            long total = 0;
            foreach (var id in _definitions.Keys)
            {
                var state = GetOrCreateState(id);
                if (state != null && state.IsInstalled)
                    total += Math.Max(0, state.installedSizeBytes);
            }
            return total;
        }

        /// <summary>
        /// Free space in bytes on the volume backing <see cref="Application.persistentDataPath"/>,
        /// or <c>-1</c> when it cannot be determined (unsupported platform). Callers treat a
        /// negative value as "unknown — do not block".
        /// </summary>
        public long GetAvailableDiskBytes()
        {
            try
            {
                var root = System.IO.Directory.GetDirectoryRoot(Application.persistentDataPath);
                return new System.IO.DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                Log($"[PackageService] Could not determine free disk space: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Returns installed, non-required package IDs in eviction order (least-recently-modified
        /// first), accumulating until <paramref name="bytesToFree"/> is covered. Pass
        /// <c>bytesToFree &lt;= 0</c> to return every eviction-eligible package.
        /// Required packages and <paramref name="excludePackageId"/> are never included.
        /// </summary>
        /// <remarks>
        /// Pure selection only — it does not check installed dependents; <see cref="FreeUpSpaceAsync"/>
        /// performs the safe uninstall (which validates dependents) and skips any that cannot be removed.
        /// </remarks>
        public List<string> GetEvictionCandidates(long bytesToFree, string excludePackageId = null)
        {
            var ordered = _definitions.Values
                .Where(c => c != null && !c.isRequired && c.packageId != excludePackageId)
                .Select(c => GetOrCreateState(c.packageId))
                .Where(s => s != null && s.IsInstalled)
                .OrderBy(s => ParseTimestamp(s.lastModified))
                .ToList();

            var result = new List<string>();
            long freed = 0;
            foreach (var state in ordered)
            {
                if (bytesToFree > 0 && freed >= bytesToFree) break;
                result.Add(state.packageId);
                freed += Math.Max(0, state.installedSizeBytes);
            }
            return result;
        }

        /// <summary>
        /// Evicts least-recently-used non-required packages until at least
        /// <paramref name="bytesToFree"/> has been freed (or no further candidates remain).
        /// This is the "free up space" affordance; each eviction is a safe uninstall.
        /// </summary>
        /// <param name="bytesToFree">Target bytes to free; <c>&lt;= 0</c> evicts all eligible packages.</param>
        /// <param name="cancellationToken">Cancels the eviction loop.</param>
        /// <param name="excludePackageId">A package to never evict (e.g. one being installed).</param>
        /// <returns>Success with the freed-byte total available via the log; cancelled if aborted.</returns>
        public async Awaitable<OperationResult> FreeUpSpaceAsync(
            long bytesToFree, CancellationToken cancellationToken = default, string excludePackageId = null)
        {
            long freed = 0;
            foreach (var id in GetEvictionCandidates(bytesToFree, excludePackageId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long size = Math.Max(0, GetOrCreateState(id)?.installedSizeBytes ?? 0);

                var result = await UninstallPackageAsync(id, cancellationToken);
                if (result.WasCancelled)
                    return result;
                if (result.Success)
                {
                    freed += size;
                    Log($"[PackageService] Evicted '{id}' to free space ({size} bytes)");
                }
                else
                {
                    LogWarning($"[PackageService] Could not evict '{id}': {result.ErrorMessage}");
                }

                if (bytesToFree > 0 && freed >= bytesToFree) break;
            }

            Log($"[PackageService] FreeUpSpace complete: freed {freed} bytes");
            return OperationResult.CreateSuccess();
        }

        /// <summary>
        /// Enforces <see cref="ContentPackageSettings.MaxCacheBytes"/> by evicting LRU non-required
        /// packages when the cap is set and current usage exceeds it. No-op when the cap is 0.
        /// </summary>
        public async Awaitable EnforceCacheBudgetAsync(CancellationToken cancellationToken = default)
        {
            long cap = _settings.MaxCacheBytes;
            if (cap <= 0) return;

            long usage = GetCacheUsageBytes();
            if (usage <= cap) return;

            Log($"[PackageService] Cache usage {usage} exceeds cap {cap}; evicting {usage - cap} bytes");
            await FreeUpSpaceAsync(usage - cap, cancellationToken);
        }

        /// <summary>
        /// Pre-install space check: makes room for an upcoming download of <paramref name="packageId"/>.
        /// Honours the cache cap (evicting LRU to keep projected usage under it) and the available
        /// disk space (evicting, then failing if still insufficient). Best-effort when disk space
        /// is unknown.
        /// </summary>
        private async Awaitable<OperationResult> EnsureInstallSpaceAsync(string packageId, CancellationToken cancellationToken)
        {
            long needed = await GetDownloadSizeAsync(packageId);
            if (needed <= 0) return OperationResult.CreateSuccess();

            long cap = _settings.MaxCacheBytes;
            if (cap > 0)
            {
                long projected = GetCacheUsageBytes() + needed;
                if (projected > cap)
                    await FreeUpSpaceAsync(projected - cap, cancellationToken, excludePackageId: packageId);
            }

            long avail = GetAvailableDiskBytes();
            if (avail >= 0 && avail < needed)
            {
                await FreeUpSpaceAsync(needed - avail, cancellationToken, excludePackageId: packageId);
                avail = GetAvailableDiskBytes();
                if (avail >= 0 && avail < needed)
                    return OperationResult.CreateFailure(
                        $"Insufficient disk space to install '{packageId}': need {needed} bytes, {avail} free after eviction.");
            }

            return OperationResult.CreateSuccess();
        }

        /// <summary>Parses an ISO-8601 timestamp, returning <see cref="DateTime.MinValue"/> on failure.</summary>
        private static DateTime ParseTimestamp(string iso)
        {
            return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt : DateTime.MinValue;
        }

        #endregion

        #region Dependency Validation

        /// <summary>
        /// Gets all installed packages that depend on the specified package.
        /// This method is used to validate whether a package can be safely uninstalled.
        /// </summary>
        /// <param name="packageId">The package ID to check for dependents.</param>
        /// <returns>A list of <see cref="PackageConfig"/> objects representing installed packages that depend on the specified package.</returns>
        public List<ContentPackageSettings.PackageConfig> GetInstalledDependents(string packageId)
        {
            var dependents = new List<ContentPackageSettings.PackageConfig>();

            if (string.IsNullOrEmpty(packageId))
            {
                LogWarning("[PackageService] GetInstalledDependents called with null or empty packageId");
                return dependents;
            }

            try
            {
                // Iterate through all package definitions
                foreach (var kvp in _definitions)
                {
                    var definition = kvp.Value;
                    var state = GetOrCreateState(definition.packageId);

                    // Only consider installed packages
                    if (state != null && state.IsInstalled)
                    {
                        // Check if this installed package depends on the target package
                        if (GetDependencyIds(definition).Contains(packageId))
                        {
                            dependents.Add(definition);
                            Log($"[PackageService] Found dependent package: '{definition.packageId}' depends on '{packageId}'");
                        }
                    }
                }

                Log($"[PackageService] Found {dependents.Count} installed dependents for package '{packageId}'");
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Error while finding dependents for package '{packageId}': {ex.Message}");
            }

            return dependents;
        }

        /// <summary>
        /// Validates whether a package can be safely uninstalled by checking for installed dependents.
        /// </summary>
        /// <param name="packageId">The package ID to validate for uninstallation.</param>
        /// <returns>An OperationResult indicating whether the package can be uninstalled. If not, includes error message listing dependent packages.</returns>
        public OperationResult ValidatePackageUninstallation(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                return OperationResult.CreateFailure("Package ID cannot be null or empty");
            }

            // Check if package exists
            if (!_definitions.TryGetValue(packageId, out var definition))
            {
                return OperationResult.CreateFailure($"Package '{packageId}' not found");
            }

            // Check if package is installed
            var state = GetOrCreateState(packageId);
            if (state == null || !state.IsInstalled)
            {
                // Package is not installed, so it can be "uninstalled" (no-op)
                return OperationResult.CreateSuccess();
            }

            // Check for installed dependents
            var dependents = GetInstalledDependents(packageId);
            if (dependents.Count > 0)
            {
                // Create clear error message listing dependent packages
                var dependentNames = dependents.Select(d => $"'{d.displayName}' ({d.packageId})").ToArray();
                string dependentList = string.Join(", ", dependentNames);
                
                string errorMessage = $"Cannot uninstall package '{definition.displayName}' ({packageId}): " +
                                    $"it is required by {dependents.Count} installed package{(dependents.Count > 1 ? "s" : "")}: {dependentList}. " +
                                    $"Please uninstall the dependent packages first.";

                LogWarning($"[PackageService] {errorMessage}");
                return OperationResult.CreateFailure(errorMessage);
            }

            // Package can be safely uninstalled
            Log($"[PackageService] Package '{packageId}' can be safely uninstalled (no dependents found)");
            return OperationResult.CreateSuccess();
        }

        #endregion

        #region Catalog Operations

        /// <summary>
        /// Refreshes the Addressables catalog from remote sources and checks for package updates.
        /// Uses Addressables' built-in catalog update functionality to check for and download updated catalogs.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the catalog refresh operation.</param>
        /// <returns>An OperationResult indicating success or failure of the catalog refresh.</returns>
        public async Awaitable<OperationResult> RefreshCatalogAsync(CancellationToken cancellationToken = default)
        {
            Log("[PackageService] Starting catalog refresh...");

            try
            {
                // 1. Fetch packages.json first — it contains the current catalogUrl so the app
                //    never depends on a hash-based URL baked into the binary.
                await FetchRemoteManifestAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    return OperationResult.CreateCancelled();

                // 2. Load the Addressables catalog.
                //    Primary path: use catalogUrl from packages.json (survives catalog hash changes).
                //    Fallback: CheckForCatalogUpdates using the URL baked at build time.
                var dynamicCatalogUrl = _remoteManifest?.catalogUrl;
                bool usedDynamicCatalog = !string.IsNullOrEmpty(dynamicCatalogUrl);

                if (usedDynamicCatalog)
                {
                    Log($"[PackageService] Loading catalog from packages.json URL: {dynamicCatalogUrl}");
                    var loadHandle = Addressables.LoadContentCatalogAsync(dynamicCatalogUrl, false);

                    while (!loadHandle.IsDone)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            if (loadHandle.IsValid()) Addressables.Release(loadHandle);
                            return OperationResult.CreateCancelled();
                        }
                        await Awaitable.NextFrameAsync();
                    }

                    if (loadHandle.Status != AsyncOperationStatus.Succeeded)
                    {
                        string error = loadHandle.OperationException?.Message ?? "Unknown error";
                        LogError($"[PackageService] Failed to load catalog from packages.json URL: {error}");
                        if (loadHandle.IsValid()) Addressables.Release(loadHandle);
                        return OperationResult.CreateFailure($"Failed to load catalog: {error}");
                    }

                    if (loadHandle.IsValid()) Addressables.Release(loadHandle);
                    Log("[PackageService] Catalog loaded successfully via packages.json URL");
                }
                else
                {
                    // Fallback: packages.json has no catalogUrl (old deploy or first run).
                    // Use the standard CheckForCatalogUpdates path with the baked-in URL.
                    Log("[PackageService] No catalogUrl in packages.json — falling back to CheckForCatalogUpdates");

                    var checkHandle = Addressables.CheckForCatalogUpdates(false);
                    while (!checkHandle.IsDone)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            if (checkHandle.IsValid()) Addressables.Release(checkHandle);
                            return OperationResult.CreateCancelled();
                        }
                        await Awaitable.NextFrameAsync();
                    }

                    if (checkHandle.Status != AsyncOperationStatus.Succeeded)
                    {
                        string error = checkHandle.OperationException?.Message ?? "Unknown error";
                        LogError($"[PackageService] Failed to check for catalog updates: {error}");
                        if (checkHandle.IsValid()) Addressables.Release(checkHandle);
                        return OperationResult.CreateFailure($"Failed to check for catalog updates: {error}");
                    }

                    var catalogsToUpdate = checkHandle.Result;
                    if (checkHandle.IsValid()) Addressables.Release(checkHandle);

                    if (catalogsToUpdate != null && catalogsToUpdate.Count > 0)
                    {
                        var updateHandle = Addressables.UpdateCatalogs(catalogsToUpdate, false);
                        while (!updateHandle.IsDone)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                if (updateHandle.IsValid()) Addressables.Release(updateHandle);
                                return OperationResult.CreateCancelled();
                            }
                            await Awaitable.NextFrameAsync();
                        }

                        if (updateHandle.Status != AsyncOperationStatus.Succeeded)
                        {
                            string error = updateHandle.OperationException?.Message ?? "Unknown error";
                            LogError($"[PackageService] Failed to update catalogs: {error}");
                            if (updateHandle.IsValid()) Addressables.Release(updateHandle);
                            return OperationResult.CreateFailure($"Failed to update catalogs: {error}");
                        }

                        if (updateHandle.IsValid()) Addressables.Release(updateHandle);
                        Log("[PackageService] Catalogs updated via fallback path");
                    }
                    else
                    {
                        Log("[PackageService] No catalog updates available");
                    }
                }

                // 3. Check for package updates now that the catalog is current.
                await CheckForPackageUpdatesAsync();

                // 4. Dispatch catalog refreshed event.
                OnCatalogRefreshed?.Invoke();

                Log("[PackageService] Catalog refresh completed successfully");
                return OperationResult.CreateSuccess();
            }
            catch (OperationCanceledException)
            {
                Log("[PackageService] Catalog refresh was cancelled");
                return OperationResult.CreateCancelled();
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Unexpected error during catalog refresh: {ex.Message}");
                return OperationResult.CreateFailure($"Catalog refresh failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Fetches the remote <c>packages.json</c> from CDN. When the response is schema v2
        /// (<see cref="ContentVersionIndex"/>) it populates <see cref="_versionIndex"/> and
        /// resolves <see cref="_remoteManifest"/> from the installed (or latest) version entry.
        /// Schema v1 responses are parsed directly into <see cref="_remoteManifest"/> as before.
        /// Failure is non-fatal. Updates <see cref="CloudStatus"/> and fires
        /// <see cref="OnCloudStatusChanged"/> on every attempt regardless of outcome.
        /// </summary>
        private async Awaitable FetchRemoteManifestAsync(CancellationToken cancellationToken)
        {
            var url = _settings.RemotePackagesManifestUrl;
            if (string.IsNullOrEmpty(url))
            {
                Log("[PackageService] RemotePackagesManifestUrl not configured — skipping remote manifest fetch");
                UpdateCloudStatus(CloudConnectionState.NotConfigured, errorMessage: null);
                return;
            }

            try
            {
                Log($"[PackageService] Fetching remote manifest from: {url}");

                var fetchResult = await FetchTextWithRetryAsync(url, 30, cancellationToken);
                if (!fetchResult.Success)
                {
                    LogWarning($"[PackageService] Failed to fetch packages.json: {fetchResult.ErrorMessage}");
                    UpdateCloudStatus(CloudConnectionState.Unreachable, errorMessage: fetchResult.ErrorMessage);
                    return;
                }

                var json = fetchResult.Data;

                // Probe schema version before committing to a full deserialize.
                // v2 path is only active when content versioning is enabled in settings.
                var probe = JsonUtility.FromJson<SchemaProbe>(json);
                if (_settings.EnableContentVersioning && probe?.schemaVersion == "2")
                {
                    var index = JsonUtility.FromJson<ContentVersionIndex>(json);
                    if (index == null || index.versions == null || index.versions.Count == 0)
                    {
                        LogWarning("[PackageService] packages.json (v2) parsed to empty index — ignoring");
                        UpdateCloudStatus(CloudConnectionState.Unreachable, errorMessage: "packages.json v2 could not be parsed");
                        return;
                    }

                    _versionIndex = index;
                    Log($"[PackageService] Version index loaded: {index.versions.Count} version(s), latest '{index.latestVersion}'");

                    // Use the per-version manifest for the currently installed or latest version.
                    var installedVersion = _manifest.InstalledContentVersion;
                    var resolvedEntry    = (!string.IsNullOrEmpty(installedVersion)
                        ? index.versions.Find(e => e.version == installedVersion)
                        : null) ?? index.versions.Find(e => e.version == index.latestVersion)
                                       ?? index.versions[0];

                    // _remoteManifest will be populated once SwitchContentVersionAsync or
                    // the per-version manifest URL is fetched; set catalogUrl from the entry
                    // so the existing RefreshCatalogAsync catalog-load path continues to work.
                    _remoteManifest = new RemotePackageManifest
                    {
                        schemaVersion = "1",
                        catalogUrl    = resolvedEntry.catalogUrl,
                        generatedAt   = index.generatedAt,
                    };

                    UpdateCloudStatus(CloudConnectionState.Connected, errorMessage: null);
                    return;
                }

                // Schema v1 — original flat manifest.
                var manifest = JsonUtility.FromJson<RemotePackageManifest>(json);
                if (manifest == null)
                {
                    LogWarning("[PackageService] packages.json parsed to null — ignoring");
                    UpdateCloudStatus(CloudConnectionState.Unreachable, errorMessage: "packages.json could not be parsed");
                    return;
                }

                _remoteManifest = manifest;
                Log($"[PackageService] Remote manifest loaded: {_remoteManifest.packages?.Count ?? 0} package(s), generated {_remoteManifest.generatedAt}");
                UpdateCloudStatus(CloudConnectionState.Connected, errorMessage: null);
            }
            catch (OperationCanceledException)
            {
                Log("[PackageService] Remote manifest fetch cancelled");
                // Cancelled fetches do not transition the cloud status — keep whatever was last known.
            }
            catch (Exception ex)
            {
                LogWarning($"[PackageService] Remote manifest fetch failed: {ex.Message}");
                UpdateCloudStatus(CloudConnectionState.Unreachable, errorMessage: ex.Message);
            }
        }

        /// <summary>Minimal deserialize target used to read schemaVersion before full parsing.</summary>
        [Serializable]
        private class SchemaProbe { public string schemaVersion; }

        /// <summary>
        /// HTTP GET with retry-on-failure and exponential backoff, using the same retry/backoff
        /// budget as package downloads (<see cref="ContentPackageSettings.MaxRetryAttempts"/>,
        /// <see cref="ContentPackageSettings.InitialRetryDelay"/>,
        /// <see cref="ContentPackageSettings.MaxRetryDelay"/>). Each attempt uses
        /// <paramref name="timeoutSeconds"/> as the per-request timeout.
        /// </summary>
        /// <returns>
        /// A success result carrying the response body, or a failure result with the last HTTP error.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> fires.
        /// </returns>
        private async Awaitable<OperationResult<string>> FetchTextWithRetryAsync(
            string url, int timeoutSeconds, CancellationToken cancellationToken)
        {
            int maxAttempts = Mathf.Max(1, _settings.MaxRetryAttempts);
            float delay = ContentPackageSettings.InitialRetryDelay;
            var last = OperationResult<string>.CreateFailure($"GET {url} was not attempted");

            for (int attemptNo = 1; attemptNo <= maxAttempts; attemptNo++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var req = UnityWebRequest.Get(url);
                req.timeout = timeoutSeconds;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Awaitable.NextFrameAsync(cancellationToken);
                }

                if (req.result == UnityWebRequest.Result.Success)
                    return OperationResult<string>.CreateSuccess(req.downloadHandler.text);

                last = OperationResult<string>.CreateFailure($"HTTP {req.responseCode}: {req.error}");

                if (attemptNo < maxAttempts)
                {
                    LogWarning($"[PackageService] GET {url} attempt {attemptNo}/{maxAttempts} failed: " +
                               $"{last.ErrorMessage}. Retrying in {delay:0.#}s");
                    await Awaitable.WaitForSecondsAsync(delay, cancellationToken);
                    delay = Mathf.Min(delay * 2f, ContentPackageSettings.MaxRetryDelay);
                }
            }

            return last;
        }

        /// <summary>
        /// Writes a new snapshot into <see cref="CloudStatus"/> and fires
        /// <see cref="OnCloudStatusChanged"/>. On <see cref="CloudConnectionState.Connected"/>,
        /// also captures the sync time and manifest metadata.
        /// </summary>
        private void UpdateCloudStatus(CloudConnectionState state, string errorMessage)
        {
            CloudStatus.State = state;
            CloudStatus.ErrorMessage = errorMessage;

            if (state == CloudConnectionState.Connected && _remoteManifest != null)
            {
                CloudStatus.LastSyncTime = DateTime.UtcNow;
                CloudStatus.ManifestGeneratedAt = _remoteManifest.generatedAt;
                CloudStatus.RemotePackageCount = _remoteManifest.packages?.Count ?? 0;
            }

            // Pass a snapshot so subscribers receive a stable value rather than
            // a reference to the live mutable CloudStatus object.
            OnCloudStatusChanged?.Invoke(CloudStatus.Clone());
        }

        /// <summary>
        /// Checks for updates to installed packages.
        /// When a remote manifest is available, compares <see cref="RemotePackageEntry.version"/> against
        /// <see cref="PackageState.installedVersion"/> — this is reliable because the build pipeline
        /// increments the version in <c>packages.json</c> on every CDN push.
        /// Falls back to Addressables download-size detection when no remote manifest is loaded (offline).
        /// </summary>
        private async Awaitable CheckForPackageUpdatesAsync()
        {
            Log("[PackageService] Checking for package updates...");

            try
            {
                int updatesFound = 0;

                foreach (var state in _states.Values.Where(s => s.IsInstalled).ToList())
                {
                    if (!_definitions.TryGetValue(state.packageId, out var definition)) continue;

                    // Primary: remote manifest version comparison (accurate — version updated by build pipeline)
                    var remoteEntry = _remoteManifest?.FindPackage(state.packageId);
                    if (remoteEntry != null)
                    {
                        if (!string.IsNullOrEmpty(remoteEntry.version) &&
                            remoteEntry.version != state.installedVersion)
                        {
                            Log($"[PackageService] Update available for '{state.packageId}': " +
                                      $"{state.installedVersion} → {remoteEntry.version}");
                            UpdateState(state.packageId, PackageStatus.UpdateAvailable);
                            updatesFound++;
                        }
                        continue;
                    }

                    // Fallback: Addressables download-size check (used when offline or manifest not fetched)
                    var validLabels = definition.addressableLabels?
                        .Where(l => !string.IsNullOrEmpty(l))
                        .Cast<object>()
                        .ToList();

                    if (validLabels == null || validLabels.Count == 0) continue;

                    var handle = Addressables.GetDownloadSizeAsync(validLabels);
                    await handle.Task;

                    if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result > 0)
                    {
                        Log($"[PackageService] Update available for '{state.packageId}' " +
                                  $"(offline fallback — pending download: {handle.Result} bytes)");
                        UpdateState(state.packageId, PackageStatus.UpdateAvailable);
                        updatesFound++;
                    }

                    if (handle.IsValid())
                        Addressables.Release(handle);
                }

                Log($"[PackageService] Found {updatesFound} package update(s)");
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Error while checking for package updates: {ex.Message}");
            }
        }

        #endregion

        #region Content Version Management

        /// <summary>
        /// Returns all content versions from the CDN index that are compatible with the
        /// given app version, sorted newest-first. Excludes deprecated entries.
        /// Returns an empty list when no version index has been fetched (schema v1 CDN or offline).
        /// </summary>
        /// <param name="appVersion">
        /// Running app version in semantic version format (e.g. <c>"2.1.0"</c>).
        /// Pass <see cref="ContentPackageSettings.AppVersion"/> here.
        /// </param>
        public List<ContentVersionEntry> GetCompatibleVersions(string appVersion)
        {
            if (!_settings.EnableContentVersioning || _versionIndex?.versions == null)
                return new List<ContentVersionEntry>();

            var result = new List<ContentVersionEntry>();
            foreach (var entry in _versionIndex.versions)
            {
                if (entry.isDeprecated) continue;
                if (!IsVersionCompatible(appVersion, entry.minAppVersion, entry.maxAppVersion)) continue;
                result.Add(entry);
            }
            return result;
        }

        /// <summary>
        /// Uninstalls the current content version and installs the specified target version.
        /// Clears all cached package bundles, loads the new Addressables catalog, fetches the
        /// per-version package manifest, resets all package states, and re-installs content.
        /// Safe to call with the version that is already installed — returns success immediately.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Content carry-forward: all <em>required</em> packages are re-installed, and every
        /// <em>optional</em> package that was installed before the switch is re-installed too, so
        /// the user's content is not silently dropped. An optional package that does not exist in
        /// the target version is skipped with a warning.
        /// </para>
        /// <para>
        /// Per-package download progress during these re-installs is reported via
        /// <see cref="OnDownloadProgress"/> rather than a progress parameter, because multiple
        /// sequential installs would each reset a single reporter to 0, producing confusing jumps.
        /// </para>
        /// </remarks>
        /// <param name="targetVersion">The version string to switch to (e.g. <c>"1.1.0"</c>).</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>An <see cref="OperationResult"/> indicating success or failure.</returns>
        public async Awaitable<OperationResult> SwitchContentVersionAsync(
            string targetVersion,
            CancellationToken cancellationToken = default)
        {
            if (!_settings.EnableContentVersioning)
                return OperationResult.CreateFailure("Content versioning is disabled. Enable it in ContentPackageSettings.");

            if (string.IsNullOrEmpty(targetVersion))
                return OperationResult.CreateFailure("Target version cannot be null or empty");

            if (_versionIndex == null)
                return OperationResult.CreateFailure("No version index available. Call RefreshCatalogAsync first.");

            var entry = _versionIndex.versions?.Find(e => e.version == targetVersion);
            if (entry == null)
                return OperationResult.CreateFailure($"Content version '{targetVersion}' not found in version index");

            if (!IsVersionCompatible(_settings.AppVersion, entry.minAppVersion, entry.maxAppVersion))
                return OperationResult.CreateFailure(
                    $"Content version '{targetVersion}' is not compatible with app version '{_settings.AppVersion}'");

            // No-op if already on this version and all required packages are installed.
            if (_manifest.InstalledContentVersion == targetVersion)
            {
                Log($"[PackageService] Content version '{targetVersion}' is already installed");
                return OperationResult.CreateSuccess();
            }

            var fromVersion = _manifest.InstalledContentVersion;
            Log($"[PackageService] Switching content version: {fromVersion ?? "none"} → {targetVersion}");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 0. Validate catalog is reachable BEFORE clearing any local cache.
                //    Without this guard a network failure after cache clear would leave
                //    the device with no installed content and no valid catalog to recover from.
                if (string.IsNullOrEmpty(entry.catalogUrl))
                    return OperationResult.CreateFailure($"Content version '{targetVersion}' has no catalogUrl");

                Log($"[PackageService] Pre-flight: checking catalog reachability at {entry.catalogUrl}");
                using (var probe = UnityWebRequest.Head(entry.catalogUrl))
                {
                    probe.timeout = 10;
                    var probeOp = probe.SendWebRequest();
                    while (!probeOp.isDone)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Awaitable.NextFrameAsync();
                    }
                    if (probe.result != UnityWebRequest.Result.Success)
                        return OperationResult.CreateFailure(
                            $"Catalog for version '{targetVersion}' is unreachable ({probe.responseCode}): {probe.error}. " +
                            "Local content has not been modified.");
                }

                // 1. Clear all installed package caches — safe because catalog is confirmed reachable.
                foreach (var state in _states.Values.Where(s => s.IsInstalled || s.HasUpdate).ToList())
                {
                    if (!_definitions.TryGetValue(state.packageId, out var def)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    await ClearPackageCacheAsync(state.packageId, def, cancellationToken);
                }

                // 2. Load the new Addressables catalog.

                Log($"[PackageService] Loading catalog for version {targetVersion}: {entry.catalogUrl}");
                var loadHandle = Addressables.LoadContentCatalogAsync(entry.catalogUrl, false);
                while (!loadHandle.IsDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Awaitable.NextFrameAsync();
                }

                if (loadHandle.Status != AsyncOperationStatus.Succeeded)
                {
                    string err = loadHandle.OperationException?.Message ?? "Unknown error";
                    if (loadHandle.IsValid()) Addressables.Release(loadHandle);
                    return OperationResult.CreateFailure($"Failed to load catalog for version '{targetVersion}': {err}");
                }
                if (loadHandle.IsValid()) Addressables.Release(loadHandle);

                // 3. Fetch the per-version package manifest.
                if (!string.IsNullOrEmpty(entry.manifestUrl))
                {
                    Log($"[PackageService] Fetching per-version manifest: {entry.manifestUrl}");
                    var manifestFetch = await FetchTextWithRetryAsync(entry.manifestUrl, 30, cancellationToken);

                    if (manifestFetch.Success)
                    {
                        var versionManifest = JsonUtility.FromJson<RemotePackageManifest>(manifestFetch.Data);
                        if (versionManifest != null)
                        {
                            _remoteManifest = versionManifest;
                            Log($"[PackageService] Per-version manifest loaded: {_remoteManifest.packages?.Count ?? 0} package(s)");
                        }
                    }
                    else
                    {
                        LogWarning($"[PackageService] Could not fetch per-version manifest: {manifestFetch.ErrorMessage}");
                    }
                }

                // 4. Capture previously-installed OPTIONAL packages before resetting state, so the
                //    user's content carries across the version switch instead of being silently
                //    dropped. Required packages are handled by step 6 and excluded here.
                var previouslyInstalledOptional = _states.Values
                    .Where(s => s.IsInstalled || s.HasUpdate)
                    .Select(s => s.packageId)
                    .Where(id => _definitions.TryGetValue(id, out var d) && d != null && !d.isRequired)
                    .Distinct()
                    .ToList();
                if (previouslyInstalledOptional.Count > 0)
                    Log($"[PackageService] {previouslyInstalledOptional.Count} optional package(s) will be re-installed after the version switch: [{string.Join(", ", previouslyInstalledOptional)}]");

                // 5. Reset all package states to Available — one batch write instead of N.
                foreach (var state in _states.Values)
                {
                    state.status           = PackageStatus.Available;
                    state.installedVersion = null;
                    state.downloadProgress = 0f;
                    state.downloadedBytes  = 0;
                    state.totalBytes       = 0;
                    state.errorMessage     = null;
                }
                _manifest.SetStatesBatch(_states.Values);

                // 6. Persist the new installed content version.
                _manifest.InstalledContentVersion = targetVersion;
                Log($"[PackageService] Content version switched to '{targetVersion}'");

                // 7. Re-install required packages.
                // Progress is not forwarded here — each install would reset it to 0,
                // producing confusing backwards jumps for the caller. The caller can
                // subscribe to OnDownloadProgress for per-package granularity instead.
                foreach (var config in _definitions.Values.Where(c => c.isRequired))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Log($"[PackageService] Re-installing required package '{config.packageId}' after version switch");
                    var result = await InstallPackageAsync(config.packageId, null, cancellationToken);
                    if (!result.Success && !result.WasCancelled)
                        LogWarning($"[PackageService] Required package '{config.packageId}' failed to re-install: {result.ErrorMessage}");
                }

                // 8. Re-install previously-installed optional packages so the user's content
                //    carries forward. A package that no longer exists in this version is skipped
                //    with a warning rather than silently lost.
                foreach (var pkgId in previouslyInstalledOptional)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_definitions.ContainsKey(pkgId))
                    {
                        LogWarning($"[PackageService] Optional package '{pkgId}' was installed before the switch but is not defined in version '{targetVersion}' — skipping.");
                        continue;
                    }
                    Log($"[PackageService] Re-installing optional package '{pkgId}' after version switch");
                    var result = await InstallPackageAsync(pkgId, null, cancellationToken);
                    if (!result.Success && !result.WasCancelled)
                        LogWarning($"[PackageService] Optional package '{pkgId}' failed to re-install: {result.ErrorMessage}");
                }

                OnCatalogRefreshed?.Invoke();
                return TrackVersionSwitch(targetVersion, fromVersion, OperationResult.CreateSuccess(), stopwatch);
            }
            catch (OperationCanceledException)
            {
                Log($"[PackageService] SwitchContentVersionAsync cancelled");
                return TrackVersionSwitch(targetVersion, fromVersion, OperationResult.CreateCancelled(), stopwatch);
            }
            catch (Exception ex)
            {
                LogError($"[PackageService] Unexpected error during version switch: {ex.Message}");
                return TrackVersionSwitch(targetVersion, fromVersion,
                    OperationResult.CreateFailure($"Version switch failed: {ex.Message}"), stopwatch);
            }
        }

        /// <summary>Emits a version-switch outcome to telemetry (no-op when telemetry is absent) and returns the result.</summary>
        private OperationResult TrackVersionSwitch(string targetVersion, string fromVersion,
            OperationResult result, System.Diagnostics.Stopwatch stopwatch)
        {
            if (_telemetry == null) return result;

            var props = new Dictionary<string, object>
            {
                { "targetVersion", targetVersion },
                { "fromVersion", fromVersion ?? "none" },
                { "success", result.Success },
                { "cancelled", result.WasCancelled },
                { "durationSeconds", stopwatch.Elapsed.TotalSeconds },
            };
            if (!result.Success && !result.WasCancelled && !string.IsNullOrEmpty(result.ErrorMessage))
                props["error"] = result.ErrorMessage;

            _telemetry.Track("content_package.version_switch", props);
            return result;
        }

        /// <summary>
        /// Returns true when <paramref name="appVersion"/> falls within [<paramref name="min"/>, <paramref name="max"/>].
        /// An empty bound is treated as unbounded. A non-empty but <em>unparseable</em> bound is also
        /// treated as unbounded, but logs a warning — a malformed CDN bound silently means
        /// "no limit", which could otherwise let incompatible content through unnoticed.
        /// </summary>
        /// <remarks>
        /// Uses <see cref="Version"/> parsing, which expects numeric dotted versions and does not
        /// understand semantic-version pre-release suffixes (e.g. <c>"1.2.0-beta"</c>); such a
        /// value parses as unbounded and warns.
        /// </remarks>
        private bool IsVersionCompatible(string appVersion, string min, string max)
        {
            if (!Version.TryParse(appVersion, out var v))
            {
                LogWarning($"[PackageService] App version '{appVersion}' is not a parseable numeric version; treating all content versions as compatible.");
                return true;
            }

            if (!string.IsNullOrEmpty(min))
            {
                if (!Version.TryParse(min, out var vMin))
                    LogWarning($"[PackageService] minAppVersion '{min}' is not parseable; treating as no lower bound.");
                else if (v < vMin)
                    return false;
            }

            if (!string.IsNullOrEmpty(max))
            {
                if (!Version.TryParse(max, out var vMax))
                    LogWarning($"[PackageService] maxAppVersion '{max}' is not parseable; treating as no upper bound.");
                else if (v > vMax)
                    return false;
            }

            return true;
        }

        #endregion

        #endregion
    }
}