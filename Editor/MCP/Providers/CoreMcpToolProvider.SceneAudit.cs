using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Doctor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Profiling;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_scene_audit</c> tool (Sprint 50): runs only the scene-performance Doctor
        /// checks (ids prefixed <c>scene-</c>) against the open scene(s) and returns the ranked
        /// findings plus a per-scene budget-vs-actual summary, so an assistant gets both the numbers
        /// and the actionable problems in one call. Read-only; discovered via convention.
        /// </summary>
        private static McpToolDefinition CreateSceneAuditTool() => new McpToolDefinition(
            name: "molca_scene_audit",
            description: "Audits the open Unity scene(s) for performance problems against the project's "
                       + "platform BudgetSettings: triangle/texture/material/mesh/light budgets, missing "
                       + "GPU-instancing/LODs, deep hierarchies, and convention hints. Returns findings "
                       + "(checkId, severity, message, path) ranked by severity plus a per-scene "
                       + "budget-vs-actual summary. Optional 'minSeverity' (Info|Warning|Error).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"minSeverity\":{\"type\":\"string\",\"enum\":[\"Info\",\"Warning\",\"Error\"]," +
                "\"description\":\"Only return findings at or above this severity (default Info).\"}}," +
                "\"additionalProperties\":false}",
            executeAsync: ExecuteSceneAuditAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        /// <summary>The scene-performance check ids — every registered check whose id starts with "scene-".</summary>
        private static HashSet<string> SceneCheckIds() =>
            new HashSet<string>(MolcaDoctor.Checks
                .Select(c => c.Id)
                .Where(id => id.StartsWith("scene-", System.StringComparison.Ordinal)));

        private static async Awaitable<string> ExecuteSceneAuditAsync(string argumentsJson)
        {
            var minSeverity = DoctorSeverity.Info;
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (args["minSeverity"] != null &&
                    System.Enum.TryParse<DoctorSeverity>(args.Value<string>("minSeverity"), out var parsed))
                    minSeverity = parsed;
            }
            catch { /* default: Info */ }

            // Budget-vs-actual summary from a single scene walk (also resolves the platform budget).
            var summary = BuildSceneSummary(out var loadedScenes);
            if (loadedScenes == 0)
            {
                return new JObject
                {
                    ["loadedScenes"] = 0,
                    ["message"] = "No scenes are loaded. Open a scene to audit it.",
                    ["findings"] = new JArray()
                }.ToString(Formatting.None);
            }

            var issues = await MolcaDoctor.RunAllAsync(SceneCheckIds());

            var findings = new JArray();
            foreach (var issue in issues.Where(i => i.Severity >= minSeverity)
                                        .OrderByDescending(i => i.Severity))
            {
                findings.Add(new JObject
                {
                    ["checkId"] = issue.CheckId,
                    ["severity"] = issue.Severity.ToString(),
                    ["message"] = issue.Message,
                    ["path"] = issue.Path
                });
            }

            return new JObject
            {
                ["loadedScenes"] = loadedScenes,
                ["findingCount"] = findings.Count,
                ["errorCount"] = issues.Count(i => i.Severity == DoctorSeverity.Error),
                ["warningCount"] = issues.Count(i => i.Severity == DoctorSeverity.Warning),
                ["budgetSummary"] = summary,
                ["findings"] = findings
            }.ToString(Formatting.None);
        }

        private static JArray BuildSceneSummary(out int loadedScenes)
        {
            var ctx = SceneBudgetContext.Build();
            var settings = ctx.Budget.Settings;
            var scenes = new JArray();

            foreach (var snap in ctx.Scenes)
            {
                scenes.Add(new JObject
                {
                    ["scene"] = snap.Scene.name,
                    ["path"] = snap.PathOrName,
                    ["budgetSource"] = ctx.Budget.Source,
                    ["activeGameObjects"] = new JObject { ["actual"] = snap.ActiveGameObjectCount, ["budget"] = settings.MaxGameObjects },
                    ["triangles"] = new JObject { ["actual"] = snap.TotalTriangles, ["budget"] = settings.MaxTriangles },
                    ["uniqueMaterials"] = new JObject { ["actual"] = snap.UniqueMaterials.Count, ["budget"] = settings.MaxMaterialInstances },
                    ["uniqueMeshes"] = new JObject { ["actual"] = snap.UniqueMeshes.Count, ["budget"] = settings.MaxMeshInstances },
                    ["textureMemoryMB"] = new JObject { ["actual"] = System.Math.Round(TextureMemoryMB(snap.UniqueMaterials), 1), ["budget"] = settings.MaxTextureMemoryMB },
                    ["realtimeLights"] = snap.Lights.Count(l => l.lightmapBakeType == UnityEngine.LightmapBakeType.Realtime)
                });
            }

            loadedScenes = ctx.Scenes.Count;
            return scenes;
        }

        private static double TextureMemoryMB(IEnumerable<Material> materials)
        {
            var seen = new HashSet<Texture>();
            foreach (var mat in materials)
            {
                if (mat == null) continue;
                foreach (var prop in mat.GetTexturePropertyNames())
                {
                    var tex = mat.GetTexture(prop);
                    if (tex != null) seen.Add(tex);
                }
            }
            long bytes = seen.Sum(Profiler.GetRuntimeMemorySizeLong);
            return bytes / (1024.0 * 1024.0);
        }
    }
}
