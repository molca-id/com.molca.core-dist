using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags lighting setups that are expensive at runtime: too many realtime lights, too
    /// many realtime shadow casters, and — when the scene is baked — renderers that are not
    /// contributing to global illumination.
    /// </summary>
    /// <remarks>
    /// <see cref="Utilities.BudgetSettings"/> carries no light thresholds, so these use
    /// conservative heuristic limits. Static, main-thread scene-graph audit;
    /// see <see cref="SceneBudgetContext"/>.
    /// </remarks>
    public sealed class SceneLightingBudgetCheck : IDoctorCheck
    {
        public string Id => "scene-lighting-budget";
        public string Description => "Realtime light / shadow-caster overuse and missing GI contribution";

        /// <summary>Realtime lights at/above this count warn; double that errors.</summary>
        private const int RealtimeLightWarn = 8;

        /// <summary>Realtime shadow-casting lights at/above this count warn.</summary>
        private const int RealtimeShadowWarn = 4;

        private const int MaxOffenders = 8;

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            var issues = new List<DoctorIssue>();
            var sceneCtx = SceneBudgetContext.Acquire();

            foreach (var snap in sceneCtx.Scenes)
            {
                if (context.IsIgnored(snap.PathOrName))
                    continue;

                context.ReportStatus($"Lighting: {snap.Scene.name}");
                await SceneBudgetContext.EditorYieldAsync(cancellationToken);

                var realtime = snap.Lights.Where(IsRealtime).ToList();
                int realtimeShadows = realtime.Count(l => l.shadows != LightShadows.None);
                bool hasBaked = snap.Lights.Any(l => !IsRealtime(l));

                if (SceneBudgetContext.GradeOverBudget(realtime.Count, RealtimeLightWarn, errorFactor: 2.0) is { } sev)
                    issues.Add(new DoctorIssue(Id, sev,
                        $"Scene '{snap.Scene.name}' has {realtime.Count} realtime lights (heuristic budget {RealtimeLightWarn}). " +
                        "Bake static lighting or convert distant lights to baked/mixed.",
                        snap.PathOrName));

                if (realtimeShadows > RealtimeShadowWarn)
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"Scene '{snap.Scene.name}' has {realtimeShadows} realtime shadow-casting lights " +
                        $"(heuristic budget {RealtimeShadowWarn}). Realtime shadows are costly — disable shadows on " +
                        "minor lights or bake them.",
                        snap.PathOrName));

                // When the scene is (partly) baked, renderers that don't contribute GI look
                // like an authoring oversight. Info — only meaningful with baked lights present.
                if (hasBaked)
                {
                    var notContributing = snap.Renderers
                        .Where(r => r is MeshRenderer &&
                                    (GameObjectUtility.GetStaticEditorFlags(r.gameObject) & StaticEditorFlags.ContributeGI) == 0)
                        .Take(MaxOffenders);

                    foreach (var renderer in notContributing)
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Info,
                            $"Renderer '{renderer.gameObject.name}' is not marked Contribute GI in a baked scene; " +
                            "it won't receive baked lighting. Mark it Contribute GI if it should be lit by bakes.",
                            $"{snap.PathOrName} :: {GameObjectEditingService.GetHierarchyPath(renderer.gameObject)}"));
                }
            }

            return issues;
        }

        private static bool IsRealtime(Light light) => light.lightmapBakeType == LightmapBakeType.Realtime;
    }
}
