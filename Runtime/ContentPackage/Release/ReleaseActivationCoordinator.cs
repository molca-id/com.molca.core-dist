using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using Molca.ContentPackage.Core;

namespace Molca.ContentPackage.Release
{
    /// <summary>Downloads one package's content. Supplied by the owning service.</summary>
    /// <remarks>
    /// A seam rather than a direct Addressables call, because the coordinator's whole value is what
    /// it does when a download fails at a particular moment, and provoking that against real
    /// Addressables means a network and a build. The implementation stays where the label mapping
    /// and the download queue already live.
    /// </remarks>
    public interface IReleasePackageDownloader
    {
        /// <summary>Downloads everything the package needs, reporting 0..1 progress.</summary>
        /// <param name="packageId">The package to download.</param>
        /// <param name="progress">Progress sink, may be null.</param>
        /// <param name="cancellationToken">Cancels the download.</param>
        /// <returns>Null on success, or a human-readable failure reason.</returns>
        Awaitable<string> DownloadPackageAsync(
            string packageId, IProgress<float> progress, CancellationToken cancellationToken);
    }

    /// <summary>The outcome of one activation attempt.</summary>
    public sealed class ReleaseActivationResult
    {
        /// <summary>True when the release is now the active one.</summary>
        public bool Success { get; set; }

        /// <summary>True when the caller cancelled before commit.</summary>
        public bool Cancelled { get; set; }

        /// <summary>A <see cref="ContentReleaseReason"/> on failure, else empty.</summary>
        public string Reason { get; set; } = "";

        /// <summary>Operator-facing detail. Never contains a token, ticket, or signed URL.</summary>
        public string Detail { get; set; } = "";

        /// <summary>The release that is active after this attempt.</summary>
        public string ActiveReleaseId { get; set; } = "";

        /// <summary>Required packages that downloaded successfully.</summary>
        public List<string> RequiredInstalled { get; } = new List<string>();

        /// <summary>
        /// Optional packages carried forward, and those that failed to carry forward.
        /// </summary>
        /// <remarks>
        /// Reported separately from <see cref="Success"/> on purpose (contract §7.3 item 11). An
        /// optional package that no longer exists in the target release, or that fails to download,
        /// must not falsify a successful required-content activation — otherwise a user with one
        /// stale optional download can never move to a working release.
        /// </remarks>
        public List<string> OptionalCarriedForward { get; } = new List<string>();

        /// <summary>Optional packages that could not be carried forward, with reasons.</summary>
        public List<string> OptionalFailed { get; } = new List<string>();
    }

    /// <summary>
    /// Adopts a new content release transactionally: stage everything, then commit once.
    /// </summary>
    /// <remarks>
    /// This replaces a sequence that cleared every package cache and reset every package state
    /// <em>before</em> downloading anything, and then reported success even when required packages
    /// had failed to reinstall. A network drop after the clear left a device with no content, no
    /// record that it ever had any, and a success result to explain it.
    ///
    /// The order below is chosen so that nothing observable changes until the new release is proven:
    ///
    /// <list type="number">
    /// <item>verify the signed manifest — refuse before touching anything;</item>
    /// <item>check app compatibility — an out-of-range release is a normal outcome that leaves the
    /// current release running;</item>
    /// <item>bind access material and stage the catalog in a session the old locator survives;</item>
    /// <item>persist the <em>staged</em> record, so an interrupted attempt is recognisable next
    /// launch rather than invisible;</item>
    /// <item>download the required closure — the old content is still present and still resolving
    /// throughout;</item>
    /// <item>commit: swap the locator, persist the active record, and only then report success.</item>
    /// </list>
    ///
    /// Every failure and every cancellation before step 6 rolls the session back and leaves the
    /// previous release exactly as it was. Old content is never deleted here at all; retiring it is
    /// a later, separate decision made by <see cref="ReleaseCachePolicy"/>.
    /// </remarks>
    public sealed class ReleaseActivationCoordinator
    {
        private readonly IContentReleaseClient _client;
        private readonly ReleaseManifestVerifier _verifier;
        private readonly ReleaseAccessProvider _access;
        private readonly IPackageStateStore _store;
        private readonly IReleasePackageDownloader _downloader;
        private readonly Func<string, AddressablesCatalogSession, ICatalogSession> _sessionFactory;

        // One activation at a time, and the second caller is refused rather than queued. Two
        // concurrent commits would race on the locator swap and on the activation record, and the
        // loser could pair the catalog of one release with the record of another -- a state nothing
        // downstream can interpret.
        //
        // Refusing beats queueing here: by the time a queued second activation ran, its descriptor
        // would be stale, and it would either redo work the first one just did or overwrite it with
        // an older release.
        private bool _activationInFlight;

        private AddressablesCatalogSession _committedSession;

        /// <summary>Fired when a release commits, carrying the new active release id.</summary>
        public event Action<string> OnReleaseActivated;

        /// <summary>
        /// Fired as the activation moves between phases and as required content transfers.
        /// </summary>
        /// <remarks>
        /// Separate from the <see cref="IProgress{T}"/> parameter, which stays a bare fraction so the
        /// existing callers keep working. This event is what a UI needs: the fraction is zero for
        /// every phase before the download, and a progress bar pinned at zero through a signature
        /// check and a catalog load is indistinguishable from a hang.
        /// </remarks>
        public event Action<ReleaseActivationProgress> OnActivationProgress;

        private void Report(
            ReleaseActivationPhase phase,
            float fraction = 0f,
            string contentVersion = "",
            string packageId = "",
            int index = 0,
            int count = 0)
        {
            if (OnActivationProgress == null) return;
            OnActivationProgress.Invoke(
                new ReleaseActivationProgress(phase, fraction, contentVersion, packageId, index, count));
        }

        /// <summary>The manifest of the currently active release, or null.</summary>
        public ContentReleaseManifest ActiveManifest { get; private set; }

        /// <summary>Builds a coordinator.</summary>
        /// <param name="client">Control-plane client. Required.</param>
        /// <param name="verifier">Manifest verifier. Required.</param>
        /// <param name="access">Access ticket holder. Required.</param>
        /// <param name="store">Durable state. Required.</param>
        /// <param name="downloader">Package download seam. Required.</param>
        /// <param name="sessionFactory">
        /// Builds a catalog session. Overridable so activation ordering can be tested without
        /// Addressables; production passes null and gets <see cref="AddressablesCatalogSession"/>.
        /// </param>
        public ReleaseActivationCoordinator(
            IContentReleaseClient client,
            ReleaseManifestVerifier verifier,
            ReleaseAccessProvider access,
            IPackageStateStore store,
            IReleasePackageDownloader downloader,
            Func<string, AddressablesCatalogSession, ICatalogSession> sessionFactory = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _access = access ?? throw new ArgumentNullException(nameof(access));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _sessionFactory = sessionFactory
                ?? ((releaseId, previous) => new AddressablesCatalogSession(releaseId, previous));
        }

        /// <summary>
        /// Resolves and activates the current release for this platform, if there is a new one.
        /// </summary>
        /// <param name="platform">Normalized platform identifier.</param>
        /// <param name="appVersion">The running app version, for the compatibility check.</param>
        /// <param name="expectedProjectId">The bound project, or null to skip that check.</param>
        /// <param name="progress">Aggregate 0..1 progress across the required closure.</param>
        /// <param name="cancellationToken">Cancels before commit; see the type remarks.</param>
        /// <returns>The outcome; never null.</returns>
        public async Awaitable<ReleaseActivationResult> ActivateLatestAsync(
            string platform,
            string appVersion,
            string expectedProjectId = null,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            Report(ReleaseActivationPhase.Resolving);

            var resolved = await _client.ResolveActiveAsync(platform, cancellationToken);
            if (!resolved.Success)
            {
                Report(ReleaseActivationPhase.Idle);
                return Failure(resolved.Reason, resolved.Detail);
            }

            var descriptor = resolved.Value;
            if (!descriptor.IsActive)
            {
                // Not a failure of anything: this project has nothing promoted for this platform,
                // or the server declined for a reason it already named.
                Report(ReleaseActivationPhase.Idle);
                return new ReleaseActivationResult
                {
                    Success = false,
                    Reason = string.IsNullOrEmpty(descriptor.reason) ? ContentReleaseReason.NoRelease : descriptor.reason,
                    Detail = "No active release for this project, channel, and platform.",
                    ActiveReleaseId = _store.Activation.activeReleaseId,
                };
            }

            return await ActivateAsync(descriptor, appVersion, expectedProjectId, progress, cancellationToken);
        }

        /// <summary>
        /// Activates a specific resolved release.
        /// </summary>
        /// <param name="descriptor">The descriptor to activate. Required.</param>
        /// <param name="appVersion">The running app version.</param>
        /// <param name="expectedProjectId">The bound project, or null to skip that check.</param>
        /// <param name="progress">Aggregate 0..1 progress across the required closure.</param>
        /// <param name="cancellationToken">Cancels before commit.</param>
        /// <returns>The outcome; never null.</returns>
        public async Awaitable<ReleaseActivationResult> ActivateAsync(
            ContentReleaseDescriptor descriptor,
            string appVersion,
            string expectedProjectId = null,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (descriptor == null || !descriptor.IsActive)
                return Failure(ContentReleaseReason.NoRelease, "No release descriptor to activate.");

            if (_activationInFlight)
                return Failure(ContentReleaseReason.ActiveReleaseConflict,
                    "Another release activation is already in progress.");

            _activationInFlight = true;
            try
            {
                return await ActivateInsideGateAsync(
                    descriptor, appVersion, expectedProjectId, progress, cancellationToken);
            }
            finally
            {
                _activationInFlight = false;

                // Exactly one Idle per activation, on every path including the throwing ones. A UI
                // that shows the release row on any non-Idle phase would otherwise be left showing
                // "Verifying…" forever after an exception.
                Report(ReleaseActivationPhase.Idle);
            }
        }

        private async Awaitable<ReleaseActivationResult> ActivateInsideGateAsync(
            ContentReleaseDescriptor descriptor,
            string appVersion,
            string expectedProjectId,
            IProgress<float> progress,
            CancellationToken cancellationToken)
        {
            // 1. Fetch and verify before anything local changes. A manifest that fails here has
            //    cost us one request and nothing else.
            Report(ReleaseActivationPhase.Verifying);
            var fetched = await _client.FetchManifestAsync(descriptor, cancellationToken);
            if (!fetched.Success) return Failure(fetched.Reason, fetched.Detail);

            var verification = _verifier.Verify(
                fetched.Value.Bytes, fetched.Value.Signature, expectedProjectId, descriptor.platform);
            if (!verification.Success) return Failure(verification.Reason, verification.Detail);

            var manifest = verification.Manifest;

            // The descriptor and the manifest must name the same release. They come from two
            // different responses, and acting on a manifest we did not ask for is exactly the
            // substitution the digest check cannot see.
            if (!string.Equals(manifest.releaseId, descriptor.releaseId, StringComparison.Ordinal))
                return Failure(ContentReleaseReason.ManifestUntrusted,
                    "Fetched manifest is for a different release than the one resolved.");

            // 2. Compatibility. Out of range is a normal steady state during a staged rollout
            //    (contract §4.4), so it leaves the installed release running and says why.
            var compatibility = manifest.compatibility ?? new ContentReleaseManifest.Compatibility();
            if (!ReleaseCompatibility.IsInRange(
                    appVersion, compatibility.minAppVersion, compatibility.maxAppVersion, out string why))
                return Failure(ContentReleaseReason.AppIncompatible, why);

            // Already running it: nothing to do, and re-staging would churn the record for no gain.
            var activation = _store.Activation;
            if (string.Equals(activation.activeReleaseId, manifest.releaseId, StringComparison.Ordinal)
                && !activation.HasStagedActivation)
            {
                ActiveManifest = manifest;
                return new ReleaseActivationResult
                {
                    Success = true,
                    ActiveReleaseId = manifest.releaseId,
                    Detail = "Already active.",
                };
            }

            // 3. Access material, then the catalog. Both are scoped to this release.
            Report(ReleaseActivationPhase.PreparingCatalog, 0f, manifest.contentVersion);
            string rejection = _access.Bind(manifest.releaseId, descriptor.access);
            if (rejection != null)
                return Failure(rejection, rejection == ContentReleaseReason.AccessModeUnsupported
                    ? $"Server offered access mode '{descriptor.access?.mode}', which this app cannot use."
                    : "Access material was rejected.");

            var session = _sessionFactory(manifest.releaseId, _committedSession);
            bool committed = false;

            try
            {
                string catalogUrl = CatalogUrl(descriptor, manifest);
                if (catalogUrl == null)
                    return Failure(ContentReleaseReason.ObjectNotFound, "Release declares no reachable catalog.");

                string loadFailure = await session.LoadAsync(catalogUrl, cancellationToken);
                if (loadFailure != null)
                    return Failure(loadFailure, "The release catalog could not be loaded. Nothing was changed.");

                // 4. Persist the staged record before downloading. If the process dies during the
                //    download, the next launch can tell "interrupted" from "never started" -- which
                //    is the difference between resuming and silently doing nothing.
                activation.BeginStaging(manifest.releaseId, manifest.contentVersion);
                if (!_store.SetActivation(activation))
                    return Failure(ContentReleaseReason.NoRelease,
                        "Could not record the staged activation; refusing to download content this device would not remember.");

                // 5. Required closure. Old content stays present and stays resolving.
                var result = await DownloadRequiredAsync(manifest, progress, cancellationToken);
                if (!result.Success) return RollBack(session, activation, result);

                // 6. Commit. From here on the new release is the live one.
                Report(ReleaseActivationPhase.Committing, 1f, manifest.contentVersion);
                session.Commit();
                if (session is AddressablesCatalogSession addressablesSession)
                    _committedSession = addressablesSession;
                committed = true;

                activation.CommitStaging();
                if (!_store.SetActivation(activation))
                {
                    // The catalog swap already happened, so this is not recoverable by rolling back
                    // -- the running app is on the new release either way. Report it loudly rather
                    // than pretending: the next launch will resolve the release again and re-commit.
                    Debug.LogError("[ContentRelease] Activation committed but its record could not be saved. " +
                                   "The next launch will re-resolve and re-commit.");
                }

                _store.InstalledReleaseId = manifest.releaseId;
                _store.InstalledContentVersion = manifest.contentVersion;
                ActiveManifest = manifest;

                result.ActiveReleaseId = manifest.releaseId;
                result.Success = true;

                // 7. Optional carry-forward, after the release is already committed. Its failures are
                //    reported and do not change the outcome.
                await CarryForwardOptionalAsync(manifest, result, cancellationToken);

                OnReleaseActivated?.Invoke(manifest.releaseId);
                return result;
            }
            catch (OperationCanceledException)
            {
                if (!committed)
                {
                    session.Rollback();
                    activation.ClearStaging();
                    _store.SetActivation(activation);
                }
                return new ReleaseActivationResult
                {
                    Cancelled = true,
                    Reason = "",
                    Detail = committed
                        ? "Cancelled after the release had already committed; the new release is active."
                        : "Cancelled before commit; the previous release is unchanged.",
                    ActiveReleaseId = _store.Activation.activeReleaseId,
                    Success = committed,
                };
            }
            catch (Exception exception)
            {
                if (!committed)
                {
                    session.Rollback();
                    activation.ClearStaging();
                    _store.SetActivation(activation);
                }
                return Failure(ContentReleaseReason.NoRelease,
                    $"Activation failed: {exception.Message}. The previous release is unchanged.");
            }
            finally
            {
                if (!committed && session is IDisposable disposable) disposable.Dispose();
            }
        }

        private async Awaitable<ReleaseActivationResult> DownloadRequiredAsync(
            ContentReleaseManifest manifest, IProgress<float> progress, CancellationToken cancellationToken)
        {
            var result = new ReleaseActivationResult();
            var required = manifest.RequiredPackages().Select(package => package.packageId).ToList();
            Report(ReleaseActivationPhase.Downloading, 0f, manifest.contentVersion,
                packageId: "", index: 0, count: required.Count);
            if (required.Count == 0)
            {
                result.Success = true;
                progress?.Report(1f);
                return result;
            }

            // Weight progress by declared bytes rather than by package count: a release with one
            // 4 GB package and six small ones would otherwise sit at 14% for almost the whole
            // download and then jump to done.
            var weights = required.ToDictionary(
                id => id,
                id => Math.Max(1L, manifest.TotalBytesOf(manifest.ObjectClosure(new[] { id }))),
                StringComparer.Ordinal);
            long totalWeight = weights.Values.Sum();
            long completedWeight = 0;
            int index = 0;

            foreach (string packageId in required)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                // A long download can outlive the ticket. Refreshing between packages is where it
                // costs nothing; the alternative is a mid-transfer 401 that reads as a network error.
                await _access.EnsureFreshAsync(cancellationToken);

                long weight = weights[packageId];
                long baseWeight = completedWeight;
                int position = index;

                // Announce the package before the first byte moves, synchronously. Progress<T> posts
                // through the synchronization context, so relying on it alone would leave the label
                // naming the previous package until the new one's first callback arrived -- which for
                // a package that fails to start never happens at all.
                Report(ReleaseActivationPhase.Downloading,
                    Mathf.Clamp01(baseWeight / (float)totalWeight), manifest.contentVersion,
                    packageId, position, required.Count);

                var packageProgress = new Progress<float>(fraction =>
                {
                    float overall = Mathf.Clamp01((baseWeight + weight * fraction) / (float)totalWeight);
                    progress?.Report(overall);
                    Report(ReleaseActivationPhase.Downloading, overall, manifest.contentVersion,
                        packageId, position, required.Count);
                });

                string failure = await _downloader.DownloadPackageAsync(packageId, packageProgress, cancellationToken);
                if (failure != null)
                {
                    // Required means required: one failure fails the activation, and the caller keeps
                    // the release it already had.
                    result.Success = false;
                    result.Reason = ContentReleaseReason.ObjectNotFound;
                    result.Detail = $"Required package '{packageId}' could not be downloaded: {failure}";
                    return result;
                }

                result.RequiredInstalled.Add(packageId);
                completedWeight += weight;
                progress?.Report(Mathf.Clamp01(completedWeight / (float)totalWeight));
            }

            result.Success = true;
            return result;
        }

        private async Awaitable CarryForwardOptionalAsync(
            ContentReleaseManifest manifest, ReleaseActivationResult result, CancellationToken cancellationToken)
        {
            var previouslyInstalled = _store.GetAllStates()
                .Where(state => state != null && state.IsInstalled)
                .Select(state => state.packageId)
                .Where(id => !result.RequiredInstalled.Contains(id))
                .ToList();

            if (previouslyInstalled.Count > 0)
                Report(ReleaseActivationPhase.CarryingForward, 1f, manifest.contentVersion,
                    packageId: "", index: 0, count: previouslyInstalled.Count);

            foreach (string packageId in previouslyInstalled)
            {
                if (cancellationToken.IsCancellationRequested) return;

                var entry = manifest.FindPackage(packageId);
                if (entry == null)
                {
                    // Gone from the target release. Reported, not silently dropped: the user chose
                    // to install it and deserves to know it is no longer available.
                    result.OptionalFailed.Add($"{packageId} (not in release {manifest.contentVersion})");
                    continue;
                }
                if (entry.required) continue;

                string failure = await _downloader.DownloadPackageAsync(packageId, null, cancellationToken);
                if (failure != null) result.OptionalFailed.Add($"{packageId} ({failure})");
                else result.OptionalCarriedForward.Add(packageId);
            }
        }

        /// <summary>
        /// The gateway URL of the release catalog, with the access ticket attached.
        /// </summary>
        /// <remarks>
        /// Built explicitly rather than left to <see cref="ReleaseAccessProvider.Transform"/>.
        /// Addressables' catalog load is not guaranteed to route the catalog URL itself through the
        /// internal-id transform, and a catalog fetched without a ticket fails with a 403 that looks
        /// like a missing release.
        /// </remarks>
        private string CatalogUrl(ContentReleaseDescriptor descriptor, ContentReleaseManifest manifest)
        {
            string objectId = manifest.catalog?.catalogObjectId;
            string baseUrl = descriptor.access?.baseUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(baseUrl)) return null;
            return _access.Transform($"{baseUrl}/{Uri.EscapeDataString(objectId)}");
        }

        private ReleaseActivationResult RollBack(
            ICatalogSession session, ReleaseActivationRecord activation, ReleaseActivationResult result)
        {
            session.Rollback();
            activation.ClearStaging();
            _store.SetActivation(activation);
            result.ActiveReleaseId = activation.activeReleaseId;
            return result;
        }

        private ReleaseActivationResult Failure(string reason, string detail) => new ReleaseActivationResult
        {
            Success = false,
            Reason = reason ?? "",
            Detail = detail ?? "",
            ActiveReleaseId = _store.Activation.activeReleaseId,
        };
    }
}
