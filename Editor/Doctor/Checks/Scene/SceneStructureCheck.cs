using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags scene-structure costs: too many active GameObjects, excessively deep hierarchies,
    /// and high-poly renderers with no <see cref="LODGroup"/>. Also surfaces which budget the
    /// audit resolved (and warns when no platform-specific budget matched).
    /// </summary>
    /// <remarks>Static, main-thread scene-graph audit; see <see cref="SceneBudgetContext"/>.</remarks>
    public sealed class SceneStructureCheck : IDoctorCheck
    {
        public string Id => "scene-structure";
        public string Description => "Active object count, hierarchy depth, and missing LODGroups";

        private const int DeepHierarchyDepth = 12;
        private const long LodCandidateTriangles = 5000;
        private const int MaxOffenders = 6;

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            var issues = new List<DoctorIssue>();
            var sceneCtx = SceneBudgetContext.Acquire();

            // Surface the resolved budget once, here, so a Doctor-window run reports which
            // budget it graded against (the MCP tool repeats this in its summary).
            issues.AddRange(BudgetProvenance(sceneCtx.Budget));

            int maxGO = sceneCtx.Budget.Settings.MaxGameObjects;

            foreach (var snap in sceneCtx.Scenes)
            {
                if (context.IsIgnored(snap.PathOrName))
                    continue;

                context.ReportStatus($"Structure: {snap.Scene.name}");
                await SceneBudgetContext.EditorYieldAsync(cancellationToken);

                if (SceneBudgetContext.GradeOverBudget(snap.ActiveGameObjectCount, maxGO) is { } sev)
                    issues.Add(new DoctorIssue(Id, sev,
                        $"Scene '{snap.Scene.name}' has {snap.ActiveGameObjectCount} active GameObjects, over the " +
                        $"{maxGO} budget. Pool, combine, or deactivate off-screen objects.",
                        snap.PathOrName));

                int deep = 0;
                int lodHinted = 0;
                foreach (var renderer in snap.Renderers)
                {
                    if (deep < MaxOffenders && Depth(renderer.transform) > DeepHierarchyDepth)
                    {
                        deep++;
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Info,
                            $"'{renderer.gameObject.name}' is nested {Depth(renderer.transform)} levels deep " +
                            $"(>{DeepHierarchyDepth}); deep hierarchies cost transform updates. Flatten where practical.",
                            $"{snap.PathOrName} :: {GameObjectEditingService.GetHierarchyPath(renderer.gameObject)}"));
                    }

                    if (lodHinted < MaxOffenders &&
                        TriangleCountOf(renderer) > LodCandidateTriangles &&
                        renderer.GetComponentInParent<LODGroup>() == null)
                    {
                        lodHinted++;
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Info,
                            $"High-poly renderer '{renderer.gameObject.name}' has no LODGroup. " +
                            "Add LODs so it cheapens at distance.",
                            $"{snap.PathOrName} :: {GameObjectEditingService.GetHierarchyPath(renderer.gameObject)}"));
                    }
                }
            }

            return issues;
        }

        private IEnumerable<DoctorIssue> BudgetProvenance(BudgetSettingsResolver.Resolution budget)
        {
            if (budget.IsDefault)
                yield return new DoctorIssue(Id, DoctorSeverity.Info,
                    "No BudgetSettings asset found — scene budgets graded against framework defaults. " +
                    "Author a BudgetSettings asset (Mobile/PC/Quest) for project-specific thresholds.");
            else if (budget.IsPlatformMismatch)
                yield return new DoctorIssue(Id, DoctorSeverity.Warning,
                    $"No BudgetSettings matched the active build target — grading against '{budget.Settings.name}' " +
                    "as a fallback. Name a budget for this platform (e.g. contains 'Mobile'/'PC'/'Quest').");
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            while (t.parent != null) { d++; t = t.parent; }
            return d;
        }

        private static long TriangleCountOf(Renderer renderer)
        {
            var mesh = SceneBudgetContext.MeshFor(renderer);
            if (mesh == null)
                return 0;
            long total = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                total += mesh.GetIndexCount(s) / 3;
            return total;
        }
    }
}
