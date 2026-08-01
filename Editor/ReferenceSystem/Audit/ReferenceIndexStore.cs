using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>Why a persisted index could not be restored.</summary>
    public enum ReferenceIndexLoadStatus
    {
        /// <summary>The index was restored and every input it names is unchanged.</summary>
        Restored = 0,

        /// <summary>No index file exists yet.</summary>
        Missing = 1,

        /// <summary>The file exists but could not be read or parsed.</summary>
        Unreadable = 2,

        /// <summary>Written by a different schema, Core version, or project.</summary>
        Incompatible = 3,

        /// <summary>Readable and compatible, but assets it was built from have changed since.</summary>
        Outdated = 4,
    }

    /// <summary>The outcome of attempting to restore the on-disk index.</summary>
    public sealed class ReferenceIndexLoadResult
    {
        /// <summary>What happened.</summary>
        public ReferenceIndexLoadStatus Status { get; }

        /// <summary>The restored snapshot, or null unless <see cref="Status"/> is <c>Restored</c>.</summary>
        public ReferenceAuditSnapshot Snapshot { get; }

        /// <summary>Human-readable explanation, suitable for the Coverage view.</summary>
        public string Detail { get; }

        /// <summary>
        /// Asset paths whose contents no longer match the index. Populated for <c>Outdated</c>, and the input
        /// to an incremental update.
        /// </summary>
        public IReadOnlyList<string> ChangedAssets { get; }

        internal ReferenceIndexLoadResult(
            ReferenceIndexLoadStatus status,
            ReferenceAuditSnapshot snapshot = null,
            string detail = null,
            IReadOnlyList<string> changedAssets = null)
        {
            Status = status;
            Snapshot = snapshot;
            Detail = detail ?? string.Empty;
            ChangedAssets = changedAssets ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Reads and writes the derived reference index under <c>Library/Molca/References/</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Derived data, never committed.</b> The index lives under <c>Library/</c> precisely because it
    /// is reproducible from the project: a committed index would be a second source of truth that could
    /// disagree with the assets, which is what the authored id lists on <c>ReferenceManagerSettings</c> used
    /// to be.</para>
    ///
    /// <para><b>Findings are re-derived on load, never replayed.</b> The file stores them, because a stored
    /// index should be readable on its own, but restoring re-runs
    /// <see cref="ReferenceResolutionAnalyzer"/> over the stored providers and sites under the <i>current</i>
    /// severity policy and the <i>current</i> Core rules. Replaying stored findings would mean a policy change
    /// — or a fixed analyzer bug — silently failed to take effect until someone happened to run a full audit.
    /// Scanning is the expensive half; analysis is pure and cheap, so there is nothing to gain by caching its
    /// output as authoritative.</para>
    ///
    /// <para>An index is only restored when every asset it names still hashes to what it hashed at scan time.
    /// The in-memory cache can trust <c>AssetPostprocessor</c> and scene events, but those do not run while
    /// Unity is closed, so a file-backed cache has to prove its own currency.</para>
    /// </remarks>
    public static class ReferenceIndexStore
    {
        /// <summary>Schema version of the stored record. Bumped whenever the shape changes.</summary>
        /// <remarks>
        /// Version 2 added what a site declares — scope, requiredness, availability, enclosing scope root.
        /// A version 1 file is rejected rather than read with those fields defaulted: findings are
        /// re-derived on load, so a missing declaration would silently produce a more permissive result
        /// than the audit that wrote the file. Re-scanning once costs less than one wrong green result.
        /// </remarks>
        public const int SchemaVersion = 2;

        /// <summary>Directory holding the derived index, relative to the project root.</summary>
        public const string Directory = "Library/Molca/References";

        /// <summary>Path of the index file, relative to the project root.</summary>
        public const string FilePath = Directory + "/index.json";

        /// <summary>Absolute path of the index file for this project.</summary>
        public static string AbsolutePath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, FilePath);

        /// <summary>True when an index file exists on disk.</summary>
        public static bool Exists => File.Exists(AbsolutePath);

        /// <summary>Size of the index file in bytes, or 0 when there is none.</summary>
        public static long SizeInBytes
        {
            get
            {
                try
                {
                    var info = new FileInfo(AbsolutePath);
                    return info.Exists ? info.Length : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>Deletes the index file. Safe to call when there is none.</summary>
        /// <returns>True when a file was deleted.</returns>
        public static bool Delete()
        {
            try
            {
                if (!Exists)
                    return false;
                File.Delete(AbsolutePath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ReferenceIndex] Could not delete the index: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Writes <paramref name="snapshot"/> to disk, if it is one that can be revalidated later.
        /// </summary>
        /// <param name="snapshot">The snapshot to persist.</param>
        /// <returns>True when the index was written.</returns>
        /// <remarks>
        /// A snapshot with <see cref="ReferenceAuditSnapshot.CanPersist"/> false is skipped rather than
        /// written: see that property for why an unverifiable index is worse than none.
        /// </remarks>
        public static bool Save(ReferenceAuditSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.CanPersist)
                return false;

            try
            {
                var record = ReferenceIndexRecord.From(snapshot);
                var json = JsonUtility.ToJson(record);

                var absolute = AbsolutePath;
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? string.Empty);

                // Written via a temporary file and moved into place, so a crash mid-write leaves the previous
                // index intact rather than a half-written one that would fail to parse on next load.
                var temp = absolute + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(absolute))
                    File.Delete(absolute);
                File.Move(temp, absolute);
                return true;
            }
            catch (Exception e)
            {
                // Losing the cache costs a rescan; it must never cost the audit.
                Debug.LogWarning($"[ReferenceIndex] Could not write the index: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to restore the on-disk index.
        /// </summary>
        /// <param name="scope">
        /// The scope to attribute the restored snapshot to. Null uses the stored scope description only for
        /// display.
        /// </param>
        /// <returns>The outcome; never null.</returns>
        public static ReferenceIndexLoadResult Load(ReferenceAuditScope scope = null)
        {
            if (!Exists)
                return new ReferenceIndexLoadResult(
                    ReferenceIndexLoadStatus.Missing, detail: "no index has been written yet");

            ReferenceIndexRecord record;
            try
            {
                record = JsonUtility.FromJson<ReferenceIndexRecord>(File.ReadAllText(AbsolutePath));
            }
            catch (Exception e)
            {
                return new ReferenceIndexLoadResult(
                    ReferenceIndexLoadStatus.Unreadable, detail: $"the index could not be read ({e.Message})");
            }

            if (record == null)
                return new ReferenceIndexLoadResult(
                    ReferenceIndexLoadStatus.Unreadable, detail: "the index file is empty or malformed");

            var incompatible = record.DescribeIncompatibility();
            if (incompatible != null)
                return new ReferenceIndexLoadResult(ReferenceIndexLoadStatus.Incompatible, detail: incompatible);

            var changed = record.ChangedAssets();
            if (changed.Count > 0)
            {
                return new ReferenceIndexLoadResult(
                    ReferenceIndexLoadStatus.Outdated,
                    detail: $"{changed.Count} asset(s) changed since the index was built",
                    changedAssets: changed);
            }

            try
            {
                return new ReferenceIndexLoadResult(
                    ReferenceIndexLoadStatus.Restored,
                    record.ToSnapshot(scope),
                    $"restored {record.providers.Length} provider(s) and {record.sites.Length} reference site(s) "
                    + $"from an index built {record.GeneratedAtUtc():u}");
            }
            catch (Exception e)
            {
                return new ReferenceIndexLoadResult(
                    ReferenceIndexLoadStatus.Unreadable,
                    detail: $"the index could not be rebuilt into a snapshot ({e.Message})");
            }
        }

        /// <summary>
        /// The identity this project's index is written under. A stored index whose identity differs is
        /// rejected rather than merged.
        /// </summary>
        /// <remarks>
        /// Includes the Core package version because the analyzer's rules and the record shape both live in
        /// Core: an index written by an older Core describes providers that a newer Core might classify
        /// differently, and silently trusting it would reintroduce disagreement between versions.
        /// </remarks>
        internal static string ProjectIdentity() =>
            $"{PlayerSettings.productGUID}|{CoreVersion()}";

        private static string _coreVersion;

        internal static string CoreVersion()
        {
            if (_coreVersion != null)
                return _coreVersion;

            // The package version, read once. A missing package.json is not fatal — the identity simply
            // becomes less specific, and the asset fingerprints still guard correctness.
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(ReferenceIndexStore).Assembly);
                _coreVersion = info?.version ?? "unknown";
            }
            catch (Exception)
            {
                _coreVersion = "unknown";
            }

            return _coreVersion;
        }
    }

    #region Serialized record

    /// <summary>One persisted provider. Plain data; no live object, no <see cref="Type"/>.</summary>
    [Serializable]
    internal sealed class ReferenceIndexProvider
    {
        public int kind;
        public string refId;
        public string refType;
        public string displayName;
        public string runtimeTypeAssemblyQualifiedName;
        public bool isReadOnly;
        public string assetGuid;
        public long localFileId;
        public string assetPath;
        public string objectPath;
        public string typeName;
        public string globalId;
    }

    /// <summary>One persisted reference site.</summary>
    [Serializable]
    internal sealed class ReferenceIndexSite
    {
        public string propertyPath;
        public string storedRefId;
        public string storedRefType;
        public string expectedTypeAssemblyQualifiedName;
        public int sourceKind;
        public bool isReadOnly;

        // What the site declares about itself. Persisted because findings are re-derived on load: a
        // restored index that dropped the declared scope or requiredness would produce a different — and
        // quietly more permissive — result than the audit that wrote it.
        public int scopeKind;
        public string scopeId;
        public int requiredness;
        public int availability;
        public string scopeRootId;

        public string assetGuid;
        public long localFileId;
        public string assetPath;
        public string objectPath;
        public string typeName;
        public string globalId;
    }

    /// <summary>One persisted coverage entry.</summary>
    [Serializable]
    internal sealed class ReferenceIndexCoverage
    {
        public string category;
        public int status;
        public int count;
        public string reason;
        public bool isRequired;
    }

    /// <summary>One persisted finding. Informational only; findings are re-derived on load.</summary>
    [Serializable]
    internal sealed class ReferenceIndexFinding
    {
        public int code;
        public int severity;
        public string title;
        public string summary;
        public string assetPath;
    }

    /// <summary>One persisted scanned-asset fingerprint.</summary>
    [Serializable]
    internal sealed class ReferenceIndexAsset
    {
        public string assetPath;
        public string contentHash;
    }

    /// <summary>
    /// The serialized form of the index. Shaped for <see cref="JsonUtility"/>: public fields, arrays rather
    /// than dictionaries, and no nullable or generic-interface members.
    /// </summary>
    [Serializable]
    internal sealed class ReferenceIndexRecord
    {
        public int schemaVersion;
        public string projectIdentity;
        public string coreVersion;
        public string generatedAtUtc;
        public string completedAtUtc;
        public double buildDurationMs;
        public string scopeDescription;

        public ReferenceIndexProvider[] providers = Array.Empty<ReferenceIndexProvider>();
        public ReferenceIndexSite[] sites = Array.Empty<ReferenceIndexSite>();
        public ReferenceIndexFinding[] findings = Array.Empty<ReferenceIndexFinding>();
        public ReferenceIndexCoverage[] coverage = Array.Empty<ReferenceIndexCoverage>();
        public string[] scanErrors = Array.Empty<string>();
        public ReferenceIndexAsset[] scannedAssets = Array.Empty<ReferenceIndexAsset>();

        internal static ReferenceIndexRecord From(ReferenceAuditSnapshot snapshot) => new ReferenceIndexRecord
        {
            schemaVersion = ReferenceIndexStore.SchemaVersion,
            projectIdentity = ReferenceIndexStore.ProjectIdentity(),
            coreVersion = ReferenceIndexStore.CoreVersion(),
            generatedAtUtc = DateTime.UtcNow.ToString("O"),
            completedAtUtc = snapshot.CompletedAtUtc.ToString("O"),
            buildDurationMs = snapshot.Duration.TotalMilliseconds,
            scopeDescription = snapshot.Coverage.DescribeGaps(),

            providers = snapshot.Providers.Select(ToRecord).ToArray(),
            sites = snapshot.Sites.Select(ToRecord).ToArray(),
            findings = snapshot.Findings.Select(f => new ReferenceIndexFinding
            {
                code = (int)f.Code,
                severity = (int)f.Severity,
                title = f.Title,
                summary = f.Summary,
                assetPath = f.AssetPath,
            }).ToArray(),
            coverage = snapshot.Coverage.Entries.Select(e => new ReferenceIndexCoverage
            {
                category = e.Category,
                status = (int)e.Status,
                count = e.Count,
                reason = e.Reason,
                isRequired = e.IsRequired,
            }).ToArray(),
            scannedAssets = snapshot.ScannedAssets
                .GroupBy(a => a.AssetPath, StringComparer.Ordinal)
                .Select(g => new ReferenceIndexAsset { assetPath = g.Key, contentHash = g.First().ContentHash })
                .ToArray(),
        };

        private static ReferenceIndexProvider ToRecord(ReferenceProviderRecord provider) =>
            new ReferenceIndexProvider
            {
                kind = (int)provider.Kind,
                refId = provider.RefId,
                refType = provider.RefType,
                displayName = provider.DisplayName,
                // The assembly-qualified name, not the FullName the record displays: only the qualified form
                // round-trips back to a Type, and the assignability check is what needs it.
                runtimeTypeAssemblyQualifiedName = provider.RuntimeType?.AssemblyQualifiedName ?? string.Empty,
                isReadOnly = provider.IsReadOnly,
                assetGuid = provider.Locator.AssetGuid,
                localFileId = provider.Locator.LocalFileId,
                assetPath = provider.Locator.AssetPath,
                objectPath = provider.Locator.ObjectPath,
                typeName = provider.Locator.TypeName,
                globalId = provider.Locator.GlobalId,
            };

        private static ReferenceIndexSite ToRecord(ReferenceSiteRecord site) => new ReferenceIndexSite
        {
            propertyPath = site.PropertyPath,
            storedRefId = site.StoredRefId,
            storedRefType = site.StoredRefType,
            expectedTypeAssemblyQualifiedName = site.ExpectedRuntimeType?.AssemblyQualifiedName ?? string.Empty,
            sourceKind = (int)site.SourceKind,
            isReadOnly = site.IsReadOnly,
            scopeKind = (int)site.ScopeKind,
            scopeId = site.ScopeId,
            requiredness = (int)site.Requiredness,
            availability = (int)site.Availability,
            scopeRootId = site.ScopeRootId,
            assetGuid = site.OwnerLocator.AssetGuid,
            localFileId = site.OwnerLocator.LocalFileId,
            assetPath = site.OwnerLocator.AssetPath,
            objectPath = site.OwnerLocator.ObjectPath,
            typeName = site.OwnerLocator.TypeName,
            globalId = site.OwnerLocator.GlobalId,
        };

        /// <summary>The generation timestamp, or <see cref="DateTime.MinValue"/> when unparseable.</summary>
        internal DateTime GeneratedAtUtc() =>
            DateTime.TryParse(generatedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d
                : DateTime.MinValue;

        /// <summary>Why this record cannot be used by the running editor, or null when it can.</summary>
        internal string DescribeIncompatibility()
        {
            if (schemaVersion != ReferenceIndexStore.SchemaVersion)
                return $"written by index schema {schemaVersion}; this Core reads {ReferenceIndexStore.SchemaVersion}";

            if (!string.Equals(projectIdentity, ReferenceIndexStore.ProjectIdentity(), StringComparison.Ordinal))
                return "written by a different project or a different Molca Core version";

            return null;
        }

        /// <summary>
        /// Asset paths whose contents no longer match what was recorded, including any that have since been
        /// deleted.
        /// </summary>
        internal IReadOnlyList<string> ChangedAssets()
        {
            var changed = new List<string>();
            foreach (var asset in scannedAssets)
            {
                if (asset == null || string.IsNullOrEmpty(asset.assetPath))
                    continue;

                if (!new ReferenceScannedAsset(asset.assetPath, asset.contentHash).MatchesDisk())
                    changed.Add(asset.assetPath);
            }
            return changed;
        }

        /// <summary>
        /// Rebuilds a snapshot, re-deriving findings under the current rules and policy.
        /// </summary>
        /// <param name="scope">Scope to attribute the snapshot to; null falls back to open scenes.</param>
        internal ReferenceAuditSnapshot ToSnapshot(ReferenceAuditScope scope)
        {
            scope ??= ReferenceAuditScope.OpenScenes();

            var providerRecords = providers.Where(p => p != null).Select(p => new ReferenceProviderRecord(
                (ReferenceProviderKind)p.kind,
                p.refId,
                p.refType,
                p.displayName,
                ResolveType(p.runtimeTypeAssemblyQualifiedName),
                new ReferenceObjectLocator(
                    p.assetGuid, p.localFileId, p.assetPath, p.objectPath, p.typeName, p.globalId),
                p.isReadOnly)).ToList();

            var siteRecords = sites.Where(s => s != null).Select(s => new ReferenceSiteRecord(
                new ReferenceObjectLocator(
                    s.assetGuid, s.localFileId, s.assetPath, s.objectPath, s.typeName, s.globalId),
                s.propertyPath,
                s.storedRefId,
                s.storedRefType,
                ResolveType(s.expectedTypeAssemblyQualifiedName),
                (ReferenceSiteSourceKind)s.sourceKind,
                s.isReadOnly,
                (ReferenceScopeKind)s.scopeKind,
                s.scopeId,
                (ReferenceRequiredness)s.requiredness,
                (ReferenceAvailabilityPolicy)s.availability,
                s.scopeRootId)).ToList();

            var coverageEntries = coverage.Where(c => c != null).Select(c => new ReferenceCoverageEntry(
                c.category, (ReferenceCoverageStatus)c.status, c.count, c.reason, c.isRequired)).ToList();

            var analysis = ReferenceResolutionAnalyzer.Analyze(
                providerRecords, siteRecords, new ReferenceCoverage(coverageEntries), scope.Policy, scanErrors,
                ReferenceLoadSetStore.Evaluate);

            var completed = DateTime.TryParse(
                completedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                ? when
                : GeneratedAtUtc();

            return new ReferenceAuditSnapshot(
                scope, providerRecords, siteRecords, analysis,
                TimeSpan.FromMilliseconds(buildDurationMs),
                scannedAssets
                    .Where(a => a != null)
                    .Select(a => new ReferenceScannedAsset(a.assetPath, a.contentHash))
                    .ToList(),
                persistBlockedReason: null,
                completedAtUtc: completed);
        }

        /// <summary>
        /// Resolves a persisted assembly-qualified type name.
        /// </summary>
        /// <remarks>
        /// Null is a legitimate answer: a script renamed or removed since the index was written no longer has
        /// a type, and the analyzer already treats an unresolvable type as unsupported rather than as a type
        /// mismatch. Guessing at a replacement would manufacture a wrong finding.
        /// </remarks>
        private static Type ResolveType(string assemblyQualifiedName)
        {
            if (string.IsNullOrEmpty(assemblyQualifiedName))
                return null;

            try
            {
                return Type.GetType(assemblyQualifiedName, throwOnError: false);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    #endregion
}
