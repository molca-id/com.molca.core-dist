using Molca.Editor;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_tool_schema</c> meta-tool (Sprint 67): returns the full input schema(s) for named
        /// tools. It backs the assistant's tiered/on-demand tool exposure — the model sees a compact catalog
        /// of tool names + summaries and calls this to fetch a tool's parameters before using it, instead of
        /// the whole registry's schemas being sent on every request. Also useful to IDE MCP clients for
        /// schema introspection. Read-only.
        /// </summary>
        private static McpToolDefinition CreateToolSchemaTool() => new McpToolDefinition(
            name: "molca_tool_schema",
            description: "Returns the full input schema + description for one or more Molca tools by name. "
                       + "Call this to learn a tool's parameters before using it — the chat catalog lists "
                       + "every tool's name and summary, and this fetches the details on demand. Pass 'names' "
                       + "(array of tool names, e.g. [\"molca_read_source\",\"molca_edit_source\"]).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"names\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
                "\"description\":\"Tool names to fetch schemas for.\"}}," +
                "\"required\":[\"names\"],\"additionalProperties\":false}",
            execute: ExecuteToolSchema,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteToolSchema(string argumentsJson)
        {
            var names = new JArray();
            try
            {
                var arg = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (arg["names"] is JArray arr) names = arr;
            }
            catch
            {
                return "{\"error\":\"Invalid arguments; expected { names: string[] }.\"}";
            }

            var registry = MolcaEditorSettings.Instance.McpSettings?.BuildRegistry();
            var result = new JObject();
            foreach (var token in names)
            {
                var name = token?.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                if (registry != null && registry.TryGet(name, out var tool))
                {
                    JToken schema;
                    try { schema = JToken.Parse(string.IsNullOrWhiteSpace(tool.InputSchemaJson) ? "{}" : tool.InputSchemaJson); }
                    catch { schema = JValue.CreateString(tool.InputSchemaJson ?? string.Empty); }
                    result[name] = new JObject
                    {
                        ["kind"] = tool.Kind.ToString(),
                        ["description"] = tool.Description,
                        ["input_schema"] = schema
                    };
                }
                else
                {
                    result[name] = new JObject { ["error"] = "Unknown tool." };
                }
            }
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
