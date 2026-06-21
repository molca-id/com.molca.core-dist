using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags open scenes whose referenced textures exceed
    /// <see cref="Utilities.BudgetSettings.MaxTextureMemoryMB"/>, and calls out individually
    /// oversized textures (very large runtime footprint or 4K+ source dimensions).
    /// </summary>
    /// <remarks>
    /// Textures are deduplicated across materials/renderers so a shared atlas counts once.
    /// Static, main-thread scene-graph audit; see <see cref="SceneBudgetContext"/>.
    /// </remarks>
    public sealed class SceneTextureBudgetCheck : IDoctorCheck
    {
        public string Id => "scene-texture-budget";
        public string Description => "Scene texture memory vs the platform texture budget";

        private const double SingleOffenderFraction = 0.25;
        private const int LargeDimension = 4096;
        private const int MaxOffenders = 8;
        private const float BytesPerMB = 1024f * 1024f;

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            var issues = new List<DoctorIssue>();
            var sceneCtx = SceneBudgetContext.Acquire();
            float maxMB = sceneCtx.Budget.Settings.MaxTextureMemoryMB;
            double maxBytes = maxMB * BytesPerMB;

            foreach (var snap in sceneCtx.Scenes)
            {
                if (context.IsIgnored(snap.PathOrName))
                    continue;

                context.ReportStatus($"Textures: {snap.Scene.name}");
                await SceneBudgetContext.EditorYieldAsync(cancellationToken);

                var textures = CollectTextures(snap.UniqueMaterials);
                long totalBytes = textures.Sum(t => Profiler.GetRuntimeMemorySizeLong(t));
                double totalMB = totalBytes / BytesPerMB;

                var severity = SceneBudgetContext.GradeOverBudget(totalMB, maxMB);
                if (severity is { } sev)
                {
                    issues.Add(new DoctorIssue(Id, sev,
                        $"Scene '{snap.Scene.name}' references {totalMB:N1} MB of textures, over the " +
                        $"{maxMB:N0} MB budget ({Ratio(totalMB, maxMB)}). " +
                        "Compress, downscale, or enable mip streaming on the largest textures.",
                        snap.PathOrName));
                }

                var offenders = textures
                    .Select(t => (tex: t, bytes: Profiler.GetRuntimeMemorySizeLong(t)))
                    .Where(x => x.bytes > maxBytes * SingleOffenderFraction || IsLargeDimension(x.tex))
                    .OrderByDescending(x => x.bytes)
                    .Take(MaxOffenders);

                foreach (var (tex, bytes) in offenders)
                {
                    bool huge = bytes > maxBytes; // a single texture over the whole budget is egregious
                    issues.Add(new DoctorIssue(Id, huge ? DoctorSeverity.Error : DoctorSeverity.Warning,
                        $"Texture '{tex.name}' is {bytes / BytesPerMB:N1} MB{DimensionNote(tex)}. " +
                        "Reduce max size, enable compression, or turn on mip streaming.",
                        AssetDatabase.GetAssetPath(tex)));
                }
            }

            return issues;
        }

        private static List<Texture> CollectTextures(IEnumerable<Material> materials)
        {
            var seen = new HashSet<Texture>();
            foreach (var mat in materials)
            {
                if (mat == null)
                    continue;
                foreach (var prop in mat.GetTexturePropertyNames())
                {
                    var tex = mat.GetTexture(prop);
                    if (tex != null)
                        seen.Add(tex);
                }
            }
            return seen.ToList();
        }

        private static bool IsLargeDimension(Texture tex) =>
            tex != null && (tex.width >= LargeDimension || tex.height >= LargeDimension);

        private static string DimensionNote(Texture tex) =>
            tex != null ? $" ({tex.width}×{tex.height})" : "";

        private static string Ratio(double actual, double budget) =>
            budget <= 0 ? "n/a" : $"{actual / budget:P0}";
    }
}
