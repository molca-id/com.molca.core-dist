using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// Fully-qualified name of the structured plan-proposal tool (Sprint 52). The in-editor assistant
        /// bridge special-cases this tool to surface a reviewable, editable plan turn and pause for the
        /// user's Approve / Edit / Cancel decision, mirroring <see cref="AskUserToolName"/>.
        /// </summary>
        public const string ProposePlanToolName = "molca_propose_plan";

        /// <summary>
        /// The <c>molca_propose_plan</c> tool (Sprint 52). In Plan mode the model calls this with an ordered
        /// list of steps before running any actions; the assistant renders the plan as a first-class,
        /// reviewable checklist and returns the user's disposition (approved / edited / cancelled) as the
        /// tool result so the round loop continues.
        /// </summary>
        /// <remarks>
        /// It is <see cref="McpToolKind.ReadOnly"/>, so it never goes through the allowlist or the action
        /// confirmation gate. In the in-editor assistant, <c>AssistantToolBridge</c> intercepts the call,
        /// surfaces the <c>Plan</c> turn, and pauses the round loop. Outside that surface (e.g. an IDE proxy
        /// with no chat UI) there is nobody to approve, so the fallback delegate returns an error rather than
        /// silently proceeding.
        /// </remarks>
        private static McpToolDefinition CreateProposePlanTool() => new McpToolDefinition(
            name: ProposePlanToolName,
            description: "Propose an ordered, structured plan before executing a multi-step task in Plan "
                       + "mode. Provide 'steps' as an array of objects, each with a short stable 'id' and a "
                       + "one-line 'summary' of what the step does. The user reviews, optionally edits, and "
                       + "approves the plan; the tool result reports their disposition. When approved, run "
                       + "the steps in order; do not call action tools before proposing the plan.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{"
                + "\"steps\":{\"type\":\"array\",\"description\":\"Ordered plan steps.\",\"items\":{"
                + "\"type\":\"object\",\"properties\":{"
                + "\"id\":{\"type\":\"string\",\"description\":\"Short stable step id.\"},"
                + "\"summary\":{\"type\":\"string\",\"description\":\"One-line description of the step.\"}"
                + "},\"required\":[\"summary\"],\"additionalProperties\":false}}"
                + "},\"required\":[\"steps\"],\"additionalProperties\":false}",
            execute: ExecuteProposePlanFallback,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        /// <summary>
        /// Fallback for surfaces with no interactive front-end. The in-editor assistant intercepts this tool
        /// before the delegate runs; reaching here means no UI can review or approve the plan.
        /// </summary>
        private static string ExecuteProposePlanFallback(string argumentsJson)
        {
            var result = new JObject
            {
                ["error"] = "molca_propose_plan is interactive and requires the in-editor Molca Assistant; "
                          + "no UI is available to review or approve a plan in this context."
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
