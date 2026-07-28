using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>The walking-skeleton <c>molca_status</c> tool (Sprint 14.8).</summary>
        private static McpToolDefinition CreateStatusTool() => new McpToolDefinition(
            name: "molca_status",
            description: "Reports the editor and Molca runtime status: whether the editor is in "
                       + "Play mode, whether RuntimeManager has finished bootstrapping (IsReady), "
                       + "and the installed Molca Core package version.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteStatus,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteStatus(string argumentsJson)
        {
            var result = new JObject
            {
                ["editorOpen"] = true,
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isReady"] = RuntimeManager.IsReady,
                ["coreVersion"] = GetCoreVersion()
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Resolves the installed Molca Core package version, or "unknown" if unavailable.</summary>
        private static string GetCoreVersion()
        {
            // Through the guarded helper: the Package Manager refuses during a domain reload or scene load
            // even on the main thread, and a status tool must degrade to "unknown" rather than throw.
            var version = Molca.Editor.Addons.AddonDistributionConfig.CoreVersion();
            return !string.IsNullOrEmpty(version) ? version : "unknown";
        }
    }
}
