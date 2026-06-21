using System.Collections.Generic;
using System.Linq;
using Molca.Editor;
using Molca.Editor.Validation;
using Molca.Sequence;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_sequence_author</c> tool (Sprint 42): applies a declarative whole-graph plan to a
        /// SequenceController transactionally, then converges it (validate → safe-fix → re-validate) and
        /// returns created/updated Ref Ids with the residual findings. The Core half of the Spec→Sequence
        /// generator — the agent decides the plan; this turns plan → validated graph.
        /// </summary>
        private static McpToolDefinition CreateAuthorSequenceTool() => new McpToolDefinition(
            name: "molca_sequence_author",
            description: "Authors a whole sequence graph from a declarative plan on a SequenceController, "
                       + "transactionally (all-or-nothing, one undo), then validates and applies safe fixes "
                       + "and returns before/after + residual findings (with Ref-Id suggestions). The plan is "
                       + "in Core vocabulary — typed steps with refId, parentRefId, fields, and auxiliaries; "
                       + "mode 'create' (fail on existing refId) or 'merge' (update existing, add missing). "
                       + "Discover step/auxiliary types via molca_sequence_list_types; loop on residual "
                       + "(e.g. rebind unresolved references) until valid. Revert with molca_undo_last_action.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name.\"}," +
                "\"mode\":{\"type\":\"string\",\"enum\":[\"create\",\"merge\"],\"description\":\"create (default) or merge.\"}," +
                "\"remediate\":{\"type\":\"boolean\",\"description\":\"Run validate→fix→re-validate after apply (default true).\"}," +
                "\"applySafeFixes\":{\"type\":\"boolean\",\"description\":\"Apply safe fixes during convergence (default true).\"}," +
                "\"steps\":{\"type\":\"array\",\"description\":\"The steps to author.\",\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"refId\":{\"type\":\"string\"}," +
                "\"type\":{\"type\":\"string\",\"description\":\"Step type name.\"}," +
                "\"parentRefId\":{\"type\":\"string\"}," +
                "\"name\":{\"type\":\"string\"}," +
                "\"fields\":{\"type\":\"object\"}," +
                "\"auxiliaries\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"type\":{\"type\":\"string\"},\"fields\":{\"type\":\"object\"}}}}}}}}," +
                "\"required\":[\"steps\"],\"additionalProperties\":false}",
            execute: ExecuteAuthorSequence,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteAuthorSequence(string argumentsJson)
        {
            var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var target = ResolveController(args.Value<string>("controller"), out var controllerError);
            if (target == null) return Error(controllerError);

            if (args["steps"] is not JArray stepsArr || stepsArr.Count == 0)
                return Error("'steps' is required (a non-empty array).");

            var plan = new SequenceAuthoringPlan
            {
                Mode = string.Equals(args.Value<string>("mode"), "merge", System.StringComparison.OrdinalIgnoreCase)
                    ? AuthoringMode.Merge : AuthoringMode.Create,
                Remediate = args.Value<bool?>("remediate") ?? true,
                ApplySafeFixes = args.Value<bool?>("applySafeFixes") ?? true,
            };

            foreach (var st in stepsArr.OfType<JObject>())
            {
                var planned = new PlannedStep
                {
                    RefId = st.Value<string>("refId"),
                    Type = st.Value<string>("type"),
                    ParentRefId = st.Value<string>("parentRefId"),
                    Name = st.Value<string>("name"),
                    Fields = ParseFieldMap(st["fields"] as JObject),
                };
                if (st["auxiliaries"] is JArray auxArr)
                    foreach (var a in auxArr.OfType<JObject>())
                        planned.Auxiliaries.Add(new PlannedAuxiliary
                        {
                            Type = a.Value<string>("type"),
                            Fields = ParseFieldMap(a["fields"] as JObject),
                        });
                plan.Steps.Add(planned);
            }

            var result = SequenceAuthoringService.Author(target, plan);

            if (result.PlanIssues.Count > 0)
                return new JObject
                {
                    ["applied"] = false,
                    ["planIssues"] = new JArray(result.PlanIssues),
                    ["message"] = "Plan rejected; nothing was applied. Fix the issues and retry."
                }.ToString(Newtonsoft.Json.Formatting.None);

            if (!result.Applied)
                return Error(result.Error ?? "Authoring failed (rolled back).");

            var reverts = new JArray();
            foreach (var mechanism in result.RevertMechanisms)
                AddRevert(reverts, mechanism.ToString(),
                    mechanism == FixReversibility.UnityUndo ? "editor-undo" : null);

            return new JObject
            {
                ["applied"] = true,
                ["controller"] = target.name,
                ["controllerRefId"] = target.RefId,
                ["created"] = new JArray(result.CreatedRefIds),
                ["updated"] = new JArray(result.UpdatedRefIds),
                ["before"] = new JObject { ["errorCount"] = result.BeforeErrors, ["warningCount"] = result.BeforeWarnings },
                ["after"] = new JObject
                {
                    ["errorCount"] = result.AfterErrors,
                    ["warningCount"] = result.AfterWarnings,
                    ["valid"] = result.Valid
                },
                ["requiresSceneReload"] = result.RequiresSceneReload,
                ["reverts"] = reverts,
                ["residual"] = new JArray(result.Residual.Select(SerializeFinding)),
                ["message"] = result.Valid
                    ? "Authored and validated clean."
                    : "Authored; residual findings remain — loop on them (e.g. rebind references) until valid."
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Flattens a JSON object of scalar field values to a name→string map for the editing services.</summary>
        private static Dictionary<string, string> ParseFieldMap(JObject obj)
        {
            var map = new Dictionary<string, string>();
            if (obj == null) return map;
            foreach (var p in obj.Properties())
                map[p.Name] = p.Value.Type == JTokenType.Null ? null : p.Value.ToString();
            return map;
        }
    }
}
