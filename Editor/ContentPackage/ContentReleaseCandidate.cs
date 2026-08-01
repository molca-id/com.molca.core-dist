using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Molca.ContentPackage.Editor
{
    /// <summary>
    /// The release candidate submitted to the control plane, matching <c>contentRelease</c> 1.
    ///
    /// Deliberately carries no object keys. The server assigns every key from the release identity,
    /// which is what makes "an upload cannot escape its release prefix" a structural property rather
    /// than a check somebody has to remember. It also carries no download sizes: the server
    /// recomputes those from the objects, because the client's own size arithmetic being wrong is
    /// the defect this whole phase exists to fix.
    /// </summary>
    [Serializable]
    public sealed class ContentReleaseCandidate
    {
        /// <summary>One uploadable file.</summary>
        [Serializable]
        public sealed class ObjectEntry
        {
            /// <summary>Stable identifier within the release.</summary>
            public string objectId;

            /// <summary>catalog, catalog-hash, bundle, or extra.</summary>
            public string kind;

            /// <summary>Lowercase hex SHA-256 of the file.</summary>
            public string sha256;

            /// <summary>Exact byte length.</summary>
            public long sizeBytes;

            /// <summary>MIME type stored alongside the object.</summary>
            public string contentType;

            /// <summary>Local path to upload from. Not serialized to the server.</summary>
            [NonSerialized] public string localPath;
        }

        /// <summary>A package's reference to an object.</summary>
        [Serializable]
        public sealed class ObjectRef
        {
            /// <summary>The object referenced.</summary>
            public string objectId;

            /// <summary>direct, shared, or dependency.</summary>
            public string ownership;
        }

        /// <summary>One package in the release.</summary>
        [Serializable]
        public sealed class PackageEntry
        {
            /// <summary>The package ID.</summary>
            public string packageId;

            /// <summary>Semantic version of this package's content.</summary>
            public string packageVersion;

            /// <summary>What players see.</summary>
            public string displayName;

            /// <summary>Optional longer description.</summary>
            public string description = string.Empty;

            /// <summary>True when the app cannot run without it.</summary>
            public bool required;

            /// <summary>False hides it from the content manager. Never affects correctness.</summary>
            public bool visible = true;

            /// <summary>Package IDs this one needs.</summary>
            public string[] dependencies = Array.Empty<string>();

            /// <summary>Objects the package needs, each once.</summary>
            public ObjectRef[] objects = Array.Empty<ObjectRef>();
        }

        /// <summary>Protocol version; 1 for contentRelease 1.</summary>
        public int protocolVersion = 1;

        /// <summary>stable, beta, or internal.</summary>
        public string channel;

        /// <summary>Normalized platform identifier, e.g. Android.</summary>
        public string platform;

        /// <summary>Semantic version of this release's content.</summary>
        public string contentVersion;

        /// <summary>Lowest app version that may resolve this release. Empty for no bound.</summary>
        public string minAppVersion = string.Empty;

        /// <summary>Highest app version that may resolve this release. Empty for no bound.</summary>
        public string maxAppVersion = string.Empty;

        /// <summary>Author-facing notes.</summary>
        public string changelog = string.Empty;

        /// <summary>Every object in the release.</summary>
        public ObjectEntry[] objects = Array.Empty<ObjectEntry>();

        /// <summary>Every package in the release.</summary>
        public PackageEntry[] packages = Array.Empty<PackageEntry>();

        /// <summary>Total bytes across unique objects.</summary>
        public long TotalBytes => objects?.Sum(entry => entry.sizeBytes) ?? 0;

        /// <summary>
        /// A key stable across retries and distinct whenever the content changes, so re-running an
        /// interrupted publish resumes the same draft instead of stranding a second one.
        /// </summary>
        public string IdempotencyKey
        {
            get
            {
                string seed = string.Join("|",
                    new[] { platform, channel, contentVersion }
                        .Concat(objects.Select(entry => entry.sha256).OrderBy(x => x, StringComparer.Ordinal)));
                using var sha = SHA256.Create();
                return ToHex(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed)));
            }
        }

        private static readonly Regex CatalogHash = new Regex(@"^catalog.*\.hash$", RegexOptions.IgnoreCase);
        private static readonly Regex Catalog = new Regex(@"^catalog.*\.(json|bin)$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Builds a candidate from a resolved build graph and the staged build directory.
        /// </summary>
        /// <param name="graph">The resolved package-to-bundle graph.</param>
        /// <param name="configs">Package configurations, for metadata the graph does not carry.</param>
        /// <param name="stagingDirectory">The clean directory the build wrote to.</param>
        /// <param name="channel">Target channel.</param>
        /// <param name="platform">Normalized platform identifier.</param>
        /// <param name="contentVersion">Semantic content version.</param>
        /// <param name="minAppVersion">Optional lower compatibility bound.</param>
        /// <param name="maxAppVersion">Optional upper compatibility bound.</param>
        /// <param name="changelog">Optional notes.</param>
        public static ContentReleaseCandidate FromBuild(
            ContentBuildGraph graph,
            IReadOnlyList<ContentPackageSettings.PackageConfig> configs,
            string stagingDirectory,
            string channel,
            string platform,
            string contentVersion,
            string minAppVersion = null,
            string maxAppVersion = null,
            string changelog = null)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (!Directory.Exists(stagingDirectory))
                throw new DirectoryNotFoundException($"Staging directory not found: {stagingDirectory}");

            var candidate = new ContentReleaseCandidate
            {
                channel = channel,
                platform = platform,
                contentVersion = contentVersion,
                minAppVersion = minAppVersion ?? string.Empty,
                maxAppVersion = maxAppVersion ?? string.Empty,
                changelog = changelog ?? string.Empty,
            };

            var objectsByBundleName = new Dictionary<string, ObjectEntry>(StringComparer.Ordinal);
            var entries = new List<ObjectEntry>();
            var seenHashes = new Dictionary<string, ObjectEntry>(StringComparer.Ordinal);

            foreach (var path in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(path);
                var info = new FileInfo(path);
                string hash = HashFile(path);

                string kind = CatalogHash.IsMatch(name) ? "catalog-hash"
                    : Catalog.IsMatch(name) ? "catalog"
                    : "bundle";

                // Bundles are content-addressed, so two identical bundles under different
                // Addressables names collapse to one object -- the server would store them at the
                // same key regardless, and declaring both would be a duplicate.
                string objectId = kind == "bundle" ? "bundle-" + hash.Substring(0, 16) : kind;

                if (seenHashes.TryGetValue(objectId, out var existing))
                {
                    if (!string.Equals(existing.sha256, hash, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Two different files claim object id '{objectId}': " +
                            $"'{existing.localPath}' and '{path}'.");
                    }
                    objectsByBundleName[name] = existing;
                    continue;
                }

                var entry = new ObjectEntry
                {
                    objectId = objectId,
                    kind = kind,
                    sha256 = hash,
                    sizeBytes = info.Length,
                    contentType = kind == "catalog" && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? "application/json"
                        : kind == "catalog-hash" ? "text/plain" : "application/octet-stream",
                    localPath = path,
                };
                entries.Add(entry);
                seenHashes[objectId] = entry;
                objectsByBundleName[name] = entry;
            }

            if (entries.All(entry => entry.kind != "catalog"))
                throw new InvalidOperationException(
                    "No Addressables catalog in the staged build. The remote catalog must be enabled.");
            if (entries.All(entry => entry.kind != "catalog-hash"))
                throw new InvalidOperationException(
                    "No catalog hash in the staged build. The remote catalog must be enabled.");

            candidate.objects = entries.ToArray();

            var configById = configs
                .Where(config => config != null && !string.IsNullOrWhiteSpace(config.packageId))
                .ToDictionary(config => config.packageId, config => config, StringComparer.Ordinal);

            var packages = new List<PackageEntry>();
            foreach (var node in graph.Packages)
            {
                if (!configById.TryGetValue(node.PackageId, out var config)) continue;

                var refs = new List<ObjectRef>();
                foreach (var bundle in node.AllBundles)
                {
                    if (!objectsByBundleName.TryGetValue(bundle.Name, out var entry))
                    {
                        throw new InvalidOperationException(
                            $"Package '{node.PackageId}' needs bundle '{bundle.Name}', which is not in the " +
                            "staged build. The build layout and the staging directory disagree; rebuild clean.");
                    }

                    // Ownership is descriptive, and the distinctions matter to the server's
                    // accounting: shared beats dependency, because a bundle several packages need
                    // is shared however this package came to reach it.
                    string ownership = bundle.IsShared ? "shared"
                        : node.DependencyBundles.Contains(bundle) ? "dependency"
                        : "direct";

                    if (refs.All(existing => existing.objectId != entry.objectId))
                        refs.Add(new ObjectRef { objectId = entry.objectId, ownership = ownership });
                }

                packages.Add(new PackageEntry
                {
                    packageId = node.PackageId,
                    packageVersion = config.metadata?.version ?? "1.0.0",
                    displayName = config.displayName ?? node.PackageId,
                    description = config.metadata?.description ?? string.Empty,
                    required = config.isRequired,
                    visible = config.isVisible,
                    dependencies = (config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>())
                        .Select(dependency => dependency?.packageId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    objects = refs.OrderBy(r => r.objectId, StringComparer.Ordinal).ToArray(),
                });
            }

            candidate.packages = packages.ToArray();
            return candidate;
        }

        /// <summary>Streams the file so a multi-gigabyte bundle is never held in memory.</summary>
        private static string HashFile(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return ToHex(sha.ComputeHash(stream));
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) builder.Append(value.ToString("x2"));
            return builder.ToString();
        }
    }
}
