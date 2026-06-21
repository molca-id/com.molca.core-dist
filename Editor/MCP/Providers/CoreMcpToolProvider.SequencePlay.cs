using Molca.Sequence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Play-mode sequence <i>operation</i> Action tools (Sprint 20.7): start a sequence and complete a
    /// step live. These mutate runtime state (not serialized data), so they are <see cref="McpToolMode.Play"/>
    /// and honestly <see cref="McpToolReversibility.Irreversible"/>. Pairs with the read-only
    /// <c>molca_subsystems</c> Play introspection; mirrors the graph editor's play-mode node actions.
    /// </summary>
    public partial class CoreMcpToolProvider
    {
        // ── molca_sequence_start (Sprint 20.7) ───────────────────────────────────────────────

        private static McpToolDefinition CreateSequenceStartTool() => new McpToolDefinition(
            name: "molca_sequence_start",
            description: "Starts a SequenceController (play mode only) via StartSequence(). Identify the "
                       + "controller by Ref Id or GameObject name; if omitted and exactly one exists, that "
                       + "one is used.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteSequenceStart,
            mode: McpToolMode.Play,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteSequenceStart(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            controller.StartSequence();
            return new JObject
            {
                ["controller"] = controller.name,
                ["started"] = true
            }.ToString(Formatting.None);
        }

        // ── molca_sequence_complete_step (Sprint 20.7) ───────────────────────────────────────

        private static McpToolDefinition CreateSequenceCompleteStepTool() => new McpToolDefinition(
            name: "molca_sequence_complete_step",
            description: "Completes a step (by Ref Id) in play mode. By default respects the step's "
                       + "CanComplete() gate and reports if it is blocked; pass 'force':true to bypass the "
                       + "gate (ForceComplete).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name.\"}," +
                "\"stepRefId\":{\"type\":\"string\",\"description\":\"Ref Id of the step to complete.\"}," +
                "\"force\":{\"type\":\"boolean\",\"description\":\"Bypass the CanComplete() gate (ForceComplete).\"}}," +
                "\"required\":[\"stepRefId\"],\"additionalProperties\":false}",
            execute: ExecuteCompleteStep,
            mode: McpToolMode.Play,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteCompleteStep(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            var step = ResolveStep(controller, args.Value<string>("stepRefId"), out var stepError);
            if (step == null) return Error(stepError);

            bool force = args.Value<bool?>("force") ?? false;
            bool canComplete = step.CanCompleteNow();

            if (!force && !canComplete)
            {
                return new JObject
                {
                    ["controller"] = controller.name,
                    ["stepRefId"] = step.RefId,
                    ["completed"] = false,
                    ["blocked"] = true,
                    ["message"] = "Step's CanComplete() gate returned false. Pass 'force':true to bypass it."
                }.ToString(Formatting.None);
            }

            if (force) step.ForceComplete();
            else step.Complete();

            return new JObject
            {
                ["controller"] = controller.name,
                ["stepRefId"] = step.RefId,
                ["completed"] = step.IsInternallyCompleted,
                ["forced"] = force
            }.ToString(Formatting.None);
        }
    }
}
