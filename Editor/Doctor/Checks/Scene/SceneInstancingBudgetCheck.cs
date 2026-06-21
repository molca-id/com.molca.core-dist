using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags scenes over the material/mesh instance budgets and renderers that share a
    /// material with many others without GPU instancing or static batching — the cheapest
    /// draw-call wins a generic profiler won't phrase against the project's budget.
    /// </summary>
    /// <remarks>Static, main-thread scene-graph audit; see <see cref="SceneBudgetContext"/>.</remarks>
    public sealed class SceneInstancingBudgetCheck : IDoctorCheck
    {
        public string Id => "scene-instancing-budget";
        public string Description => "Material/mesh instance counts and missing GPU-instancing / static batching";

        /// <summary>A material shared by at least this many renderers is worth instancing/batching.</summary>
        private const int SharedRendererThreshold = 3;

        private const int MaxOffenders = 6;

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            var issues = new List<DoctorIssue>();
            var sceneCtx = SceneBudgetContext.Acquire();
            int maxMat = sceneCtx.Budget.Settings.MaxMaterialInstances;
            int maxMesh = sceneCtx.Budget.Settings.MaxMeshInstances;

            foreach (var snap in sceneCtx.Scenes)
            {
                if (context.IsIgnored(snap.PathOrName))
                    continue;

                context.ReportStatus($"Instancing: {snap.Scene.name}");
                await SceneBudgetContext.EditorYieldAsync(cancellationToken);

                if (SceneBudgetContext.GradeOverBudget(snap.UniqueMaterials.Count, maxMat) is { } matSev)
                    issues.Add(new DoctorIssue(Id, matSev,
                        $"Scene '{snap.Scene.name}' uses {snap.UniqueMaterials.Count} unique materials, over the " +
                        $"{maxMat} budget. Share materials or use material property blocks.",
                        snap.PathOrName));

                if (SceneBudgetContext.GradeOverBudget(snap.UniqueMeshes.Count, maxMesh) is { } meshSev)
                    issues.Add(new DoctorIssue(Id, meshSev,
                        $"Scene '{snap.Scene.name}' uses {snap.UniqueMeshes.Count} unique meshes, over the " +
                        $"{maxMesh} budget. Reuse meshes or combine where possible.",
                        snap.PathOrName));

                issues.AddRange(BatchingHints(snap));
            }

            return issues;
        }

        // Materials shared by many renderers that are neither GPU-instanced nor batching-static
        // are leaving draw calls on the table. Reported Info — it's a hint, not a defect.
        private IEnumerable<DoctorIssue> BatchingHints(SceneBudgetSnapshot snap)
        {
            var byMaterial = new Dictionary<Material, List<Renderer>>();
            foreach (var renderer in snap.Renderers)
            {
                var mats = renderer.sharedMaterials;
                if (mats == null)
                    continue;
                foreach (var mat in mats.Where(m => m != null).Distinct())
                {
                    if (!byMaterial.TryGetValue(mat, out var list))
                        byMaterial[mat] = list = new List<Renderer>();
                    list.Add(renderer);
                }
            }

            var hinted = 0;
            foreach (var (mat, renderers) in byMaterial.OrderByDescending(kv => kv.Value.Count))
            {
                if (hinted >= MaxOffenders)
                    yield break;
                if (renderers.Count < SharedRendererThreshold || mat.enableInstancing)
                    continue;

                bool anyNonBatched = renderers.Any(r =>
                    (GameObjectUtility.GetStaticEditorFlags(r.gameObject) & StaticEditorFlags.BatchingStatic) == 0);
                if (!anyNonBatched)
                    continue;

                hinted++;
                yield return new DoctorIssue(Id, DoctorSeverity.Info,
                    $"Material '{mat.name}' is shared by {renderers.Count} renderers in '{snap.Scene.name}' " +
                    "but is not GPU-instanced and some users are not batching-static. " +
                    "Enable 'GPU Instancing' on the material or mark the renderers Batching Static to cut draw calls.",
                    AssetDatabase.GetAssetPath(mat));
            }
        }
    }
}
