using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.AddressableAssets.Build.Layout;
using UnityEngine.AddressableAssets;

namespace Molca.ContentPackage.Editor
{
    /// <summary>
    /// The authoritative mapping from Molca packages to the AssetBundles a build actually produced.
    ///
    /// This replaces a filename heuristic that matched bundles to packages by lowercasing a group
    /// name, stripping its spaces, and testing whether a bundle filename started with the result.
    /// That was wrong in three ways that all understate or misattribute content: two groups sharing
    /// a name prefix claimed each other's bundles; bundles a package depends on but does not own
    /// were invisible, so download sizes excluded content the player must fetch; and a bundle used
    /// by several packages was counted once per package or not at all, depending on naming.
    ///
    /// Sizes reported here come from the Addressables build layout, which records what was written,
    /// so they are measurements rather than inferences.
    /// </summary>
    public sealed class ContentBuildGraph
    {
        /// <summary>One AssetBundle as the build produced it.</summary>
        public sealed class BundleNode
        {
            /// <summary>The bundle file name as written to the build directory.</summary>
            public string Name;

            /// <summary>Compressed size on disk in bytes, as recorded by the build.</summary>
            public long FileSize;

            /// <summary>The Addressables group that produced it, for diagnostics.</summary>
            public string GroupName;

            /// <summary>Package IDs that reach this bundle, directly or through dependencies.</summary>
            public readonly HashSet<string> Owners = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>True when more than one package reaches it.</summary>
            public bool IsShared => Owners.Count > 1;
        }

        /// <summary>One Molca package resolved against the build.</summary>
        public sealed class PackageNode
        {
            /// <summary>The package ID from settings.</summary>
            public string PackageId;

            /// <summary>Labels the package declares.</summary>
            public string[] Labels = Array.Empty<string>();

            /// <summary>Bundles containing assets the package's labels resolve to.</summary>
            public readonly List<BundleNode> DirectBundles = new List<BundleNode>();

            /// <summary>Bundles reached only through the dependency closure of the direct bundles.</summary>
            public readonly List<BundleNode> DependencyBundles = new List<BundleNode>();

            /// <summary>Every bundle the package needs. Each appears once.</summary>
            public IEnumerable<BundleNode> AllBundles => DirectBundles.Concat(DependencyBundles);

            /// <summary>
            /// Bytes the player downloads for this package, counting each bundle once.
            /// Shared bundles are included: a package that needs them cannot load without them, and
            /// whether another package also paid for them is not knowable from this package alone.
            /// </summary>
            public long DownloadSizeBytes => AllBundles.Sum(bundle => bundle.FileSize);

            /// <summary>Addressable entries the labels matched. Zero means the package ships nothing.</summary>
            public int ResolvedAssetCount;
        }

        /// <summary>Packages in settings order.</summary>
        public List<PackageNode> Packages { get; } = new List<PackageNode>();

        /// <summary>Every bundle the build produced, keyed by file name.</summary>
        public Dictionary<string, BundleNode> Bundles { get; } =
            new Dictionary<string, BundleNode>(StringComparer.Ordinal);

        /// <summary>Bundles no package reaches. Real content nobody can download.</summary>
        public List<BundleNode> OrphanBundles =>
            Bundles.Values.Where(bundle => bundle.Owners.Count == 0).ToList();

        /// <summary>Total bytes of the build, counting each bundle once.</summary>
        public long TotalBundleBytes => Bundles.Values.Sum(bundle => bundle.FileSize);

        /// <summary>Where the layout was read from, for error reporting.</summary>
        public string LayoutPath { get; private set; }

        /// <summary>The conventional location of the build layout report.</summary>
        public static string DefaultLayoutPath => $"{Addressables.LibraryPath}buildlayout.json";

        /// <summary>True when a build layout report exists to be read.</summary>
        public static bool LayoutExists(string path = null) => File.Exists(path ?? DefaultLayoutPath);

        /// <summary>
        /// Builds the graph by resolving each package's labels against the build layout.
        /// </summary>
        /// <param name="packageLabels">Package ID to its declared Addressables labels.</param>
        /// <param name="layoutPath">Layout report path, or null for the conventional location.</param>
        /// <returns>The resolved graph.</returns>
        /// <exception cref="FileNotFoundException">
        /// The layout report is missing. It is only written when Addressables' build layout report is
        /// enabled, so this is a configuration problem rather than a build failure, and callers
        /// should say so — silently falling back to a heuristic is how the old behaviour survived.
        /// </exception>
        public static ContentBuildGraph Resolve(
            IReadOnlyDictionary<string, string[]> packageLabels, string layoutPath = null)
        {
            if (packageLabels == null) throw new ArgumentNullException(nameof(packageLabels));

            string path = layoutPath ?? DefaultLayoutPath;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Addressables build layout report not found. Enable it in " +
                    "Preferences > Addressables > 'Debug Build Layout', then rebuild. Package content " +
                    "cannot be resolved without it.", path);
            }

            var layout = BuildLayout.Open(path, readHeader: true, readFullFile: true);
            if (layout == null)
                throw new InvalidDataException($"Build layout report at '{path}' could not be read.");

            var graph = new ContentBuildGraph { LayoutPath = path };

            // Index every produced bundle first, so dependency edges can be followed by name even
            // when a dependency belongs to a group no package references.
            var bundleByLayout = new Dictionary<BuildLayout.Bundle, BundleNode>();
            foreach (var group in layout.Groups)
            {
                foreach (var bundle in group.Bundles)
                {
                    if (bundle == null) continue;
                    var node = graph.GetOrAdd(bundle.Name, (long)bundle.FileSize, group.Name);
                    bundleByLayout[bundle] = node;
                }
            }
            foreach (var bundle in layout.BuiltInBundles)
            {
                if (bundle == null) continue;
                bundleByLayout[bundle] = graph.GetOrAdd(bundle.Name, (long)bundle.FileSize, "[built-in]");
            }

            // Label -> the bundles holding assets carrying that label. Built once; a project with
            // many packages would otherwise walk the whole asset list per package.
            var bundlesByLabel = new Dictionary<string, HashSet<BuildLayout.Bundle>>(StringComparer.Ordinal);
            var assetCountByLabel = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var asset in EnumerateExplicitAssets(layout))
            {
                if (asset?.Labels == null || asset.Bundle == null) continue;
                foreach (var label in asset.Labels)
                {
                    if (string.IsNullOrEmpty(label)) continue;
                    if (!bundlesByLabel.TryGetValue(label, out var set))
                        bundlesByLabel[label] = set = new HashSet<BuildLayout.Bundle>();
                    set.Add(asset.Bundle);
                    assetCountByLabel.TryGetValue(label, out int count);
                    assetCountByLabel[label] = count + 1;
                }
            }

            foreach (var pair in packageLabels)
            {
                var node = new PackageNode
                {
                    PackageId = pair.Key,
                    Labels = pair.Value ?? Array.Empty<string>(),
                };

                var direct = new HashSet<BuildLayout.Bundle>();
                foreach (var label in node.Labels)
                {
                    if (string.IsNullOrEmpty(label)) continue;
                    if (bundlesByLabel.TryGetValue(label, out var set)) direct.UnionWith(set);
                    assetCountByLabel.TryGetValue(label, out int count);
                    node.ResolvedAssetCount += count;
                }

                // The closure, not just the directly-labelled bundles. A package whose assets
                // reference a shared material or shader cannot load without the bundle holding it,
                // so omitting those understates the download and breaks the install at runtime.
                var closure = new HashSet<BuildLayout.Bundle>(direct);
                foreach (var bundle in direct)
                {
                    var expanded = bundle.ExpandedDependencies ?? bundle.Dependencies;
                    if (expanded == null) continue;
                    foreach (var dependency in expanded)
                        if (dependency != null) closure.Add(dependency);
                }

                foreach (var bundle in closure)
                {
                    if (!bundleByLayout.TryGetValue(bundle, out var bundleNode))
                        bundleNode = graph.GetOrAdd(bundle.Name, (long)bundle.FileSize, "[unlisted]");

                    bundleNode.Owners.Add(node.PackageId);
                    if (direct.Contains(bundle)) node.DirectBundles.Add(bundleNode);
                    else node.DependencyBundles.Add(bundleNode);
                }

                node.DirectBundles.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                node.DependencyBundles.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                graph.Packages.Add(node);
            }

            return graph;
        }

        private BundleNode GetOrAdd(string name, long size, string groupName)
        {
            if (Bundles.TryGetValue(name, out var existing)) return existing;
            var node = new BundleNode { Name = name, FileSize = size, GroupName = groupName };
            Bundles[name] = node;
            return node;
        }

        private static IEnumerable<BuildLayout.ExplicitAsset> EnumerateExplicitAssets(BuildLayout layout)
        {
            foreach (var group in layout.Groups)
            foreach (var bundle in group.Bundles)
            {
                if (bundle?.Files == null) continue;
                foreach (var file in bundle.Files)
                {
                    if (file?.Assets == null) continue;
                    foreach (var asset in file.Assets) yield return asset;
                }
            }
        }
    }
}
