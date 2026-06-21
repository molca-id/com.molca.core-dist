using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Doctor;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_doctor</c> tool (Sprint 15.1): runs the Molca Doctor checks and returns the
        /// findings (severity / file / line / message), so an assistant can lint generated code against
        /// the framework's conventions. Asynchronous — the Doctor run awaits file scans.
        /// </summary>
        private static McpToolDefinition CreateDoctorTool() => new McpToolDefinition(
            name: "molca_doctor",
            description: "Runs the Molca Doctor convention checks and returns findings (checkId, "
                       + "severity, message, path, line). Optionally restrict to specific check ids or a "
                       + "minimum severity (Info|Warning|Error).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"checkIds\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
                "\"description\":\"Subset of check ids to run; omit to run all.\"}," +
                "\"minSeverity\":{\"type\":\"string\",\"enum\":[\"Info\",\"Warning\",\"Error\"]," +
                "\"description\":\"Only return findings at or above this severity (default Info).\"}}," +
                "\"additionalProperties\":false}",
            executeAsync: ExecuteDoctorAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteDoctorAsync(string argumentsJson)
        {
            HashSet<string> enabledIds = null;
            var minSeverity = DoctorSeverity.Info;
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (args["checkIds"] is JArray ids && ids.Count > 0)
                    enabledIds = new HashSet<string>(ids.Select(t => t.ToString()));
                if (args["minSeverity"] != null &&
                    System.Enum.TryParse<DoctorSeverity>(args.Value<string>("minSeverity"), out var parsed))
                    minSeverity = parsed;
            }
            catch { /* defaults: all checks, Info */ }

            var issues = await MolcaDoctor.RunAllAsync(enabledIds);

            var arr = new JArray();
            foreach (var issue in issues.Where(i => i.Severity >= minSeverity)
                                        .OrderByDescending(i => i.Severity))
            {
                arr.Add(new JObject
                {
                    ["checkId"] = issue.CheckId,
                    ["severity"] = issue.Severity.ToString(),
                    ["message"] = issue.Message,
                    ["path"] = issue.Path,
                    ["line"] = issue.Line
                });
            }

            var availableChecks = new JArray();
            foreach (var check in MolcaDoctor.Checks)
                availableChecks.Add(new JObject { ["id"] = check.Id, ["description"] = check.Description });

            var result = new JObject
            {
                ["findingCount"] = arr.Count,
                ["errorCount"] = issues.Count(i => i.Severity == DoctorSeverity.Error),
                ["warningCount"] = issues.Count(i => i.Severity == DoctorSeverity.Warning),
                ["findings"] = arr,
                ["availableChecks"] = availableChecks
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
