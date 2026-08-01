using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ContentPackage.Release
{
    /// <summary>
    /// The parsed <c>contentRelease</c> 1 manifest — see <c>contracts/content-release-v1.md</c> §4.
    /// </summary>
    /// <remarks>
    /// These types mirror the wire document and nothing else. They carry no behaviour that depends
    /// on local state, because a manifest is evidence about a release on the server, not a record of
    /// anything installed here.
    ///
    /// Unknown fields are ignored rather than rejected (contract §9, rule 1 of
    /// <c>contracts/README.md</c>): that is what lets the server add fields without stranding
    /// shipped players. <see cref="JsonUtility"/> already behaves this way. An unknown
    /// <em>protocol</em> is a different matter and is refused in
    /// <see cref="ReleaseManifestVerifier"/>.
    ///
    /// Nothing here re-serializes the document. The digest is computed over the received bytes, so a
    /// field this client does not model cannot change the hash it checks.
    /// </remarks>
    [Serializable]
    public class ContentReleaseManifest
    {
        /// <summary>Wire protocol major. Greater than <see cref="SupportedProtocolVersion"/> is refused.</summary>
        public int protocolVersion;

        /// <summary>Always <c>molca.content.release</c> for a document this client will accept.</summary>
        public string kind;

        /// <summary>Opaque release identity (RFC 4122 UUID, lowercase).</summary>
        public string releaseId;

        /// <summary>Owning Molca project. Never taken from a client; compared, not trusted.</summary>
        public string projectId;

        /// <summary><c>stable</c>, <c>beta</c>, or <c>internal</c>.</summary>
        public string channel;

        /// <summary>Normalized platform identifier (contract §1).</summary>
        public string platform;

        /// <summary>SemVer content version, unique within the identity tuple.</summary>
        public string contentVersion;

        /// <summary>Server-side creation timestamp, ISO 8601.</summary>
        public string createdAt;

        /// <summary>App version range this release may activate within.</summary>
        public Compatibility compatibility;

        /// <summary>Addressables catalog objects for this release.</summary>
        public CatalogRef catalog;

        /// <summary>Author-supplied release notes, at most 8 KiB.</summary>
        public string changelog;

        /// <summary>Every storage object in the release, each appearing exactly once.</summary>
        public ObjectEntry[] objects;

        /// <summary>Every package in the release.</summary>
        public PackageEntry[] packages;

        /// <summary>Server-computed totals, used to cross-check what this client derives.</summary>
        public Totals totals;

        /// <summary>The greatest <c>protocolVersion</c> this client understands.</summary>
        public const int SupportedProtocolVersion = 1;

        /// <summary>The only <c>kind</c> this client will accept.</summary>
        public const string ExpectedKind = "molca.content.release";

        /// <summary>App version range, inclusive at both ends; empty means unbounded (contract §4.4).</summary>
        [Serializable]
        public class Compatibility
        {
            /// <summary>Lowest app version that may activate this release, or empty.</summary>
            public string minAppVersion;

            /// <summary>Highest app version that may activate this release, or empty.</summary>
            public string maxAppVersion;
        }

        /// <summary>The catalog and catalog-hash objects, plus the catalog digest.</summary>
        [Serializable]
        public class CatalogRef
        {
            /// <summary>Object id of the Addressables catalog.</summary>
            public string catalogObjectId;

            /// <summary>Object id of the catalog hash file.</summary>
            public string catalogHashObjectId;

            /// <summary>Lowercase hex SHA-256 of the catalog bytes.</summary>
            public string catalogSha256;
        }

        /// <summary>One storage object (contract §4.1).</summary>
        [Serializable]
        public class ObjectEntry
        {
            /// <summary>Unique within the release; the only handle a gateway route uses.</summary>
            public string objectId;

            /// <summary>Storage key. Read-only detail — a client never constructs one.</summary>
            public string key;

            /// <summary><c>catalog</c>, <c>catalog-hash</c>, <c>bundle</c>, or <c>extra</c>.</summary>
            public string kind;

            /// <summary>Lowercase hex SHA-256 of the object bytes.</summary>
            public string sha256;

            /// <summary>Authoritative byte count, for progress and for rejecting an over-long body.</summary>
            public long sizeBytes;

            /// <summary>MIME type the gateway reports.</summary>
            public string contentType;
        }

        /// <summary>One package and the objects its closure needs (contract §4.2).</summary>
        [Serializable]
        public class PackageEntry
        {
            /// <summary>Stable package identity, unique within the release.</summary>
            public string packageId;

            /// <summary>SemVer version of this package within this release.</summary>
            public string packageVersion;

            /// <summary>Presentation name.</summary>
            public string displayName;

            /// <summary>Presentation description.</summary>
            public string description;

            /// <summary>Must resolve before the release activates locally.</summary>
            public bool required;

            /// <summary>Presentation only. A hidden required package is still installed.</summary>
            public bool visible;

            /// <summary>Other package ids in this same release.</summary>
            public string[] dependencies;

            /// <summary>Object references making up this package's closure.</summary>
            public PackageObjectRef[] objects;

            /// <summary>Closure size counting each object once. Not additive across packages.</summary>
            public long downloadSizeBytes;
        }

        /// <summary>A package's reference to one object, with how the package reached it.</summary>
        [Serializable]
        public class PackageObjectRef
        {
            /// <summary>The referenced <see cref="ObjectEntry.objectId"/>.</summary>
            public string objectId;

            /// <summary><c>direct</c>, <c>shared</c>, or <c>dependency</c>.</summary>
            public string ownership;
        }

        /// <summary>Server-computed release totals.</summary>
        [Serializable]
        public class Totals
        {
            /// <summary>Number of packages.</summary>
            public int packageCount;

            /// <summary>Number of distinct objects.</summary>
            public int objectCount;

            /// <summary>Sum of object sizes, counting each object once.</summary>
            public long totalBytes;
        }

        // ── Lookups ──────────────────────────────────────────────────────────
        //
        // Built lazily and cached: a 50 000-object release (contract §4.3) makes linear scans
        // during a download loop quadratic.

        private Dictionary<string, ObjectEntry> _objectsById;
        private Dictionary<string, PackageEntry> _packagesById;

        /// <summary>Finds an object by id, or null when the release does not declare it.</summary>
        /// <param name="objectId">The object id to resolve.</param>
        public ObjectEntry FindObject(string objectId)
        {
            if (string.IsNullOrEmpty(objectId)) return null;
            if (_objectsById == null)
            {
                _objectsById = new Dictionary<string, ObjectEntry>(StringComparer.Ordinal);
                foreach (var entry in objects ?? Array.Empty<ObjectEntry>())
                    if (entry != null && !string.IsNullOrEmpty(entry.objectId))
                        _objectsById[entry.objectId] = entry;
            }
            return _objectsById.TryGetValue(objectId, out var found) ? found : null;
        }

        /// <summary>Finds a package by id, or null when the release does not declare it.</summary>
        /// <param name="packageId">The package id to resolve.</param>
        public PackageEntry FindPackage(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return null;
            if (_packagesById == null)
            {
                _packagesById = new Dictionary<string, PackageEntry>(StringComparer.Ordinal);
                foreach (var entry in packages ?? Array.Empty<PackageEntry>())
                    if (entry != null && !string.IsNullOrEmpty(entry.packageId))
                        _packagesById[entry.packageId] = entry;
            }
            return _packagesById.TryGetValue(packageId, out var found) ? found : null;
        }

        /// <summary>Every package the release marks <see cref="PackageEntry.required"/>.</summary>
        public IEnumerable<PackageEntry> RequiredPackages()
        {
            foreach (var entry in packages ?? Array.Empty<PackageEntry>())
                if (entry != null && entry.required) yield return entry;
        }

        /// <summary>
        /// The union of object ids reachable from the named packages, following declared
        /// dependencies.
        /// </summary>
        /// <remarks>
        /// A union, never a sum. <see cref="PackageEntry.downloadSizeBytes"/> already counts each
        /// object once <em>within</em> a package, so adding two packages' numbers double-counts every
        /// bundle they share — contract §4.2 says so explicitly. Callers that want a multi-package
        /// total must size this set instead.
        /// </remarks>
        /// <param name="packageIds">Package ids to expand. Unknown ids are ignored.</param>
        /// <returns>Distinct object ids, including those reached through dependencies.</returns>
        public HashSet<string> ObjectClosure(IEnumerable<string> packageIds)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (packageIds == null) return result;

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            foreach (var id in packageIds)
                if (!string.IsNullOrEmpty(id)) pending.Push(id);

            while (pending.Count > 0)
            {
                string packageId = pending.Pop();
                if (!visited.Add(packageId)) continue;

                var package = FindPackage(packageId);
                if (package == null) continue;

                foreach (var reference in package.objects ?? Array.Empty<PackageObjectRef>())
                    if (reference != null && !string.IsNullOrEmpty(reference.objectId))
                        result.Add(reference.objectId);

                foreach (var dependency in package.dependencies ?? Array.Empty<string>())
                    if (!string.IsNullOrEmpty(dependency) && !visited.Contains(dependency))
                        pending.Push(dependency);
            }

            return result;
        }

        /// <summary>Sums the declared size of the given objects, counting each id once.</summary>
        /// <param name="objectIds">Object ids to total. Unknown ids contribute nothing.</param>
        public long TotalBytesOf(IEnumerable<string> objectIds)
        {
            if (objectIds == null) return 0;
            long total = 0;
            var counted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in objectIds)
            {
                if (string.IsNullOrEmpty(id) || !counted.Add(id)) continue;
                var entry = FindObject(id);
                if (entry != null) total += entry.sizeBytes;
            }
            return total;
        }
    }
}
