using System;
using System.Collections.Generic;
using UnityEditor;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// Decides which assets the audit may write to, and which it may only observe.
    /// </summary>
    /// <remarks>
    /// A read-only asset is still discovered and analysed — a broken reference inside a package is real
    /// and worth reporting — but it can never be the target of a repair. Attempting to edit a package
    /// asset either fails or produces a change that is silently lost on the next package resolve, so the
    /// audit refuses rather than trying.
    /// </remarks>
    public static class ReferenceAssetPolicy
    {
        // Resolving package membership walks the file system, so cache per asset path. Cleared on
        // domain reload, which is also when package layout can change.
        private static readonly Dictionary<string, bool> ReadOnlyCache = new(StringComparer.Ordinal);

        /// <summary>
        /// True when <paramref name="assetPath"/> lives in a package or another non-writable location.
        /// </summary>
        /// <param name="assetPath">Project-relative asset path. Empty is treated as writable.</param>
        public static bool IsReadOnly(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            var normalized = assetPath.Replace('\\', '/');
            if (ReadOnlyCache.TryGetValue(normalized, out var cached))
                return cached;

            var result = ComputeIsReadOnly(normalized);
            ReadOnlyCache[normalized] = result;
            return result;
        }

        private static bool ComputeIsReadOnly(string assetPath)
        {
            if (!assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return false;

            // An embedded package under Packages/ is editable; a resolved registry/Git package is not.
            // PackageInfo distinguishes them, and returns null for a path Unity does not own.
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (info == null)
                return true;

            return info.source != UnityEditor.PackageManager.PackageSource.Embedded
                && info.source != UnityEditor.PackageManager.PackageSource.Local;
        }

        /// <summary>
        /// True when the path should be excluded from scanning entirely (Unity-internal locations that
        /// hold no Molca references and would only slow the scan down).
        /// </summary>
        /// <param name="assetPath">Project-relative asset path.</param>
        public static bool IsExcludedFromScan(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return true;

            var normalized = assetPath.Replace('\\', '/');
            return normalized.StartsWith("Packages/com.unity.", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Packages/com.autodesk.", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Packages/com.havok.", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Drops the cached read-only decisions. Call after a package layout change.</summary>
        public static void InvalidateCache() => ReadOnlyCache.Clear();
    }
}
