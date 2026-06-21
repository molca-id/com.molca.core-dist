using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// Fully-qualified name of the interactive "ask the user" tool (Sprint 25.5). Referenced by the
        /// in-editor assistant bridge, which special-cases this tool to pause the turn and surface the
        /// question as clickable choices instead of running the fallback delegate.
        /// </summary>
        public const string AskUserToolName = "molca_ask_user";

        /// <summary>
        /// The <c>molca_ask_user</c> tool (Sprint 25.5). A read-only tool the model calls mid-turn when a
        /// decision materially changes the work (e.g. "overwrite the existing step or create a new one?").
        /// </summary>
        /// <remarks>
        /// It is not an <see cref="McpToolKind.Action"/>, so it never goes through the allowlist or the
        /// confirmation gate. In the in-editor assistant, <c>AssistantToolBridge</c> intercepts the call,
        /// pauses the round loop, and returns the user's choice as the tool result. Outside that surface
        /// (e.g. an IDE proxy with no chat UI) there is nobody to answer, so the fallback delegate returns
        /// an error rather than guessing.
        /// </remarks>
        private static McpToolDefinition CreateAskUserTool() => new McpToolDefinition(
            name: AskUserToolName,
            description: "Ask the user a question and wait for their answer before continuing. Use this "
                       + "when a decision materially changes the work and you cannot safely choose for "
                       + "them (e.g. overwrite vs. create new, which of several targets to edit). Provide "
                       + "a concise 'question'; optionally provide 'options' (short choice labels) to "
                       + "render as buttons. The tool result is the user's answer.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{"
                + "\"question\":{\"type\":\"string\",\"description\":\"The question to ask the user.\"},"
                + "\"options\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},"
                + "\"description\":\"Optional short choice labels rendered as buttons.\"}"
                + "},\"required\":[\"question\"],\"additionalProperties\":false}",
            execute: ExecuteAskUserFallback,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        /// <summary>
        /// Fallback for surfaces with no interactive front-end. The in-editor assistant intercepts this
        /// tool before the delegate runs; reaching here means no UI can answer.
        /// </summary>
        private static string ExecuteAskUserFallback(string argumentsJson)
        {
            var result = new JObject
            {
                ["error"] = "molca_ask_user is interactive and requires the in-editor Molca Assistant; "
                          + "no UI is available to answer in this context."
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
