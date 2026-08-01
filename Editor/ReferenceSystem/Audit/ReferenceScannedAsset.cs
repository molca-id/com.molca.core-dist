using System;
using UnityEditor;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// One asset an audit actually read, with a fingerprint of its contents at the moment it was read.
    /// </summary>
    /// <remarks>
    /// <para>This is what makes a persisted index trustworthy across editor sessions. The in-memory cache can
    /// rely on <c>AssetPostprocessor</c> and scene events to know when the project moved on, but those hooks
    /// do not run while Unity is closed — so an index loaded from disk has to be able to <i>prove</i> that
    /// every input it was built from is unchanged. Without a per-asset fingerprint, restoring a cached result
    /// would be exactly the "confidently wrong" failure this subsystem exists to remove.</para>
    ///
    /// <para>The fingerprint is <see cref="AssetDatabase.GetAssetDependencyHash"/>, which covers the asset's
    /// contents and its importer settings. It describes the file <b>on disk</b>, which is why a snapshot that
    /// read unsaved in-memory state refuses to persist at all (see
    /// <see cref="ReferenceAuditSnapshot.CanPersist"/>): the recorded hash would match a file whose contents
    /// the audit never actually looked at.</para>
    /// </remarks>
    public readonly struct ReferenceScannedAsset : IEquatable<ReferenceScannedAsset>
    {
        /// <summary>Project-relative path of the asset that was read.</summary>
        public string AssetPath { get; }

        /// <summary>Dependency hash of the asset at scan time, as a string.</summary>
        public string ContentHash { get; }

        /// <summary>Records an asset and its fingerprint.</summary>
        /// <param name="assetPath">Project-relative asset path.</param>
        /// <param name="contentHash">Dependency hash captured at scan time.</param>
        public ReferenceScannedAsset(string assetPath, string contentHash)
        {
            AssetPath = assetPath ?? string.Empty;
            ContentHash = contentHash ?? string.Empty;
        }

        /// <summary>
        /// Fingerprints <paramref name="assetPath"/> as it currently stands on disk.
        /// </summary>
        /// <param name="assetPath">Project-relative asset path.</param>
        /// <returns>The record; its hash is empty when the path has no importable asset.</returns>
        public static ReferenceScannedAsset Capture(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return default;

            // An empty hash (deleted asset, or a path the AssetDatabase does not own) compares unequal to any
            // recorded hash, so an asset that stops being fingerprintable invalidates the index rather than
            // quietly matching.
            var hash = AssetDatabase.GetAssetDependencyHash(assetPath);
            return new ReferenceScannedAsset(assetPath, hash.ToString());
        }

        /// <summary>True when the asset on disk still matches the fingerprint recorded here.</summary>
        public bool MatchesDisk() =>
            !string.IsNullOrEmpty(ContentHash)
            && string.Equals(
                AssetDatabase.GetAssetDependencyHash(AssetPath).ToString(), ContentHash, StringComparison.Ordinal);

        /// <inheritdoc/>
        public bool Equals(ReferenceScannedAsset other) =>
            string.Equals(AssetPath, other.AssetPath, StringComparison.Ordinal)
            && string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is ReferenceScannedAsset other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(AssetPath, ContentHash);

        /// <inheritdoc/>
        public override string ToString() => $"{AssetPath}@{ContentHash}";
    }
}
