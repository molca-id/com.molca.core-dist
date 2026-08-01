#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.PackageManager;
// UnityEditor also declares a legacy PackageInfo, so the Package Manager one is named explicitly.
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Molca.ColorID.Editor
{
    /// <summary>Why an asset can or cannot be written by project tooling.</summary>
    public enum ColorThemeAssetOwnership
    {
        /// <summary>Under <c>Assets/</c>. Owned and writable by this project.</summary>
        Project = 0,

        /// <summary>
        /// A package physically inside this repository — embedded or a local file reference. Authored here,
        /// so writable.
        /// </summary>
        AuthoredPackage = 1,

        /// <summary>
        /// A package resolved from a registry, git URL, or the built-in set. Immutable: Unity keeps it in
        /// <c>Library/PackageCache</c> and an edit would be discarded on the next resolve.
        /// </summary>
        ImmutablePackage = 2
    }

    /// <summary>
    /// Decides whether a colour-theme authoring tool may write to an asset.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Audit/</c>.
    /// <para/>
    /// <b>Why this is finer-grained than the audit's rule.</b> <c>ColorThemeAuditService</c> classifies a
    /// site as package-owned by the <c>Packages/</c> path prefix alone. That is the right call there: the
    /// audit describes what a <i>consumer</i> project would face, where every Molca package is installed and
    /// read-only, and its rename planner reports content sites it does not itself rewrite. Loosening that
    /// classification would make a rename plan claim it will repoint sites it never touches.
    /// <para/>
    /// A tool that actually writes needs the real answer, because in the framework's own repository the
    /// Molca packages are <i>embedded</i> — physically present, tracked by git, and authored here. Migrating
    /// the SDK's own catalog is a normal step of the content migration plan, and the prefix rule would
    /// refuse it. <see cref="PackageSource"/> distinguishes the two cases exactly:
    /// <see cref="PackageSource.Embedded"/> and <see cref="PackageSource.Local"/> live at the path you see,
    /// everything else lives in a cache that a re-resolve replaces.
    /// </remarks>
    public static class ColorThemeAssetWriteAccess
    {
        /// <summary>Classifies an asset path.</summary>
        /// <param name="assetPath">A project-relative asset path.</param>
        /// <returns>Who owns it, and therefore whether a tool may write it.</returns>
        public static ColorThemeAssetOwnership Classify(string assetPath)
        {
            // An empty path means the object is not an asset at all — an in-memory ScriptableObject, or a
            // candidate a tool built to preview against. Asset-level write access does not apply to it, and
            // calling it read-only would block a caller from mutating something it wholly owns.
            if (string.IsNullOrEmpty(assetPath)) return ColorThemeAssetOwnership.Project;

            if (!assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return ColorThemeAssetOwnership.Project;

            var package = UpmPackageInfo.FindForAssetPath(assetPath);
            if (package == null)
            {
                // A Packages/ path Unity cannot attribute. Treated as immutable: refusing a write that
                // might be discarded is recoverable, silently losing an edit is not.
                return ColorThemeAssetOwnership.ImmutablePackage;
            }

            return package.source == PackageSource.Embedded || package.source == PackageSource.Local
                ? ColorThemeAssetOwnership.AuthoredPackage
                : ColorThemeAssetOwnership.ImmutablePackage;
        }

        /// <summary>Whether a tool may write to this asset.</summary>
        /// <param name="assetPath">A project-relative asset path.</param>
        public static bool CanWrite(string assetPath) =>
            Classify(assetPath) != ColorThemeAssetOwnership.ImmutablePackage;

        /// <summary>Whether a tool may write to this object's asset.</summary>
        /// <param name="asset">The asset to check. <c>null</c> is not writable.</param>
        public static bool CanWrite(UnityEngine.Object asset) =>
            asset != null && CanWrite(AssetDatabase.GetAssetPath(asset));

        /// <summary>An author-facing explanation of why an asset is not writable, or <c>null</c>.</summary>
        /// <param name="assetPath">A project-relative asset path.</param>
        public static string DescribeRefusal(string assetPath)
        {
            if (CanWrite(assetPath)) return null;

            // Guarded: FindForAssetPath throws on an empty path rather than returning null, so the
            // early-out above is load-bearing and this stays defensive about it.
            var package = string.IsNullOrEmpty(assetPath)
                ? null
                : UpmPackageInfo.FindForAssetPath(assetPath);
            return package == null
                ? $"'{assetPath}' is inside Packages/ but Unity cannot attribute it to a package, so it is "
                  + "treated as read-only."
                : $"'{assetPath}' belongs to '{package.name}' ({package.source}), which Unity resolves into "
                  + "Library/PackageCache — an edit there is discarded on the next resolve. Change it in "
                  + "that package's own repository, or override the asset in project space.";
        }
    }
}
#endif
