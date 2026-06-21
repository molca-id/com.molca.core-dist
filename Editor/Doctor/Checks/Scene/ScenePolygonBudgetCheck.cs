using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags open scenes whose rendered triangle total exceeds
    /// <see cref="Utilities.BudgetSettings.MaxTriangles"/>, and calls out the individual
    /// high-poly renderers driving the cost so the finding is actionable.
    /// </summary>
    /// <remarks>Static, main-thread scene-graph audit; see <see cref="SceneBudgetContext"/>.</remarks>
    public sealed class ScenePolygonBudgetCheck : IDoctorCheck
    {
        public string Id => "scene-polygon-budget";
        public string Description => "Scene triangle total vs the platform triangle budget";

        /// <summary>A single renderer above this fraction of the budget is itself reported.</summary>
        private const double SingleOffenderFraction = 0.25;

        /// <summary>Cap on individually-named offenders per scene to keep findings readable.</summary>
        private const int MaxOffenders = 5;

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            var issues = new List<DoctorIssue>();
            var sceneCtx = SceneBudgetContext.Acquire();
            long maxTri = sceneCtx.Budget.Settings.MaxTriangles;

            foreach (var snap in sceneCtx.Scenes)
            {
                if (context.IsIgnored(snap.PathOrName))
                    continue;

                context.ReportStatus($"Polygons: {snap.Scene.name}");
                await SceneBudgetContext.EditorYieldAsync(cancellationToken);

                var severity = SceneBudgetContext.GradeOverBudget(snap.TotalTriangles, maxTri);
                if (severity is { } sev)
                {
                    issues.Add(new DoctorIssue(Id, sev,
                        $"Scene '{snap.Scene.name}' renders {snap.TotalTriangles:N0} triangles, over the " +
                        $"{maxTri:N0} budget ({Ratio(snap.TotalTriangles, maxTri)}). " +
                        "Reduce mesh density, add LODs, or raise the budget if intentional.",
                        snap.PathOrName));
                }

                // Name the heaviest individual renderers so the offender is actionable, not
                // just the aggregate — even when the scene total is within budget.
                var heavy = snap.Renderers
                    .Select(r => (renderer: r, tris: TriangleCountOf(r)))
                    .Where(x => x.tris > maxTri * SingleOffenderFraction)
                    .OrderByDescending(x => x.tris)
                    .Take(MaxOffenders);

                foreach (var (renderer, tris) in heavy)
                {
                    var offenderSeverity = tris > maxTri ? DoctorSeverity.Error : DoctorSeverity.Warning;
                    issues.Add(new DoctorIssue(Id, offenderSeverity,
                        $"Renderer '{renderer.gameObject.name}' alone draws {tris:N0} triangles " +
                        $"({Ratio(tris, maxTri)} of budget). Consider an LOD or a lower-poly mesh.",
                        $"{snap.PathOrName} :: {GameObjectEditingService.GetHierarchyPath(renderer.gameObject)}"));
                }
            }

            return issues;
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

        private static string Ratio(double actual, double budget) =>
            budget <= 0 ? "n/a" : $"{actual / budget:P0}";
    }
}
