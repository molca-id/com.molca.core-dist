using System;
using System.Threading;
using Molca.ReferenceSystem;
using Molca.Settings;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// The shared, cached entry point every reference consumer goes through.
    /// </summary>
    /// <remarks>
    /// Doctor, the build gate, the Inspector drawer, Sequence validation, Framework Graph and MCP all ask
    /// this service for a snapshot instead of scanning for themselves. That is what makes their duplicate,
    /// ambiguity and type rules identical by construction rather than by convention.
    ///
    /// The cache is invalidated by asset imports, scene open/save/close, prefab-stage saves, hierarchy
    /// changes and play-mode transitions. Anything the invalidation hooks cannot prove is still current is
    /// treated as stale, so a consumer never gets a confidently wrong answer — at worst it pays for a
    /// rescan.
    /// </remarks>
    public static class ReferenceAuditService
    {
        private static ReferenceAuditSnapshot _snapshot = ReferenceAuditSnapshot.Empty;
        private static ReferenceAuditScope _snapshotScope;
        private static bool _isStale = true;
        private static string _staleReason = "no audit has run yet";

        /// <summary>
        /// Raised after a new snapshot is produced. Handlers are isolated: one throwing subscriber does
        /// not stop the others or invalidate the snapshot.
        /// </summary>
        public static event Action<ReferenceAuditSnapshot> SnapshotChanged;

        /// <summary>
        /// The most recent snapshot, or <see cref="ReferenceAuditSnapshot.Empty"/> before the first run.
        /// May be stale — check <see cref="IsStale"/> before presenting it as current.
        /// </summary>
        public static ReferenceAuditSnapshot Current => _snapshot;

        /// <summary>True when the project changed since <see cref="Current"/> was produced.</summary>
        public static bool IsStale => _isStale;

        /// <summary>Why <see cref="Current"/> is stale. Empty when it is current.</summary>
        public static string StaleReason => _isStale ? _staleReason : string.Empty;

        /// <summary>
        /// Returns a snapshot for <paramref name="scope"/>, reusing the cached one when it is current and
        /// was produced for an equivalent scope.
        /// </summary>
        /// <param name="scope">What to cover. Null uses the project's configured scope.</param>
        /// <param name="progress">Optional progress sink for a fresh run.</param>
        /// <param name="cancellationToken">Cancels a fresh run.</param>
        /// <returns>A snapshot. Never null.</returns>
        public static ReferenceAuditSnapshot GetOrRun(
            ReferenceAuditScope scope = null,
            Action<string, float> progress = null,
            CancellationToken cancellationToken = default)
        {
            scope ??= DefaultScope();

            if (!_isStale && scope.MatchesCacheOf(_snapshotScope))
                return _snapshot;

            if (TryUpdateIncrementally(scope, out var updated))
                return updated;

            return Refresh(scope, progress, cancellationToken);
        }

        /// <summary>
        /// Attempts to bring the cached snapshot up to date by rescanning only what changed.
        /// </summary>
        /// <param name="scope">The scope the caller asked for.</param>
        /// <param name="updated">The updated snapshot when this succeeds.</param>
        /// <returns>True when the cache is now current for <paramref name="scope"/>.</returns>
        /// <remarks>
        /// Tried before a full rescan and trusted only if it leaves the cache genuinely current: the
        /// incremental path declines whenever it cannot prove the result (an unopened scene, unsaved state),
        /// and declining has to fall through to the full audit rather than to an optimistic answer.
        /// </remarks>
        private static bool TryUpdateIncrementally(ReferenceAuditScope scope, out ReferenceAuditSnapshot updated)
        {
            updated = null;

            if (ReferenceIndexScheduler.PendingChanges.Count == 0 || !scope.MatchesCacheOf(_snapshotScope))
                return false;

            var candidate = ReferenceIndexScheduler.TryApplyPendingChanges();
            if (candidate == null || _isStale || !scope.MatchesCacheOf(_snapshotScope))
                return false;

            updated = candidate;
            return true;
        }

        /// <summary>
        /// Runs a fresh audit and caches the result.
        /// </summary>
        /// <param name="scope">What to cover. Null uses the project's configured scope.</param>
        /// <param name="progress">Optional progress sink.</param>
        /// <param name="cancellationToken">Cancels the run.</param>
        /// <returns>The new snapshot. Never null.</returns>
        public static ReferenceAuditSnapshot Refresh(
            ReferenceAuditScope scope = null,
            Action<string, float> progress = null,
            CancellationToken cancellationToken = default)
        {
            scope ??= DefaultScope();
            return Store(scope, ReferenceAuditEngine.Run(scope, progress, cancellationToken));
        }

        /// <summary>
        /// Runs a fresh audit without stalling the editor, and caches the result.
        /// </summary>
        /// <param name="scope">What to cover. Null uses the project's configured scope.</param>
        /// <param name="progress">Optional progress sink.</param>
        /// <param name="cancellationToken">Cancels the run; observable within roughly one work unit.</param>
        /// <returns>The new snapshot. Never null.</returns>
        /// <remarks>
        /// Preferred for anything user-facing. <see cref="Refresh"/> exists for callers that genuinely
        /// cannot await — a build callback, or a test that needs a result in one statement.
        /// </remarks>
        public static async Awaitable<ReferenceAuditSnapshot> RefreshAsync(
            ReferenceAuditScope scope = null,
            Action<string, float> progress = null,
            CancellationToken cancellationToken = default)
        {
            scope ??= DefaultScope();
            return Store(scope, await ReferenceAuditEngine.RunAsync(scope, progress, cancellationToken));
        }

        /// <summary>
        /// Returns a snapshot for <paramref name="scope"/> without stalling the editor, reusing the cached
        /// one when it is current and was produced for an equivalent scope.
        /// </summary>
        /// <param name="scope">What to cover. Null uses the project's configured scope.</param>
        /// <param name="progress">Optional progress sink for a fresh run.</param>
        /// <param name="cancellationToken">Cancels a fresh run.</param>
        /// <returns>A snapshot. Never null.</returns>
        public static async Awaitable<ReferenceAuditSnapshot> GetOrRunAsync(
            ReferenceAuditScope scope = null,
            Action<string, float> progress = null,
            CancellationToken cancellationToken = default)
        {
            scope ??= DefaultScope();

            if (!_isStale && scope.MatchesCacheOf(_snapshotScope))
                return _snapshot;

            if (TryUpdateIncrementally(scope, out var updated))
                return updated;

            return await RefreshAsync(scope, progress, cancellationToken);
        }

        /// <summary>
        /// Adopts a snapshot this service did not run itself — one restored from the on-disk index, or
        /// produced by an incremental update.
        /// </summary>
        /// <param name="snapshot">The snapshot to adopt.</param>
        /// <param name="scope">The scope it answers for.</param>
        /// <param name="origin">Human-readable description of where it came from, for diagnostics.</param>
        /// <returns>The adopted snapshot, or the current one when <paramref name="snapshot"/> is null.</returns>
        /// <remarks>
        /// Adopting goes through exactly the same <see cref="Store"/> path as a fresh run, deliberately: a
        /// restored snapshot has to face the same staleness rule and raise the same event, or consumers would
        /// have to care where a snapshot came from in order to know whether to trust it.
        /// </remarks>
        internal static ReferenceAuditSnapshot Adopt(
            ReferenceAuditSnapshot snapshot, ReferenceAuditScope scope, string origin)
        {
            if (snapshot == null)
                return _snapshot;

            if (!string.IsNullOrEmpty(origin))
                Debug.Log($"[ReferenceIndex] {origin}.");

            return Store(scope ?? snapshot.Scope, snapshot);
        }

        /// <summary>Caches a freshly produced snapshot and notifies subscribers.</summary>
        private static ReferenceAuditSnapshot Store(ReferenceAuditScope scope, ReferenceAuditSnapshot snapshot)
        {
            _snapshot = snapshot;
            _snapshotScope = scope;

            // Stale means "the project moved on", not "the audit did not look everywhere". A deliberately
            // skipped category is reported through Coverage and shows up as Incomplete, but re-running the
            // same scope would skip it again, so it is not a reason to distrust this snapshot — and treating
            // it as one made every repair plan unapplyable in any project without prefab scan paths
            // configured, which is the common case. A coverage *failure* is different: the run did not do
            // what it set out to do, and re-running may produce a better answer.
            _isStale = snapshot.Coverage.HasFailures;
            _staleReason = _isStale
                ? $"the last audit could not complete part of its scope ({snapshot.Coverage.DescribeGaps()})"
                : string.Empty;

            RaiseSnapshotChanged(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Marks the cached snapshot stale.
        /// </summary>
        /// <param name="reason">Human-readable reason, surfaced through <see cref="StaleReason"/>.</param>
        public static void Invalidate(string reason)
        {
            _isStale = true;
            _staleReason = string.IsNullOrEmpty(reason) ? "the project changed" : reason;
        }

        /// <summary>
        /// The scope described by the project's <see cref="ReferenceManagerSettings"/>, or open scenes
        /// only when no settings asset exists.
        /// </summary>
        /// <param name="mayOpenScenes">Whether the caller permits opening closed scenes.</param>
        public static ReferenceAuditScope DefaultScope(bool mayOpenScenes = false) =>
            ReferenceAuditScope.FromSettings(FindSettings(), mayOpenScenes);

        /// <summary>
        /// Locates the project's reference settings asset without throwing in edit mode.
        /// </summary>
        /// <returns>The settings asset, or null when the project has none.</returns>
        public static ReferenceManagerSettings FindSettings()
        {
            // GlobalSettings.GetModule can throw before project settings exist, so the asset search is
            // the fallback rather than the other way round.
            try
            {
                var fromGlobals = GlobalSettings.GetModule<ReferenceManagerSettings>();
                if (fromGlobals != null)
                    return fromGlobals;
            }
            catch (Exception)
            {
                // fall through to the asset search
            }

            foreach (var guid in AssetDatabase.FindAssets("t:ReferenceManagerSettings"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<ReferenceManagerSettings>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null)
                    return asset;
            }

            return null;
        }

        private static void RaiseSnapshotChanged(ReferenceAuditSnapshot snapshot)
        {
            var handlers = SnapshotChanged;
            if (handlers == null)
                return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<ReferenceAuditSnapshot>)handler).Invoke(snapshot);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ReferenceAudit] SnapshotChanged handler threw: {e}");
                }
            }
        }

        #region Invalidation hooks

        [InitializeOnLoadMethod]
        private static void InstallInvalidationHooks()
        {
            EditorSceneManager.sceneOpened += (scene, _) => Invalidate($"scene '{scene.name}' was opened");
            EditorSceneManager.sceneClosed += scene => Invalidate($"scene '{scene.name}' was closed");
            EditorSceneManager.sceneSaved += scene => Invalidate($"scene '{scene.name}' was saved");
            PrefabStage.prefabSaved += prefab => Invalidate($"prefab '{prefab.name}' was saved");
            EditorApplication.hierarchyChanged += () => Invalidate("the scene hierarchy changed");
            EditorApplication.playModeStateChanged += state => Invalidate($"play mode state changed to {state}");

            // Field layouts can change with a recompile, so the "could this type hold a reference?"
            // answers must not survive one.
            ReferenceFieldTypeCache.Invalidate();
            ReferenceAssetPolicy.InvalidateCache();
        }

        #endregion
    }

    /// <summary>
    /// Invalidates the cached audit snapshot whenever assets are imported, moved or deleted.
    /// </summary>
    /// <remarks>
    /// A top-level type rather than a nested one: Unity discovers <see cref="AssetPostprocessor"/>
    /// implementations by scanning assemblies, and a nested declaration is not a documented-supported
    /// shape for that scan.
    /// </remarks>
    internal sealed class ReferenceAuditAssetInvalidator : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            var changed = (importedAssets?.Length ?? 0) + (deletedAssets?.Length ?? 0) + (movedAssets?.Length ?? 0);
            if (changed > 0)
                ReferenceAuditService.Invalidate($"{changed} asset(s) were imported, moved or deleted");
        }
    }
}
