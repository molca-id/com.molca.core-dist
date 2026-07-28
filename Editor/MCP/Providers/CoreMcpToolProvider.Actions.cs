using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        // ── molca_trigger_build (Sprint 17.4) ────────────────────────────────────────────────

        /// <summary>
        /// The <c>molca_trigger_build</c> action tool (Sprint 17.4): kicks a build profile through
        /// <c>BuildManager.BuildAsync</c> (which runs the pre-build Doctor gate). Async; Action — runs
        /// only when allowlisted and confirmed.
        /// </summary>
        private static McpToolDefinition CreateTriggerBuildTool() => new McpToolDefinition(
            name: "molca_trigger_build",
            description: "Triggers a build for a named build profile via BuildManager (with the pre-build "
                       + "Doctor gate). Returns the build result, output path, error count and size.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"profile\":{\"type\":\"string\",\"description\":\"Build profile name.\"}}," +
                "\"required\":[\"profile\"],\"additionalProperties\":false}",
            executeAsync: ExecuteTriggerBuildAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible); // a build cannot be undone

        private static async Awaitable<string> ExecuteTriggerBuildAsync(string argumentsJson)
        {
            var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var profile = args.Value<string>("profile");
            if (string.IsNullOrWhiteSpace(profile))
                return Error("'profile' is required.");

            var report = await BuildManager.BuildAsync(profile, runPreBuildChecks: true);
            if (report == null)
                return Error($"Build did not run (unknown profile '{profile}' or aborted by the pre-build gate).");

            var summary = report.summary;
            return new JObject
            {
                ["profile"] = profile,
                ["result"] = summary.result.ToString(),
                ["outputPath"] = summary.outputPath,
                ["totalErrors"] = (long)summary.totalErrors,
                ["totalWarnings"] = (long)summary.totalWarnings,
                ["totalSizeBytes"] = (long)summary.totalSize
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── molca_undo_last_action (Sprint 17) ───────────────────────────────────────────────

        /// <summary>
        /// The <c>molca_undo_last_action</c> action tool: reverts the most recent file-snapshot action
        /// when one exists, otherwise falls back to Unity's editor undo stack for UnityUndo actions.
        /// Itself gated as an Action (allowlist + confirmation); irreversible (redo is owned by the editor).
        /// </summary>
        private static McpToolDefinition CreateUndoLastTool() => new McpToolDefinition(
            name: "molca_undo_last_action",
            description: "Reverts the most recent revertible MCP action. File-snapshot actions restore "
                       + "their backup; UnityUndo actions use the editor undo stack. Builds are not revertible.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: _ =>
            {
                var message = McpUndoStack.HasEntries
                    ? McpUndoStack.UndoLast()
                    : UndoLastUnityAction();
                return new JObject
                {
                    ["reverted"] = message.StartsWith("Reverted"),
                    ["message"] = message,
                    ["remaining"] = McpUndoStack.Entries.Count
                }.ToString(Newtonsoft.Json.Formatting.None);
            },
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string UndoLastUnityAction()
        {
            try
            {
                Undo.PerformUndo();
                return "Reverted: latest Unity undo group";
            }
            catch (System.Exception ex)
            {
                return $"Revert failed: {ex.Message}";
            }
        }
    }
}
