using Molca.Editor;
using Molca.Editor.Mcp.Assistant;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_list_tools</c> meta-tool (Sprint 67.3): lists the tools in one family as
        /// <c>name — summary</c> lines. The chat catalog shows families + tool names only (no summaries) to
        /// stay small; this expands a family's detail on demand, after which the model fetches a specific
        /// tool's parameters with <c>molca_tool_schema</c>. Read-only.
        /// </summary>
        private static McpToolDefinition CreateListToolsTool() => new McpToolDefinition(
            name: "molca_list_tools",
            description: "Lists the Molca tools in a family (their names + one-line summaries). The chat tool "
                       + "catalog groups tools by family but omits per-tool summaries to stay compact; call "
                       + "this with 'family' (e.g. \"content\", \"localization\", \"unity/addressable\") to see "
                       + "what a family's tools do, then molca_tool_schema for a specific tool's parameters.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"family\":{\"type\":\"string\",\"description\":\"Family key as shown in the catalog, e.g. 'content'.\"}}," +
                "\"required\":[\"family\"],\"additionalProperties\":false}",
            execute: ExecuteListTools,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteListTools(string argumentsJson)
        {
            string family;
            try
            {
                var arg = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                family = arg.Value<string>("family");
            }
            catch
            {
                return "Invalid arguments; expected { family: string }.";
            }

            if (string.IsNullOrWhiteSpace(family)) return "Provide a 'family' (see the tool catalog).";

            var settings = MolcaEditorSettings.Instance.McpSettings;
            var registry = settings?.BuildRegistry();
            return AssistantToolBridge.BuildFamilyListing(registry, family.Trim(), settings != null ? settings.IsActionAllowed : null);
        }
    }
}
