using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// What a package's labels match in the project right now, before anything is built.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> called from <see cref="ContentPackageDetailView"/>'s Content card.
    /// <para>
    /// <b>This is an estimate and every caller has to say so.</b> It sums source file lengths plus the
    /// lengths of everything those files reference; a build packs, deduplicates and compresses all of
    /// it, so the number is wrong in a direction that varies per asset type. It answers "did my labels
    /// catch the assets I meant?", which is the question an author has before a fifteen-minute build —
    /// and the reason it is worth keeping despite <see cref="ContentBuildGraph"/> being the authority
    /// on what actually ships.
    /// </para>
    /// <para>
    /// Results are cached by package id and survive a workspace rebuild, because an edit anywhere in
    /// the form rebuilds the view and re-scanning on each one would walk the whole dependency graph
    /// per keystroke. The cache is dropped when the package's labels change, which is the only edit
    /// that can alter the answer.
    /// </para>
    /// </remarks>
    internal static class ContentScanPreview
    {
        /// <summary>One package's scan result.</summary>
        internal readonly struct Result
        {
            /// <summary>Top-level assets carrying one of the labels.</summary>
            public int AssetCount { get; }

            /// <summary>Source bytes of those assets and everything they reference.</summary>
            public long SourceBytes { get; }

            /// <summary>Matching entry counts per Addressables group, ordered by group name.</summary>
            public IReadOnlyList<(string Group, int Entries)> Groups { get; }

            internal Result(int assetCount, long sourceBytes, IReadOnlyList<(string, int)> groups)
            {
                AssetCount = assetCount;
                SourceBytes = sourceBytes;
                Groups = groups;
            }
        }

        private static readonly Dictionary<string, Result> Cache =
            new Dictionary<string, Result>(System.StringComparer.Ordinal);

        /// <summary>The cached scan for a package, or null when it has not been scanned.</summary>
        /// <param name="packageId">The package.</param>
        /// <returns>The result, or null.</returns>
        public static Result? Cached(string packageId) =>
            packageId != null && Cache.TryGetValue(packageId, out var result) ? result : (Result?)null;

        /// <summary>Forgets a package's scan, because its labels changed.</summary>
        /// <param name="packageId">The package.</param>
        public static void Invalidate(string packageId)
        {
            if (packageId != null) Cache.Remove(packageId);
        }

        /// <summary>Forgets every scan.</summary>
        public static void InvalidateAll() => Cache.Clear();

        /// <summary>
        /// Scans the Addressables entries matching a package's labels and caches the result.
        /// </summary>
        /// <param name="packageId">The package to cache under.</param>
        /// <param name="labels">The labels to match.</param>
        /// <returns>The result, or null when Addressables is not configured.</returns>
        public static Result? Scan(string packageId, IEnumerable<string> labels)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return null;

            var wanted = new HashSet<string>(
                (labels ?? Enumerable.Empty<string>()).Where(label => !string.IsNullOrWhiteSpace(label)),
                System.StringComparer.Ordinal);

            int count = 0;
            long bytes = 0;

            // Asset paths already counted, so an entry carrying two of this package's labels, or
            // appearing in two groups, is not counted twice.
            var counted = new HashSet<string>(System.StringComparer.Ordinal);
            var groups = new List<(string, int)>();

            foreach (var group in settings.groups)
            {
                if (group == null) continue;

                int hits = 0;
                foreach (var entry in group.entries)
                {
                    if (entry == null || !entry.labels.Overlaps(wanted)) continue;

                    hits++;
                    Accumulate(entry.AssetPath, counted, ref count, ref bytes);
                }

                if (hits > 0) groups.Add((group.Name, hits));
            }

            groups.Sort((left, right) => string.CompareOrdinal(left.Item1, right.Item1));

            var result = new Result(count, bytes, groups);
            if (packageId != null) Cache[packageId] = result;
            return result;
        }

        /// <summary>
        /// Adds one asset — or one folder's contents — and everything it references.
        /// </summary>
        /// <remarks>
        /// Dependencies contribute their size but not to the asset count: a prefab referencing four
        /// textures is one asset the author labelled, and counting five would make the number disagree
        /// with what they selected in the Addressables window.
        /// </remarks>
        private static void Accumulate(string assetPath, HashSet<string> counted, ref int count, ref long bytes)
        {
            if (string.IsNullOrEmpty(assetPath) || !counted.Add(assetPath)) return;

            if (AssetDatabase.IsValidFolder(assetPath))
            {
                foreach (string guid in AssetDatabase.FindAssets("", new[] { assetPath }))
                {
                    string child = AssetDatabase.GUIDToAssetPath(guid);
                    if (!AssetDatabase.IsValidFolder(child))
                        Accumulate(child, counted, ref count, ref bytes);
                }
                return;
            }

            var file = new FileInfo(assetPath);
            if (!file.Exists) return;

            bytes += file.Length;
            count++;

            foreach (string dependency in AssetDatabase.GetDependencies(assetPath, recursive: true))
            {
                if (dependency == assetPath || AssetDatabase.IsValidFolder(dependency)) continue;
                if (!counted.Add(dependency)) continue;

                var dependencyFile = new FileInfo(dependency);
                if (dependencyFile.Exists) bytes += dependencyFile.Length;
            }
        }
    }
}
