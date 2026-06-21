using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// One open scene's gathered renderables, lights, and pre-computed aggregates, used by
    /// the scene-performance Doctor checks so each check does not re-walk the hierarchy.
    /// </summary>
    public sealed class SceneBudgetSnapshot
    {
        /// <summary>The loaded scene this snapshot describes.</summary>
        public Scene Scene;

        /// <summary>Project-relative scene path, or the scene name for an untitled scene.</summary>
        public string PathOrName;

        /// <summary>Count of GameObjects active in the hierarchy (matches runtime cost intent).</summary>
        public int ActiveGameObjectCount;

        /// <summary>Active, enabled <see cref="MeshRenderer"/>/<see cref="SkinnedMeshRenderer"/> in the scene.</summary>
        public readonly List<Renderer> Renderers = new();

        /// <summary>Active, enabled <see cref="Light"/> components in the scene.</summary>
        public readonly List<Light> Lights = new();

        /// <summary>Triangle total across the rendered shared meshes (shared meshes counted once per renderer use).</summary>
        public long TotalTriangles;

        /// <summary>Distinct materials referenced by the scene's renderers.</summary>
        public readonly HashSet<Material> UniqueMaterials = new();

        /// <summary>Distinct meshes rendered in the scene.</summary>
        public readonly HashSet<Mesh> UniqueMeshes = new();
    }

    /// <summary>
    /// Shared, single-walk view of the open scenes for the scene-performance audit: the
    /// resolved platform <see cref="Utilities.BudgetSettings"/> plus a per-scene
    /// <see cref="SceneBudgetSnapshot"/>.
    /// </summary>
    /// <remarks>
    /// Editor-only, main thread only (touches the scene graph and <see cref="AssetDatabase"/>).
    /// Build it once per audit via <see cref="Acquire"/> (cached, invalidated when the loaded
    /// scene set or any scene's dirty/root signature changes) so a Doctor run of all scene
    /// checks walks each scene once; tests use <see cref="Build"/> for a guaranteed-fresh view.
    /// </remarks>
    public sealed class SceneBudgetContext
    {
        /// <summary>The budget the audit grades against, with provenance.</summary>
        public BudgetSettingsResolver.Resolution Budget { get; private set; }

        /// <summary>Per-scene snapshots for every loaded scene.</summary>
        public IReadOnlyList<SceneBudgetSnapshot> Scenes { get; private set; }

        private static SceneBudgetContext _cached;
        private static string _cachedSignature;
        private static SceneBudgetContext _testOverride;

        /// <summary>
        /// Returns a cached context for the currently open scenes, rebuilding only when the
        /// loaded-scene set or a scene's dirty/root-count signature has changed since the last
        /// build. Use from checks so a single audit walks the scenes once.
        /// </summary>
        public static SceneBudgetContext Acquire()
        {
            if (_testOverride != null)
                return _testOverride;

            var signature = ComputeSignature();
            if (_cached != null && _cachedSignature == signature)
                return _cached;

            _cached = Build();
            _cachedSignature = signature;
            return _cached;
        }

        /// <summary>Drops the cached context so the next <see cref="Acquire"/> rebuilds.</summary>
        public static void Invalidate()
        {
            _cached = null;
            _cachedSignature = null;
        }

        /// <summary>
        /// Builds a context from an explicit budget and snapshots, for tests. <see cref="Acquire"/>
        /// returns the seeded context (via <see cref="SetTestOverride"/>) regardless of open scenes.
        /// </summary>
        public static SceneBudgetContext CreateForTesting(
            Utilities.BudgetSettings settings, params SceneBudgetSnapshot[] scenes) =>
            new SceneBudgetContext
            {
                Budget = new BudgetSettingsResolver.Resolution(settings, "test", isDefault: false, isPlatformMismatch: false),
                Scenes = scenes ?? System.Array.Empty<SceneBudgetSnapshot>(),
            };

        /// <summary>Forces <see cref="Acquire"/> to return <paramref name="ctx"/> until cleared. Test-only.</summary>
        public static void SetTestOverride(SceneBudgetContext ctx) => _testOverride = ctx;

        /// <summary>Clears any test override set by <see cref="SetTestOverride"/>.</summary>
        public static void ClearTestOverride() => _testOverride = null;

        /// <summary>Builds a fresh context from the currently loaded scenes (no caching).</summary>
        public static SceneBudgetContext Build()
        {
            var ctx = new SceneBudgetContext
            {
                Budget = BudgetSettingsResolver.Resolve(),
            };

            var snapshots = new List<SceneBudgetSnapshot>();
            // Shared per-mesh triangle cache so an instanced mesh is measured once.
            var triangleCache = new Dictionary<Mesh, long>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                snapshots.Add(BuildSnapshot(scene, triangleCache));
            }

            ctx.Scenes = snapshots;
            return ctx;
        }

        private static SceneBudgetSnapshot BuildSnapshot(Scene scene, Dictionary<Mesh, long> triangleCache)
        {
            var snap = new SceneBudgetSnapshot
            {
                Scene = scene,
                PathOrName = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path,
            };

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive: true))
                    if (t.gameObject.activeInHierarchy)
                        snap.ActiveGameObjectCount++;

                foreach (var renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
                {
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                        continue;
                    if (renderer is not (MeshRenderer or SkinnedMeshRenderer))
                        continue; // particle/line/trail renderers have no static triangle/material budget here

                    snap.Renderers.Add(renderer);

                    var mesh = MeshFor(renderer);
                    if (mesh != null)
                    {
                        snap.UniqueMeshes.Add(mesh);
                        snap.TotalTriangles += TriangleCount(mesh, triangleCache);
                    }

                    var materials = renderer.sharedMaterials;
                    if (materials != null)
                        foreach (var m in materials)
                            if (m != null)
                                snap.UniqueMaterials.Add(m);
                }

                foreach (var light in root.GetComponentsInChildren<Light>(includeInactive: true))
                    if (light != null && light.enabled && light.gameObject.activeInHierarchy)
                        snap.Lights.Add(light);
            }

            return snap;
        }

        /// <summary>Returns the shared mesh a mesh/skinned renderer draws, or null.</summary>
        public static Mesh MeshFor(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer skinned:
                    return skinned.sharedMesh;
                case MeshRenderer when renderer.TryGetComponent<MeshFilter>(out var filter):
                    return filter.sharedMesh;
                default:
                    return null;
            }
        }

        private static long TriangleCount(Mesh mesh, Dictionary<Mesh, long> cache)
        {
            if (mesh == null)
                return 0;
            if (cache.TryGetValue(mesh, out var cached))
                return cached;

            long total = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                total += mesh.GetIndexCount(s) / 3; // index count is 3 per triangle for triangle topology

            cache[mesh] = total;
            return total;
        }

        // ── Shared severity grading ──────────────────────────────────────────────

        /// <summary>
        /// Grades an aggregate against a budget: at-or-under budget is no finding (null);
        /// over budget is a <see cref="DoctorSeverity.Warning"/>; over by
        /// <paramref name="errorFactor"/>× is an <see cref="DoctorSeverity.Error"/>.
        /// </summary>
        /// <returns>The severity, or null when within budget.</returns>
        public static DoctorSeverity? GradeOverBudget(double actual, double budget, double errorFactor = 1.5)
        {
            if (budget <= 0 || actual <= budget)
                return null;
            return actual > budget * errorFactor ? DoctorSeverity.Error : DoctorSeverity.Warning;
        }

        private static string ComputeSignature()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                sb.Append(scene.handle).Append(':')
                  .Append(scene.isLoaded ? scene.rootCount : -1).Append(':')
                  .Append(scene.isDirty ? 'D' : 'C').Append('|');
            }
            return sb.ToString();
        }

        // ── Edit-mode-safe yield (shared by scene checks) ────────────────────────

        /// <summary>
        /// Yields until the next editor tick, honoring cancellation. Scene checks use this
        /// instead of <c>Awaitable.NextFrameAsync</c> because the player loop that drives it
        /// does not advance in Edit Mode, so awaiting a frame there never resumes.
        /// </summary>
        public static Awaitable EditorYieldAsync(CancellationToken cancellationToken)
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
    }
}
