using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Low-confidence convention heuristic: flags project MonoBehaviours on scene objects whose
    /// names suggest service/manager responsibilities that, per Molca conventions, belong in a
    /// <c>RuntimeSubsystem</c> or a pure C# class rather than a scene-placed MonoBehaviour.
    /// </summary>
    /// <remarks>
    /// This is the convention half of the scene audit — the part a generic profiler cannot do.
    /// It is intentionally conservative and emits <see cref="DoctorSeverity.Info"/> only:
    /// framework (<c>Molca.*</c>) and Unity types are excluded, as are types already deriving
    /// from a <c>RuntimeSubsystem</c>. Static, main-thread scene-graph audit.
    /// </remarks>
    public sealed class SubsystemPlacementHintCheck : IDoctorCheck
    {
        public string Id => "scene-subsystem-placement";
        public string Description => "Scene MonoBehaviours that may belong in a RuntimeSubsystem (convention hint)";

        private static readonly string[] ServiceNameSuffixes = { "Manager", "Service", "System", "Controller" };
        private const int MaxOffenders = 10;

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            var issues = new List<DoctorIssue>();
            var sceneCtx = SceneBudgetContext.Acquire();

            // Don't report the same MonoBehaviour type twice — one hint per type per scene.
            var reportedTypes = new HashSet<Type>();
            int hinted = 0;

            foreach (var snap in sceneCtx.Scenes)
            {
                if (context.IsIgnored(snap.PathOrName))
                    continue;

                context.ReportStatus($"Placement: {snap.Scene.name}");
                await SceneBudgetContext.EditorYieldAsync(cancellationToken);

                foreach (var root in snap.Scene.GetRootGameObjects())
                {
                    if (hinted >= MaxOffenders)
                        break;

                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
                    {
                        if (mb == null || hinted >= MaxOffenders)
                            continue;

                        var type = mb.GetType();
                        if (!reportedTypes.Add(type) || !LooksLikeMisplacedService(type))
                            continue;

                        hinted++;
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Info,
                            $"'{type.Name}' on '{mb.gameObject.name}' looks like a service/manager placed on a scene " +
                            "MonoBehaviour. Per Molca conventions, business logic belongs in a RuntimeSubsystem " +
                            "(registered with RuntimeManager) or a pure C# class. Verify this isn't a misplaced service.",
                            $"{snap.PathOrName} :: {GameObjectEditingService.GetHierarchyPath(mb.gameObject)}"));
                    }
                }
            }

            return issues;
        }

        private static bool LooksLikeMisplacedService(Type type)
        {
            // Framework and engine types are out of scope — only first-party project code.
            var ns = type.Namespace ?? "";
            if (ns.StartsWith("Molca", StringComparison.Ordinal) || ns.StartsWith("Unity", StringComparison.Ordinal))
                return false;
            if (DerivesFromRuntimeSubsystem(type))
                return false;
            return ServiceNameSuffixes.Any(s => type.Name.EndsWith(s, StringComparison.Ordinal));
        }

        // Walk the base chain by name so we don't take a hard dependency on the runtime type.
        private static bool DerivesFromRuntimeSubsystem(Type type)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
                if (t.Name == "RuntimeSubsystem")
                    return true;
            return false;
        }
    }
}
