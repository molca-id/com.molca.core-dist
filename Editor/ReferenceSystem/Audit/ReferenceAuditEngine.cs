using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// Discovers reference providers and reference sites across a declared scope and produces one
    /// <see cref="ReferenceAuditSnapshot"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>The audit never writes.</b> No <c>SetDirty</c>, no <c>ApplyModifiedProperties</c>, no
    /// <c>AssetDatabase.SaveAssets</c>, no id generation. A button labelled Scan, Refresh, Validate or
    /// Audit only ever reads. Repair is a separate, previewed, transactional operation.</para>
    ///
    /// <para>Opening a closed scene is the one exception to "does not disturb the editor", and it is
    /// gated on <see cref="ReferenceAuditScope.MayOpenScenes"/>. The engine restores the user's scene
    /// setup afterwards, and refuses to open anything when an open scene has unsaved in-memory state that
    /// could not be restored.</para>
    ///
    /// <para><b>One implementation, two drivers.</b> The scan is written once, as an iterator that yields
    /// after each unit of work (<see cref="EnumerateWork"/>). <see cref="Run"/> drains it straight through
    /// for callers that must be synchronous — chiefly the build gate, since a build callback cannot await.
    /// <see cref="RunAsync"/> drains the same iterator but hands the main thread back whenever it has held
    /// it for longer than <see cref="MaxMillisecondsBetweenYields"/>, so the editor keeps painting and a
    /// Cancel button is actually observable. Writing the scan twice would let the two paths drift, which is
    /// the mistake this whole subsystem exists to undo.</para>
    /// </remarks>
    public static class ReferenceAuditEngine
    {
        /// <summary>
        /// How long <see cref="RunAsync"/> may hold the main thread before yielding.
        /// </summary>
        /// <remarks>
        /// Well inside the 100 ms stall budget, and roughly a frame at 30 fps, so the editor stays
        /// interactive without the yield overhead dominating the scan.
        /// </remarks>
        private const long MaxMillisecondsBetweenYields = 33;

        /// <summary>One unit of scan progress: which phase, and how far through the run.</summary>
        private readonly struct WorkStep
        {
            public readonly string Phase;
            public readonly float Fraction;

            public WorkStep(string phase, float fraction)
            {
                Phase = phase;
                Fraction = fraction;
            }
        }

        /// <summary>
        /// The mutable accumulators one audit run fills in, threaded through the phase iterators.
        /// </summary>
        private sealed class RunState
        {
            public readonly ReferenceAuditScope Scope;
            public readonly List<ReferenceProviderRecord> Providers = new();
            public readonly List<ReferenceSiteRecord> Sites = new();
            public readonly List<string> ScanErrors = new();
            public readonly List<ReferenceCoverageEntry> Coverage = new();
            public readonly List<ReferenceScannedAsset> ScannedAssets = new();

            /// <summary>Why this run's result cannot be written to the on-disk index, or null.</summary>
            public string PersistBlockedReason;

            public RunState(ReferenceAuditScope scope) => Scope = scope;

            /// <summary>Fingerprints an asset the run read, so a persisted index can revalidate it later.</summary>
            public void RecordScanned(string assetPath)
            {
                if (!string.IsNullOrEmpty(assetPath))
                    ScannedAssets.Add(ReferenceScannedAsset.Capture(assetPath));
            }

            /// <summary>
            /// Records that this run read state that is not on disk, so its result must not be persisted.
            /// </summary>
            /// <remarks>
            /// First reason wins; they are all equally disqualifying and the first one encountered is the most
            /// useful to report.
            /// </remarks>
            public void BlockPersist(string reason) => PersistBlockedReason ??= reason;
        }

        #region Entry points

        /// <summary>
        /// Runs an audit synchronously, holding the main thread until it completes.
        /// </summary>
        /// <param name="scope">What to cover. Null uses <see cref="ReferenceAuditScope.OpenScenes"/>.</param>
        /// <param name="progress">
        /// Optional progress sink, invoked with a phase description and a 0..1 fraction. Never invoked
        /// after this method returns.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancels the run. A cancelled run still returns a snapshot, with the unreached phases marked as
        /// skipped coverage — a partial result is reported as partial, never as clean. Note that a caller
        /// cannot observe its own token mid-run here; use <see cref="RunAsync"/> when that matters.
        /// </param>
        /// <returns>The snapshot. Never null.</returns>
        public static ReferenceAuditSnapshot Run(
            ReferenceAuditScope scope = null,
            Action<string, float> progress = null,
            CancellationToken cancellationToken = default)
        {
            var state = new RunState(scope ?? ReferenceAuditScope.OpenScenes());
            var stopwatch = Stopwatch.StartNew();

            try
            {
                foreach (var step in EnumerateWork(state, cancellationToken))
                    progress?.Invoke(step.Phase, step.Fraction);
            }
            catch (OperationCanceledException)
            {
                RecordCancellation(state);
            }

            stopwatch.Stop();
            return Complete(state, stopwatch.Elapsed);
        }

        /// <summary>
        /// Runs an audit, yielding the main thread periodically so the editor stays responsive.
        /// </summary>
        /// <param name="scope">What to cover. Null uses <see cref="ReferenceAuditScope.OpenScenes"/>.</param>
        /// <param name="progress">Optional progress sink; a phase description and a 0..1 fraction.</param>
        /// <param name="cancellationToken">
        /// Cancels the run, observably: it is checked at every yield point, so a Cancel button responds
        /// within roughly one work unit. A cancelled run still returns a snapshot marked incomplete.
        /// </param>
        /// <returns>The snapshot. Never null.</returns>
        public static async Awaitable<ReferenceAuditSnapshot> RunAsync(
            ReferenceAuditScope scope = null,
            Action<string, float> progress = null,
            CancellationToken cancellationToken = default)
        {
            var state = new RunState(scope ?? ReferenceAuditScope.OpenScenes());
            var stopwatch = Stopwatch.StartNew();
            var sinceYield = Stopwatch.StartNew();

            try
            {
                // The iterator is stepped manually rather than with foreach so the yield can happen between
                // MoveNext calls; a foreach body cannot await without making the whole method an async
                // iterator, which Unity's Awaitable does not support.
                using var work = EnumerateWork(state, cancellationToken).GetEnumerator();
                while (work.MoveNext())
                {
                    progress?.Invoke(work.Current.Phase, work.Current.Fraction);

                    if (sinceYield.ElapsedMilliseconds < MaxMillisecondsBetweenYields)
                        continue;

                    await EditorYieldAsync(cancellationToken);
                    sinceYield.Restart();
                }
            }
            catch (OperationCanceledException)
            {
                RecordCancellation(state);
            }

            stopwatch.Stop();
            return Complete(state, stopwatch.Elapsed);
        }

        /// <summary>
        /// Checks only the provider identities inside one scene: providers with no Ref Id, and providers
        /// colliding on the same <c>(RefType, RefId)</c>.
        /// </summary>
        /// <param name="scene">The loaded scene to check.</param>
        /// <param name="policy">Severity policy; <see cref="ReferenceSeverityPolicy.Default"/> when null.</param>
        /// <returns>Findings in deterministic order. Empty when the scene's provider identities are sound.</returns>
        /// <remarks>
        /// Deliberately narrow, for the pre-scene-save hook where a full project audit would be far too
        /// expensive. It reports only what a single-scene view can be <i>authoritative</i> about: a missing
        /// or duplicated provider id is a defect no matter what the rest of the project contains. It does
        /// <b>not</b> analyse reference sites, because from one scene a perfectly good cross-scene
        /// reference is indistinguishable from a broken one — reporting those here would be the same
        /// false-confidence mistake in reverse.
        /// </remarks>
        public static IReadOnlyList<ReferenceFinding> CheckSceneProviders(
            Scene scene, ReferenceSeverityPolicy policy = null)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return Array.Empty<ReferenceFinding>();

            var providers = new List<ReferenceProviderRecord>();
            var sceneKey = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour is not IReferenceable)
                        continue;

                    var provider = ReferenceSerializedScanner.TryDescribeProvider(
                        behaviour, ReferenceProviderKind.SceneComponent, sceneKey);
                    if (provider != null)
                        providers.Add(provider);
                }
            }

            // Sites are intentionally empty, and coverage is empty rather than "partial": this call makes
            // no claim about resolvability, so it should not emit a coverage finding either.
            return ReferenceResolutionAnalyzer
                .Analyze(providers, Array.Empty<ReferenceSiteRecord>(), ReferenceCoverage.Empty, policy)
                .Findings;
        }

        /// <summary>
        /// Rebuilds a snapshot by rescanning only the assets that changed.
        /// </summary>
        /// <param name="previous">The snapshot to update. Null returns null.</param>
        /// <param name="changedAssetPaths">Asset paths known to have changed, moved or been deleted.</param>
        /// <returns>
        /// An updated snapshot, or <c>null</c> when the change cannot be applied incrementally and a full
        /// audit is required.
        /// </returns>
        /// <remarks>
        /// <para><b>Analysis is always re-run over everything.</b> Only <i>scanning</i> is incremental. That is
        /// what makes this safe: a reference in one scene resolves against providers in another, so a partial
        /// re-analysis could leave a finding that the changed asset has already fixed — or miss one it just
        /// caused. Re-analysing the full record set is pure and cheap; re-reading every asset is the expensive
        /// part, and that is the part this skips.</para>
        ///
        /// <para>It gives up rather than guessing. A changed scene that is not currently open would have to be
        /// opened to be re-read, which is a scene-setup change this method is not entitled to make, so the
        /// caller is told to run a full audit instead.</para>
        /// </remarks>
        public static ReferenceAuditSnapshot UpdateIncrementally(
            ReferenceAuditSnapshot previous, IReadOnlyCollection<string> changedAssetPaths)
        {
            if (previous == null || changedAssetPaths == null || changedAssetPaths.Count == 0)
                return previous;

            // Only an index that could be revalidated in the first place can be updated: a snapshot built from
            // unsaved state has no fingerprint to update against.
            if (!previous.CanPersist)
                return null;

            var changed = new HashSet<string>(changedAssetPaths, StringComparer.OrdinalIgnoreCase);
            var scope = previous.Scope;
            var stopwatch = Stopwatch.StartNew();
            var state = new RunState(scope);

            var openScenes = new Dictionary<string, Scene>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && !string.IsNullOrEmpty(scene.path))
                    openScenes[scene.path] = scene;
            }

            foreach (var path in changed)
            {
                if (string.IsNullOrEmpty(path) || scope.ShouldSkip(path))
                    continue;

                // A deleted asset needs no rescan; dropping its records below is the whole update.
                if (!System.IO.File.Exists(path) && AssetDatabase.GetMainAssetTypeAtPath(path) == null)
                    continue;

                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    if (!openScenes.TryGetValue(path, out var scene))
                        return null; // would require opening a scene; that is a full audit's decision

                    if (scene.isDirty)
                        return null; // unsaved state cannot be fingerprinted, so it cannot be indexed

                    state.RecordScanned(path);
                    ScanScene(scene, state);
                    continue;
                }

                if (!RescanAsset(path, scope, state))
                    return null;
            }

            // Everything the previous run knew about assets that did not change is still valid — that is
            // exactly what the fingerprints proved.
            var providers = previous.Providers.Where(p => !changed.Contains(p.Locator.AssetPath)).ToList();
            var sites = previous.Sites.Where(s => !changed.Contains(s.OwnerLocator.AssetPath)).ToList();
            providers.AddRange(state.Providers);
            sites.AddRange(state.Sites);

            var assets = previous.ScannedAssets.Where(a => !changed.Contains(a.AssetPath)).ToList();
            assets.AddRange(state.ScannedAssets);

            // Per-category counts come from the last full run and are not recomputed here, so the incremental
            // pass says so rather than letting the reader take them as current.
            var coverage = previous.Coverage.Entries.Where(e => e.Category != IncrementalCoverageCategory).ToList();
            coverage.Add(new ReferenceCoverageEntry(
                IncrementalCoverageCategory, ReferenceCoverageStatus.Scanned, state.ScannedAssets.Count,
                "category counts above are from the last full audit", isRequired: false));

            var analysis = ReferenceResolutionAnalyzer.Analyze(
                providers, sites, new ReferenceCoverage(coverage), scope.Policy, state.ScanErrors,
                ReferenceLoadSetStore.Evaluate);

            stopwatch.Stop();
            return new ReferenceAuditSnapshot(
                scope, providers, sites, analysis, stopwatch.Elapsed, assets, state.PersistBlockedReason);
        }

        /// <summary>Coverage category naming the incremental pass, so it can be replaced rather than stacked.</summary>
        private const string IncrementalCoverageCategory = "Index (incremental update)";

        /// <summary>
        /// Rescans one non-scene asset into <paramref name="state"/>.
        /// </summary>
        /// <returns>False when the asset cannot be handled incrementally.</returns>
        private static bool RescanAsset(string path, ReferenceAuditScope scope, RunState state)
        {
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                if (!scope.IsPrefabInScope(path))
                    return true; // outside the scope's prefab paths; it contributed nothing before either

                var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
                if (prefab == null)
                    return true;

                if (EditorUtility.IsDirty(prefab))
                    return false;

                state.RecordScanned(path);
                foreach (var behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null)
                    {
                        state.ScanErrors.Add(
                            $"{path}: a component has a missing script; any reference it held cannot be checked");
                        continue;
                    }

                    ScanObject(behaviour, ReferenceProviderKind.PrefabComponent,
                        ReferenceSiteSourceKind.PrefabAsset, path, state);
                }

                return true;
            }

            if (!scope.IncludeScriptableObjects)
                return true;

            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
                return true;

            if (!ReferenceFieldTypeCache.MayContainReference(type)
                && !typeof(IReferenceable).IsAssignableFrom(type))
                return true;

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null)
                return true;

            if (EditorUtility.IsDirty(asset))
                return false;

            state.RecordScanned(path);
            ScanObject(asset, ReferenceProviderKind.ScriptableObjectAsset,
                ReferenceSiteSourceKind.ScriptableObjectAsset, path, state);
            return true;
        }

        /// <summary>
        /// Records that the run stopped early.
        /// </summary>
        /// <remarks>
        /// Recorded as <see cref="ReferenceCoverageStatus.Failed"/> rather than Skipped: the run did not do
        /// what it set out to do, so unlike a deliberately skipped category this <i>is</i> a reason to
        /// re-run, and <see cref="ReferenceCoverage.HasFailures"/> is what tells the cache so.
        /// </remarks>
        private static void RecordCancellation(RunState state) =>
            state.Coverage.Add(new ReferenceCoverageEntry(
                "Audit", ReferenceCoverageStatus.Failed, 0, "cancelled before completion"));

        private static ReferenceAuditSnapshot Complete(RunState state, TimeSpan duration)
        {
            var coverage = new ReferenceCoverage(state.Coverage);
            var analysis = ReferenceResolutionAnalyzer.Analyze(
                state.Providers, state.Sites, coverage, state.Scope.Policy, state.ScanErrors,
                ReferenceLoadSetStore.Evaluate);

            // A run that could not finish describes a project it did not fully read, so persisting it would
            // store a partial answer that a later session would restore as if it were whole.
            if (coverage.HasFailures)
                state.BlockPersist("the audit did not complete every category it attempted");

            return new ReferenceAuditSnapshot(
                state.Scope, state.Providers, state.Sites, analysis, duration,
                state.ScannedAssets, state.PersistBlockedReason);
        }

        /// <summary>
        /// Yields until the next editor tick.
        /// </summary>
        /// <remarks>
        /// Uses <see cref="EditorApplication.update"/> rather than <c>Awaitable.NextFrameAsync</c>: the
        /// player loop that drives the latter does not advance in Edit Mode, so awaiting a frame there
        /// never resumes.
        /// </remarks>
        private static Awaitable EditorYieldAsync(CancellationToken cancellationToken)
        {
            var source = new AwaitableCompletionSource();

            void Tick()
            {
                EditorApplication.update -= Tick;
                if (cancellationToken.IsCancellationRequested)
                    source.SetCanceled();
                else
                    source.SetResult();
            }

            EditorApplication.update += Tick;
            return source.Awaitable;
        }

        #endregion

        /// <summary>
        /// The scan itself: every phase in order, yielding once per unit of work.
        /// </summary>
        /// <remarks>
        /// A phase that borrows the editor's scene setup wraps its steps in <c>try/finally</c>. Iterator
        /// <c>finally</c> blocks run when the enumerator is disposed, so the scene setup is restored even
        /// when the caller abandons the run mid-phase — which is exactly what cancellation does.
        /// </remarks>
        private static IEnumerable<WorkStep> EnumerateWork(RunState state, CancellationToken cancellationToken)
        {
            foreach (var step in EnumerateOpenScenes(state, cancellationToken))
                yield return step;

            foreach (var step in EnumerateExplicitScenes(state, cancellationToken))
                yield return step;

            foreach (var step in EnumeratePrefabs(state, cancellationToken))
                yield return step;

            foreach (var step in EnumerateScriptableObjects(state, cancellationToken))
                yield return step;

            foreach (var step in EnumerateContributors(state))
                yield return step;
        }

        #region Scenes

        private static IEnumerable<WorkStep> EnumerateOpenScenes(
            RunState state, CancellationToken cancellationToken)
        {
            var scope = state.Scope;

            if (!scope.IncludeOpenScenes)
            {
                state.Coverage.Add(new ReferenceCoverageEntry(
                    "Scenes (open)", ReferenceCoverageStatus.Skipped, 0,
                    "not part of this scope", isRequired: false));
                yield break;
            }

            var scanned = 0;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !scene.IsValid())
                    continue;
                if (!string.IsNullOrEmpty(scene.path) && scope.ShouldSkip(scene.path))
                    continue;

                // An untitled or modified scene is read from memory, so no on-disk fingerprint describes what
                // was actually scanned. The result is still correct for right now; it just cannot be
                // revalidated in a later session, so it must not reach the on-disk index.
                if (string.IsNullOrEmpty(scene.path))
                    state.BlockPersist($"scene '{scene.name}' has never been saved");
                else if (scene.isDirty)
                    state.BlockPersist($"scene '{scene.name}' has unsaved changes");
                else
                    state.RecordScanned(scene.path);

                ScanScene(scene, state);
                scanned++;
                yield return new WorkStep($"Scene {scene.name}", 0.1f);
            }

            state.Coverage.Add(new ReferenceCoverageEntry(
                "Scenes (open)", ReferenceCoverageStatus.Scanned, scanned));
        }

        private static IEnumerable<WorkStep> EnumerateExplicitScenes(
            RunState state, CancellationToken cancellationToken)
        {
            var scope = state.Scope;

            var wanted = scope.ExplicitScenePaths
                .Where(p => !scope.ShouldSkip(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (wanted.Count == 0)
            {
                // Reported even though this scope never claimed to cover closed scenes: "we looked at the
                // open scenes" and "we looked at the project" are different guarantees, and the reader has
                // to be able to tell which one they were given.
                state.Coverage.Add(new ReferenceCoverageEntry(
                    "Scenes (closed)", ReferenceCoverageStatus.Skipped, 0,
                    "only open scenes were scanned; enable Comprehensive Scene Scanning and run a project "
                    + "audit to include closed scenes",
                    isRequired: false));
                yield break;
            }

            // Scenes already open were covered in place; only the rest need opening.
            var alreadyOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scope.IncludeOpenScenes)
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && !string.IsNullOrEmpty(scene.path))
                        alreadyOpen.Add(scene.path);
                }
            }

            var toOpen = wanted.Where(p => !alreadyOpen.Contains(p)).ToList();
            if (toOpen.Count == 0)
            {
                state.Coverage.Add(new ReferenceCoverageEntry(
                    "Scenes (declared)", ReferenceCoverageStatus.Scanned, wanted.Count));
                yield break;
            }

            if (!scope.MayOpenScenes)
            {
                state.Coverage.Add(new ReferenceCoverageEntry(
                    "Scenes (declared)", ReferenceCoverageStatus.Skipped, wanted.Count - toOpen.Count,
                    $"{toOpen.Count} scene(s) are closed and this audit is not permitted to open them"));
                yield break;
            }

            if (!ReferenceSceneScanSession.TryBegin(out var session, out var reason))
            {
                state.Coverage.Add(new ReferenceCoverageEntry(
                    "Scenes (declared)", ReferenceCoverageStatus.Failed, wanted.Count - toOpen.Count, reason));
                yield break;
            }

            var scanned = wanted.Count - toOpen.Count;
            try
            {
                for (var i = 0; i < toOpen.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var path = toOpen[i];

                    Scene scene;
                    try
                    {
                        scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    }
                    catch (Exception e)
                    {
                        state.ScanErrors.Add($"{path}: could not be opened for scanning ({e.Message})");
                        continue;
                    }

                    state.RecordScanned(path);
                    ScanScene(scene, state);
                    scanned++;

                    yield return new WorkStep(
                        $"Scene {System.IO.Path.GetFileNameWithoutExtension(path)}",
                        0.1f + 0.3f * ((i + 1) / (float)toOpen.Count));
                }
            }
            finally
            {
                // Runs on normal completion, on cancellation, and when the caller abandons the enumerator.
                session.Restore();
            }

            state.Coverage.Add(scanned == wanted.Count
                ? new ReferenceCoverageEntry("Scenes (declared)", ReferenceCoverageStatus.Scanned, scanned)
                : new ReferenceCoverageEntry(
                    "Scenes (declared)", ReferenceCoverageStatus.Failed, scanned,
                    $"{wanted.Count - scanned} of {wanted.Count} declared scene(s) could not be scanned"));
        }

        private static void ScanScene(Scene scene, RunState state)
        {
            var sceneKey = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    // A null entry is a component whose script is missing. It may well have held a
                    // reference, so it is a coverage error rather than something to skip quietly.
                    if (behaviour == null)
                    {
                        state.ScanErrors.Add(
                            $"{sceneKey}: a component has a missing script; any reference it held cannot be checked");
                        continue;
                    }

                    ScanObject(behaviour, ReferenceProviderKind.SceneComponent,
                        ReferenceSiteSourceKind.Scene, sceneKey, state);
                }
            }
        }

        #endregion

        #region Prefabs

        private static IEnumerable<WorkStep> EnumeratePrefabs(
            RunState state, CancellationToken cancellationToken)
        {
            var scope = state.Scope;

            if (scope.PrefabScanPaths.Count == 0)
            {
                // No configured paths is a configuration choice, not a success. It is reported so a green
                // result cannot be mistaken for "prefabs are fine".
                state.Coverage.Add(new ReferenceCoverageEntry(
                    "Prefab assets", ReferenceCoverageStatus.Skipped, 0,
                    "no Prefab Scan Paths are configured in Reference Manager Settings",
                    isRequired: scope.PrefabCoverageRequired));
                yield break;
            }

            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => scope.IsPrefabInScope(p) && !scope.ShouldSkip(p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            var scanned = 0;
            for (var i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = paths[i];

                GameObject prefab;
                try
                {
                    prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
                }
                catch (Exception e)
                {
                    state.ScanErrors.Add($"{path}: prefab could not be loaded ({e.Message})");
                    continue;
                }

                if (prefab != null)
                {
                    if (EditorUtility.IsDirty(prefab))
                        state.BlockPersist($"prefab '{path}' has unsaved changes");
                    else
                        state.RecordScanned(path);

                    foreach (var behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (behaviour == null)
                        {
                            state.ScanErrors.Add(
                                $"{path}: a component has a missing script; any reference it held cannot be checked");
                            continue;
                        }

                        ScanObject(behaviour, ReferenceProviderKind.PrefabComponent,
                            ReferenceSiteSourceKind.PrefabAsset, path, state);
                    }

                    scanned++;
                }

                yield return new WorkStep(
                    $"Prefabs {i + 1}/{paths.Count}", 0.4f + 0.2f * ((i + 1) / (float)paths.Count));
            }

            state.Coverage.Add(new ReferenceCoverageEntry(
                "Prefab assets", ReferenceCoverageStatus.Scanned, scanned));
        }

        #endregion

        #region ScriptableObjects

        private static IEnumerable<WorkStep> EnumerateScriptableObjects(
            RunState state, CancellationToken cancellationToken)
        {
            var scope = state.Scope;

            if (!scope.IncludeScriptableObjects)
            {
                // Not a required gap: required coverage means "what this scope promised", not "everything
                // imaginable". A scope that deliberately excludes a category would otherwise be permanently
                // Incomplete, which would make the state meaningless — and, because an incomplete audit is
                // treated as stale, would make every repair plan built from it unapplyable.
                state.Coverage.Add(new ReferenceCoverageEntry(
                    "ScriptableObject assets", ReferenceCoverageStatus.Skipped, 0,
                    "not part of this scope", isRequired: false));
                yield break;
            }

            var paths = AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct(StringComparer.Ordinal)
                .Where(p => !scope.ShouldSkip(p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            var scanned = 0;
            var considered = 0;
            for (var i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = paths[i];

                // Reading the type from the importer avoids loading the asset at all for the vast
                // majority that cannot hold a reference — the cost that previously justified skipping
                // ScriptableObjects wholesale, and with them a real broken reference.
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (type == null)
                    continue;

                considered++;
                var mayHoldSite = ReferenceFieldTypeCache.MayContainReference(type);
                var isProvider = typeof(IReferenceable).IsAssignableFrom(type);
                if (!mayHoldSite && !isProvider)
                    continue;

                ScriptableObject asset;
                try
                {
                    asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                }
                catch (Exception e)
                {
                    state.ScanErrors.Add($"{path}: ScriptableObject could not be loaded ({e.Message})");
                    continue;
                }

                if (asset == null)
                    continue;

                if (EditorUtility.IsDirty(asset))
                    state.BlockPersist($"asset '{path}' has unsaved changes");
                else
                    state.RecordScanned(path);

                ScanObject(asset, ReferenceProviderKind.ScriptableObjectAsset,
                    ReferenceSiteSourceKind.ScriptableObjectAsset, path, state);
                scanned++;

                // Only assets actually opened are worth a yield; the type-filtered majority costs almost
                // nothing and yielding per path would make the phase slower than the work it does.
                yield return new WorkStep(
                    $"ScriptableObjects {i + 1}/{paths.Count}", 0.6f + 0.3f * ((i + 1) / (float)paths.Count));
            }

            state.Coverage.Add(new ReferenceCoverageEntry(
                "ScriptableObject assets", ReferenceCoverageStatus.Scanned, scanned));

            // Package-owned ScriptableObjects are outside the writable project and are not scanned, but
            // saying so beats letting the reader assume they were.
            state.Coverage.Add(new ReferenceCoverageEntry(
                "ScriptableObject assets (packages)", ReferenceCoverageStatus.Skipped, 0,
                $"only Assets/ was searched ({considered} asset type(s) considered)", isRequired: false));
        }

        #endregion

        #region Shared

        /// <summary>
        /// Records <paramref name="target"/> as a provider when referenceable, and collects its reference
        /// sites when its type could possibly hold one.
        /// </summary>
        private static void ScanObject(
            UnityEngine.Object target,
            ReferenceProviderKind providerKind,
            ReferenceSiteSourceKind siteKind,
            string assetPathHint,
            RunState state)
        {
            var type = target.GetType();

            if (typeof(IReferenceable).IsAssignableFrom(type))
            {
                var provider = ReferenceSerializedScanner.TryDescribeProvider(target, providerKind, assetPathHint);
                if (provider != null)
                    state.Providers.Add(provider);
            }

            if (!ReferenceFieldTypeCache.MayContainReference(type))
                return;

            ReferenceSerializedScanner.CollectSites(
                target, siteKind, state.Sites, assetPathHint, state.ScanErrors.Add);
        }

        private static IEnumerable<WorkStep> EnumerateContributors(RunState state)
        {
            var contributors = MolcaReferenceIndexContributor.DiscoverAll();
            if (contributors.Count == 0)
                yield break;

            foreach (var contributor in contributors)
            {
                // A context per contributor, so "did this one declare where its records came from?" is a
                // question about that contributor rather than about the whole set.
                var context = new ReferenceCollectionContext(
                    state.Scope, state.Providers, state.Sites, state.ScanErrors, state.Coverage,
                    state.RecordScanned);

                // Isolated per contributor: a throwing add-on downgrades the audit to incomplete instead
                // of taking reference validation down with it.
                try
                {
                    contributor.Collect(context);
                }
                catch (Exception e)
                {
                    state.Coverage.Add(new ReferenceCoverageEntry(
                        $"Contributor {contributor.Id}", ReferenceCoverageStatus.Failed, 0, e.Message));
                }

                // Records with no declared origin cannot be revalidated in a later session, so this run's
                // result stays usable but never reaches the on-disk index. Blocking persistence is the
                // conservative half of the trade: the alternative is restoring records whose inputs may have
                // changed while Unity was closed, which is the exact failure mode the index exists to avoid.
                if (context.ContributedCount > 0 && context.DeclaredAssetCount == 0)
                {
                    state.BlockPersist(
                        $"contributor '{contributor.Id}' added records without declaring the assets they came "
                        + "from (call ReferenceCollectionContext.MarkScanned)");
                }

                yield return new WorkStep($"Contributor {contributor.Id}", 0.95f);
            }
        }

        #endregion
    }
}
