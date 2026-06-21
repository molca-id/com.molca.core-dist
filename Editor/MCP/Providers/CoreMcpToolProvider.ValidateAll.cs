using System.Linq;
using Molca.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_validate_all_sequences</c> tool (Sprint 43): runs the validation registry across
        /// every SequenceController and returns a per-controller summary + totals. Sweeps the open scene(s)
        /// by default (side-effect-free); <c>scenes</c>/<c>allBuildScenes</c> explicitly open and close
        /// other scenes to include them.
        /// </summary>
        private static McpToolDefinition CreateValidateAllSequencesTool() => new McpToolDefinition(
            name: "molca_validate_all_sequences",
            description: "Validates every SequenceController across scenes and returns a roll-up (per "
                       + "controller: scene, name, refId, error/warning/step counts, valid) plus totals. "
                       + "Defaults to the open scene(s) (no side effects). Pass allBuildScenes:true or "
                       + "scenes:[paths] to also open/validate/close other scenes. includeFindings:true adds "
                       + "per-controller detail; otherwise use molca_validate_sequence for one controller.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"includeFindings\":{\"type\":\"boolean\",\"description\":\"Include each controller's findings.\"}," +
                "\"allBuildScenes\":{\"type\":\"boolean\",\"description\":\"Sweep all Build Settings scenes (opens/closes them).\"}," +
                "\"scenes\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Specific scene asset paths to sweep.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteValidateAllSequences,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteValidateAllSequences(string argumentsJson)
        {
            var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            bool includeFindings = args.Value<bool?>("includeFindings") ?? false;

            SequenceSweepResult sweep;
            if (args.Value<bool?>("allBuildScenes") == true)
            {
                var paths = EditorBuildSettings.scenes.Select(s => s.path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                sweep = SequenceValidationSweep.SweepScenes(paths, includeFindings);
            }
            else if (args["scenes"] is JArray scenesArr && scenesArr.Count > 0)
            {
                sweep = SequenceValidationSweep.SweepScenes(scenesArr.Values<string>().ToList(), includeFindings);
            }
            else
            {
                sweep = SequenceValidationSweep.SweepLoadedScenes(includeFindings);
            }

            var controllers = new JArray();
            foreach (var c in sweep.Controllers)
            {
                var obj = new JObject
                {
                    ["scene"] = c.ScenePath,
                    ["controller"] = c.ControllerName,
                    ["controllerRefId"] = c.ControllerRefId,
                    ["stepCount"] = c.StepCount,
                    ["errorCount"] = c.ErrorCount,
                    ["warningCount"] = c.WarningCount,
                    ["valid"] = c.Valid
                };
                if (includeFindings)
                    obj["findings"] = new JArray(c.Findings.Select(SerializeFinding));
                controllers.Add(obj);
            }

            return new JObject
            {
                ["totalControllers"] = sweep.TotalControllers,
                ["invalidControllers"] = sweep.InvalidControllers,
                ["totalErrors"] = sweep.TotalErrors,
                ["totalWarnings"] = sweep.TotalWarnings,
                ["scenes"] = new JArray(sweep.ScenesSwept),
                ["controllers"] = controllers
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
