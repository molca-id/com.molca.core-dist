using System;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Molca.ContentPackage.Release
{
    /// <summary>
    /// An <see cref="ICatalogSession"/> over Addressables' content catalog API.
    /// </summary>
    /// <remarks>
    /// Owns exactly one locator handle and is explicit about when it is registered and when it goes
    /// away. The previous session, if any, is retired only on <see cref="Commit"/> — that ordering is
    /// the whole safety property: until the new release is proven, the old catalog is still the one
    /// resolving addresses, so a failure costs a download rather than the app's content.
    /// </remarks>
    public sealed class AddressablesCatalogSession : ICatalogSession, IDisposable
    {
        private readonly AddressablesCatalogSession _previous;
        private AsyncOperationHandle<IResourceLocator> _handle;
        private IResourceLocator _locator;
        private bool _hasHandle;
        private bool _disposed;

        /// <summary>Builds a session for a release.</summary>
        /// <param name="releaseId">The release whose catalog this session will hold.</param>
        /// <param name="previous">
        /// The session currently in force, retired on commit. Null on first activation, where there
        /// is only the catalog baked into the build and nothing to remove.
        /// </param>
        public AddressablesCatalogSession(string releaseId, AddressablesCatalogSession previous = null)
        {
            ReleaseId = releaseId ?? "";
            _previous = previous;
        }

        /// <inheritdoc/>
        public string ReleaseId { get; }

        /// <inheritdoc/>
        public bool IsStaged { get; private set; }

        /// <inheritdoc/>
        public bool IsCommitted { get; private set; }

        /// <summary>The registered locator, or null before a successful load.</summary>
        public IResourceLocator Locator => _locator;

        /// <inheritdoc/>
        public async Awaitable<string> LoadAsync(string catalogUrl, CancellationToken cancellationToken = default)
        {
            if (_disposed) return ContentReleaseReason.NoRelease;
            if (IsStaged || IsCommitted) return null;
            if (string.IsNullOrWhiteSpace(catalogUrl)) return ContentReleaseReason.NoRelease;

            // autoReleaseHandle: false, because the handle is the only way to remove this locator
            // again. Letting Addressables release it would leave a registered locator this session
            // can no longer roll back.
            _handle = Addressables.LoadContentCatalogAsync(catalogUrl, false);
            _hasHandle = true;

            try
            {
                while (!_handle.IsDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Awaitable.NextFrameAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // The load may still complete after the cancel; releasing the handle is what
                // guarantees its locator does not stay registered behind our back.
                ReleaseHandle();
                throw;
            }

            if (_handle.Status != AsyncOperationStatus.Succeeded || _handle.Result == null)
            {
                string message = _handle.OperationException?.Message ?? "unknown error";
                Debug.LogWarning($"[ContentRelease] Catalog load failed for release {ReleaseId}: {message}");
                ReleaseHandle();
                return ContentReleaseReason.ObjectNotFound;
            }

            _locator = _handle.Result;
            IsStaged = true;
            return null;
        }

        /// <inheritdoc/>
        public void Commit()
        {
            if (!IsStaged || IsCommitted) return;
            IsCommitted = true;
            IsStaged = false;

            // Only now is the old catalog unnecessary. Retiring it earlier would mean a failed
            // activation had already removed the addresses the running app depends on.
            _previous?.Retire();
        }

        /// <inheritdoc/>
        public void Rollback()
        {
            if (IsCommitted) return;
            Retire();
            IsStaged = false;
        }

        /// <summary>Removes this session's locator and releases its handle. Idempotent.</summary>
        private void Retire()
        {
            if (_locator != null)
            {
                try { Addressables.RemoveResourceLocator(_locator); }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[ContentRelease] Could not remove locator for release {ReleaseId}: {exception.Message}");
                }
                _locator = null;
            }
            ReleaseHandle();
        }

        private void ReleaseHandle()
        {
            if (!_hasHandle) return;
            _hasHandle = false;
            try { if (_handle.IsValid()) Addressables.Release(_handle); }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ContentRelease] Could not release catalog handle for release {ReleaseId}: {exception.Message}");
            }
        }

        /// <summary>
        /// Rolls back an uncommitted session.
        /// </summary>
        /// <remarks>
        /// Disposing is a rollback rather than a no-op so that an exception thrown anywhere between
        /// load and commit cannot leave a locator registered for a release that never activated.
        /// A committed session keeps its locator: it is the live catalog.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!IsCommitted) Rollback();
        }
    }
}
