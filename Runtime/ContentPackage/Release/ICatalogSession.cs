using System.Threading;
using UnityEngine;

namespace Molca.ContentPackage.Release
{
    /// <summary>
    /// A catalog loaded for one release, held separately from the catalog currently in use.
    /// </summary>
    /// <remarks>
    /// The point of the seam is that loading a catalog and <em>adopting</em> it are two events, not
    /// one. Addressables' own <c>LoadContentCatalogAsync</c> conflates them: the returned locator is
    /// live the moment the call succeeds, so a release that later fails to download has already
    /// changed how every address in the app resolves.
    ///
    /// A session makes the second step explicit. <see cref="LoadAsync"/> brings the catalog in;
    /// <see cref="Commit"/> keeps it and retires the one it replaced; <see cref="Rollback"/> removes
    /// it and leaves the previous locator exactly as it was. Disposing without committing is a
    /// rollback, so an exception on any path between the two cannot strand a half-adopted catalog.
    /// </remarks>
    public interface ICatalogSession
    {
        /// <summary>The release this session's catalog belongs to.</summary>
        string ReleaseId { get; }

        /// <summary>True once <see cref="LoadAsync"/> has succeeded and before commit or rollback.</summary>
        bool IsStaged { get; }

        /// <summary>True once <see cref="Commit"/> has run.</summary>
        bool IsCommitted { get; }

        /// <summary>
        /// Loads the catalog and registers its locator, without retiring the previous one.
        /// </summary>
        /// <param name="catalogUrl">Absolute URL of the release catalog.</param>
        /// <param name="cancellationToken">Cancels the load.</param>
        /// <returns>Null on success, or a <see cref="ContentReleaseReason"/>.</returns>
        Awaitable<string> LoadAsync(string catalogUrl, CancellationToken cancellationToken = default);

        /// <summary>Adopts the staged catalog and releases the locator it supersedes.</summary>
        void Commit();

        /// <summary>Removes the staged locator, leaving the previous catalog in force.</summary>
        void Rollback();
    }
}
