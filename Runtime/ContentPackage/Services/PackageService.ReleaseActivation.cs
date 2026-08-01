using System;
using System.Linq;
using System.Threading;
using Molca.ContentPackage.Release;
using Molca.Networking;
using Molca.Licensing;
using UnityEngine;

namespace Molca.ContentPackage.Services
{
    /// <summary>
    /// The <c>contentRelease</c> protocol entry point on <see cref="PackageService"/>.
    /// </summary>
    /// <remarks>
    /// Kept in its own file so the release path is legible next to the legacy catalog path rather
    /// than threaded through it. The legacy path still works and is unchanged by enabling this one;
    /// the two are selected by <see cref="ContentPackageSettings.EnableReleaseProtocol"/> and never
    /// run together.
    ///
    /// Everything here fails closed. A project that turns the protocol on without a service id, a
    /// trusted key, or a build token gets an explicit refusal naming the missing piece — not a
    /// silent fall back to the old path, which would ship unsigned content under a setting that
    /// claims otherwise.
    /// </remarks>
    public partial class PackageService
    {
        private ReleaseActivationCoordinator _activationCoordinator;
        private ReleaseAccessProvider _accessProvider;
        private string _releaseSetupError;

        /// <summary>
        /// The active release manifest, or null when no release has been activated in this session.
        /// </summary>
        public ContentReleaseManifest ActiveReleaseManifest => _activationCoordinator?.ActiveManifest;

        /// <summary>Why the release protocol is unavailable, or empty when it is usable.</summary>
        public string ReleaseProtocolError => _releaseSetupError ?? string.Empty;

        /// <summary>True when this build resolves content through the signed release protocol.</summary>
        /// <remarks>
        /// Read by callers that must pick between the two content paths — notably the SDK UI's
        /// refresh action, which resolves a release here and refreshes the legacy catalog otherwise.
        /// The setting, not the coordinator, is the authority: a project with the protocol on but
        /// misconfigured is still a release-protocol project, and must be told what is missing rather
        /// than quietly served by the old path.
        /// </remarks>
        public bool IsReleaseProtocolEnabled => _settings != null && _settings.EnableReleaseProtocol;

        /// <summary>
        /// Raised as a release activation moves between phases and as required content transfers.
        /// </summary>
        /// <remarks>
        /// Re-broadcast from the coordinator rather than exposing the coordinator itself, because the
        /// coordinator is built lazily on first activation: a UI that subscribed directly would have
        /// to wait for an object that does not exist until the thing it wants to observe has already
        /// started.
        /// </remarks>
        public event Action<ReleaseActivationProgress> OnReleaseActivationProgress;

        /// <summary>Raised when a release commits, carrying the new active release id.</summary>
        public event Action<string> OnReleaseActivated;

        /// <summary>
        /// Resolves the active release for this build and adopts it if it is new.
        /// </summary>
        /// <remarks>
        /// Safe to call on every launch. A project with nothing promoted, a player whose app version
        /// is outside the release's range, and a player already on the current release are all
        /// ordinary outcomes that leave local content untouched.
        /// </remarks>
        /// <param name="progress">Aggregate 0..1 progress across the required closure.</param>
        /// <param name="cancellationToken">Cancels before commit; after commit the release stands.</param>
        /// <returns>The outcome; never null.</returns>
        public async Awaitable<ReleaseActivationResult> ActivateLatestReleaseAsync(
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            var coordinator = EnsureCoordinator();
            if (coordinator == null)
                return new ReleaseActivationResult
                {
                    Success = false,
                    Reason = ContentReleaseReason.Unauthorized,
                    Detail = _releaseSetupError,
                };

            string platform = ReleasePlatform.Current;
            if (string.IsNullOrEmpty(platform))
                return new ReleaseActivationResult
                {
                    Success = false,
                    Reason = ContentReleaseReason.PlatformUnsupported,
                    Detail = $"{Application.platform} is not a publishable content platform.",
                };

            string appVersion = string.IsNullOrEmpty(_settings.AppVersion)
                ? Application.version
                : _settings.AppVersion;

            var result = await coordinator.ActivateLatestAsync(
                platform, appVersion, expectedProjectId: null, progress, cancellationToken);

            if (result.Success) OnCatalogRefreshed?.Invoke();
            return result;
        }

        /// <summary>
        /// Builds an eviction plan against the active release.
        /// </summary>
        /// <remarks>
        /// Returns a plan rather than freeing anything. The plan names the exact packages, the exact
        /// bytes each will reclaim <em>given the rest of the plan</em>, and the reason for every
        /// package it cannot free — which is what the SDK UI needs to ask for confirmation honestly.
        /// </remarks>
        /// <param name="packageIds">Packages to consider, or null for every installed package.</param>
        /// <returns>The plan; empty when no release manifest is available.</returns>
        public CacheEvictionPlan PlanCacheEviction(System.Collections.Generic.IEnumerable<string> packageIds = null)
        {
            var manifest = ActiveReleaseManifest;
            if (manifest == null) return new CacheEvictionPlan();

            var installed = _states.Values
                .Where(state => state != null && state.IsInstalled)
                .Select(state => state.packageId)
                .ToList();

            return ReleaseCachePolicy.Plan(manifest, installed, packageIds);
        }

        /// <summary>
        /// Releases the access ticket and restores the Addressables internal id transform.
        /// </summary>
        /// <remarks>
        /// Must run on teardown. <see cref="Addressables"/> holds the transform in a static, so a
        /// provider that is not uninstalled outlives the service that owns it — it would keep
        /// appending a dead ticket to object requests across a domain reload, and in the editor that
        /// survives exiting play mode.
        /// </remarks>
        public void ShutdownReleaseProtocol()
        {
            if (_activationCoordinator != null)
            {
                _activationCoordinator.OnActivationProgress -= RaiseActivationProgress;
                _activationCoordinator.OnReleaseActivated -= RaiseReleaseActivated;
            }

            _accessProvider?.Dispose();
            _accessProvider = null;
            _activationCoordinator = null;
            _releaseSetupError = null;
        }

        private void RaiseActivationProgress(ReleaseActivationProgress progress) =>
            OnReleaseActivationProgress?.Invoke(progress);

        private void RaiseReleaseActivated(string releaseId) =>
            OnReleaseActivated?.Invoke(releaseId);

        /// <summary>
        /// Builds the coordinator on first use, or records why it cannot be built.
        /// </summary>
        /// <remarks>
        /// Lazy rather than constructed with the service: the routed HTTP client is resolved from
        /// the runtime container, which is not guaranteed to be populated when
        /// <see cref="PackageService"/> itself is constructed. Building it eagerly would make the
        /// protocol's availability depend on subsystem ordering.
        /// </remarks>
        private ReleaseActivationCoordinator EnsureCoordinator()
        {
            if (_activationCoordinator != null) return _activationCoordinator;

            _releaseSetupError = FirstSetupProblem(out var http, out var keyring);
            if (_releaseSetupError != null)
            {
                LogWarning($"[PackageService] Release protocol unavailable: {_releaseSetupError}");
                return null;
            }

            var client = new ContentReleaseClient(
                http,
                _settings.ContentServiceId,
                // A delegate, not a captured value: the stamp is the build's credential and is read
                // per request so a token is never held in a field longer than one call needs it.
                () => LicenseStamp.Current?.buildToken ?? "",
                _settings.ContentPathPrefix);

            _accessProvider = new ReleaseAccessProvider(client);
            _accessProvider.Install();

            _activationCoordinator = new ReleaseActivationCoordinator(
                client,
                new ReleaseManifestVerifier(keyring),
                _accessProvider,
                _manifest,
                new PackageDownloadAdapter(this));

            _activationCoordinator.OnActivationProgress += RaiseActivationProgress;
            _activationCoordinator.OnReleaseActivated += RaiseReleaseActivated;

            return _activationCoordinator;
        }

        /// <summary>
        /// Returns the first reason the protocol cannot run, or null when it can.
        /// </summary>
        /// <remarks>
        /// One reason at a time, and specific. "Content unavailable" for a missing key, a missing
        /// service, and a missing token sends an operator looking in three wrong places.
        /// </remarks>
        private string FirstSetupProblem(out IRoutedHttpClient http, out ReleaseKeyring keyring)
        {
            http = null;
            keyring = null;

            if (!_settings.EnableReleaseProtocol)
                return "The release protocol is disabled in ContentPackageSettings.";

            if (string.IsNullOrWhiteSpace(_settings.ContentServiceId))
                return "No content service id is configured in ContentPackageSettings.";

            keyring = new ReleaseKeyring(_settings.TrustedReleaseKeys);
            if (keyring.IsEmpty)
                return "No trusted release signing keys are configured; no release could be verified.";

            if (string.IsNullOrWhiteSpace(LicenseStamp.Current?.buildToken))
                return "This build carries no project-scoped runtime token.";

            http = RuntimeManager.GetService<IRoutedHttpClient>();
            if (http == null)
                return "The routed HTTP client is not available in this runtime.";

            return null;
        }

        /// <summary>
        /// Adapts <see cref="PackageService"/>'s install path to the coordinator's download seam.
        /// </summary>
        /// <remarks>
        /// The coordinator decides <em>whether</em> and <em>when</em> content is adopted; the
        /// service still owns <em>how</em> a package is fetched, including the label mapping,
        /// dependency resolution, and the download queue. Reimplementing any of that here would
        /// produce a second download path that drifts from the one every other caller uses.
        /// </remarks>
        private sealed class PackageDownloadAdapter : IReleasePackageDownloader
        {
            private readonly PackageService _service;

            internal PackageDownloadAdapter(PackageService service) => _service = service;

            public async Awaitable<string> DownloadPackageAsync(
                string packageId, IProgress<float> progress, CancellationToken cancellationToken)
            {
                // A package the release declares but this build has no definition for cannot be
                // installed, and saying so beats a generic Addressables miss.
                if (!_service._definitions.ContainsKey(packageId))
                    return $"'{packageId}' is in the release but not defined in this build's ContentPackageSettings.";

                var result = await _service.InstallPackageAsync(packageId, progress, cancellationToken);
                if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
                return result.Success ? null : result.ErrorMessage;
            }
        }
    }
}
