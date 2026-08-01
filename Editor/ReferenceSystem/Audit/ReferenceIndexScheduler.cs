using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// Keeps the on-disk reference index and the in-memory snapshot in step: restores the index when the
    /// editor starts, writes it whenever a fresh audit completes, and applies incremental updates for the
    /// assets that change in between.
    /// </summary>
    /// <remarks>
    /// <para>This is what makes a cold editor useful. Without it, every domain reload started from nothing and
    /// the only way to learn anything about reference health was to pay for a full project scan — which is a
    /// reliable way to make people stop asking, and a coverage guarantee nobody runs is not a guarantee.</para>
    ///
    /// <para><b>Restoring never lowers the bar for trust.</b> The index is only adopted when every asset
    /// fingerprint still matches; when some do not, the changed set is handed to
    /// <see cref="ReferenceAuditEngine.UpdateIncrementally"/>, and when that cannot be done safely the
    /// snapshot is simply marked stale and a full audit is recommended. At no point is a result presented as
    /// current because it was merely convenient.</para>
    /// </remarks>
    [InitializeOnLoad]
    public static class ReferenceIndexScheduler
    {
        /// <summary>
        /// Asset paths changed since the current snapshot was produced, pending an incremental update.
        /// </summary>
        private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How the current in-memory snapshot came to be.</summary>
        public static string Origin { get; private set; } = "no audit has run yet";

        /// <summary>Whether the last restore attempt adopted the on-disk index.</summary>
        public static bool RestoredFromDisk { get; private set; }

        /// <summary>Asset paths waiting to be folded into the snapshot.</summary>
        public static IReadOnlyCollection<string> PendingChanges => Pending.ToList();

        static ReferenceIndexScheduler()
        {
            // Deferred: a domain reload is not a good moment to touch the AssetDatabase, and the first
            // consumer to ask for a snapshot will arrive well after the editor has settled.
            EditorApplication.delayCall += () => TryRestore();

            ReferenceAuditService.SnapshotChanged += OnSnapshotChanged;
        }

        /// <summary>
        /// Attempts to adopt the on-disk index as the current snapshot.
        /// </summary>
        /// <param name="scope">Scope to attribute a restored snapshot to. Null uses the configured scope.</param>
        /// <returns>The load result; never null.</returns>
        public static ReferenceIndexLoadResult TryRestore(ReferenceAuditScope scope = null)
        {
            scope ??= ReferenceAuditService.DefaultScope();

            ReferenceIndexLoadResult result;
            try
            {
                result = ReferenceIndexStore.Load(scope);
            }
            catch (Exception e)
            {
                // A broken cache must cost a rescan, never the editor.
                Debug.LogWarning($"[ReferenceIndex] Restore failed: {e.Message}");
                return new ReferenceIndexLoadResult(ReferenceIndexLoadStatus.Unreadable, detail: e.Message);
            }

            switch (result.Status)
            {
                case ReferenceIndexLoadStatus.Restored:
                    RestoredFromDisk = true;
                    Origin = result.Detail;
                    Pending.Clear();
                    ReferenceAuditService.Adopt(result.Snapshot, scope, Origin);
                    return result;

                case ReferenceIndexLoadStatus.Outdated:
                    // Worth reporting rather than silently discarding: the index is still a good starting
                    // point, and the incremental path will use it the moment a consumer asks.
                    RestoredFromDisk = false;
                    Origin = result.Detail;
                    foreach (var path in result.ChangedAssets)
                        Pending.Add(path);
                    ReferenceAuditService.Invalidate(
                        $"{result.ChangedAssets.Count} asset(s) changed since the stored index was built");
                    return result;

                default:
                    RestoredFromDisk = false;
                    Origin = result.Detail;
                    return result;
            }
        }

        /// <summary>
        /// Records that an asset changed, so the next update knows what to rescan.
        /// </summary>
        /// <param name="assetPath">Project-relative path of the changed, moved or deleted asset.</param>
        public static void NotifyAssetChanged(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath))
                Pending.Add(assetPath);
        }

        /// <summary>
        /// Folds every pending change into the current snapshot without a full rescan.
        /// </summary>
        /// <returns>
        /// The updated snapshot when the changes could be applied incrementally, otherwise <c>null</c> —
        /// meaning the caller should run a full audit.
        /// </returns>
        public static ReferenceAuditSnapshot TryApplyPendingChanges()
        {
            if (Pending.Count == 0)
                return ReferenceAuditService.Current;

            var updated = ReferenceAuditEngine.UpdateIncrementally(ReferenceAuditService.Current, Pending);
            if (updated == null)
                return null;

            var count = Pending.Count;
            Pending.Clear();
            Origin = $"incrementally updated from {count} changed asset(s)";
            ReferenceAuditService.Adopt(updated, updated.Scope, Origin);
            return updated;
        }

        /// <summary>Deletes the on-disk index and forgets what it knew.</summary>
        public static void Clear()
        {
            ReferenceIndexStore.Delete();
            Pending.Clear();
            RestoredFromDisk = false;
            Origin = "the index was cleared";
        }

        /// <summary>
        /// One-line description of the index for the Coverage view.
        /// </summary>
        public static string Describe()
        {
            if (!ReferenceIndexStore.Exists)
                return "no index on disk — the next completed audit writes one";

            var size = ReferenceIndexStore.SizeInBytes;
            var kilobytes = size / 1024.0;
            var pending = Pending.Count == 0
                ? string.Empty
                : $", {Pending.Count} changed asset(s) pending";
            return $"{ReferenceIndexStore.FilePath} · {kilobytes:0.#} KB{pending}";
        }

        private static void OnSnapshotChanged(ReferenceAuditSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            // A snapshot the engine produced supersedes whatever the index held, so pending changes it already
            // accounted for are no longer pending.
            Pending.Clear();

            if (!snapshot.CanPersist)
            {
                Origin = $"in-memory only — {snapshot.PersistBlockedReason}";
                return;
            }

            if (ReferenceIndexStore.Save(snapshot))
                Origin = $"audited {snapshot.CompletedAtUtc.ToLocalTime():HH:mm:ss} and written to the index";
        }
    }

    /// <summary>
    /// Feeds asset changes to the scheduler so an incremental update knows exactly what to rescan.
    /// </summary>
    /// <remarks>
    /// A separate postprocessor from <see cref="ReferenceAuditAssetInvalidator"/>, which only needs to know
    /// <i>that</i> something changed. This one needs to know <i>what</i>, and keeping the two apart means the
    /// invalidation contract stays as blunt and as safe as it was.
    /// </remarks>
    internal sealed class ReferenceIndexChangeCollector : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (var group in new[] { importedAssets, deletedAssets, movedAssets, movedFromAssetPaths })
            {
                if (group == null)
                    continue;
                foreach (var path in group)
                    ReferenceIndexScheduler.NotifyAssetChanged(path);
            }
        }
    }
}
