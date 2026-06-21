using System.Linq;
using Molca.Editor.Validation;
using Molca.Sequence;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_validate_sequence</c> tool (Sprint 15.2): runs the pluggable
        /// <see cref="SequenceValidatorRegistry"/> over a controller in the loaded scene(s) and returns
        /// the merged findings. The registry includes the legacy <see cref="SequenceValidator"/> checks
        /// (via an adapter) plus structural-flow and reference-resolution validators (Sprint 37); the
        /// output shape is unchanged except for additive <c>validator</c>/<c>category</c>/<c>valid</c>/
        /// <c>warningCount</c> keys.
        /// </summary>
        private static McpToolDefinition CreateValidateSequenceTool() => new McpToolDefinition(
            name: "molca_validate_sequence",
            description: "Validates a SequenceController in the loaded scene(s) — broken auxiliaries, "
                       + "empty/duplicate Ref Ids, inactive parents with active children, control-flow "
                       + "topology (empty/degenerate parallel & branch containers, detached steps), and "
                       + "unresolved/ambiguous outbound SceneObjectReferences. Also returns the "
                       + "full step tree ('steps': each step's refId, name, type, parentRefId, and auxiliary "
                       + "types by index) — use it to discover step Ref Ids before editing. Identify the "
                       + "controller by its Ref Id or GameObject name; if omitted and exactly one exists, "
                       + "that one is used.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteValidateSequence,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteValidateSequence(string argumentsJson)
        {
            string wanted = null;
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                wanted = args.Value<string>("controller");
            }
            catch { /* treat as unspecified */ }

            var controllers = Object.FindObjectsByType<SequenceController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (controllers.Length == 0)
                return Error("No SequenceController found in the loaded scene(s).");

            SequenceController target;
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                target = controllers.FirstOrDefault(c =>
                    c.RefId == wanted || c.name == wanted);
                if (target == null)
                    return Error($"No SequenceController matched '{wanted}'. Available: "
                                 + string.Join(", ", controllers.Select(c => $"{c.name} (refId:{c.RefId})")) + ".");
            }
            else if (controllers.Length == 1)
            {
                target = controllers[0];
            }
            else
            {
                return Error("Multiple SequenceControllers found; specify 'controller'. Available: "
                             + string.Join(", ", controllers.Select(c => $"{c.name} (refId:{c.RefId})")) + ".");
            }

            // Run the pluggable registry (legacy data-integrity adapter + structural-flow + reference
            // resolution, plus any fork validators) rather than the legacy validator alone (Sprint 37).
            var findings = SequenceValidatorRegistry.Run(target);
            // Attach remediation suggestions/hints on demand (Sprint 41) — validators stay cheap.
            SequenceFindingEnricher.Enrich(
                findings, new SequenceValidationContext(target, target.GetComponentsInChildren<Step>(true)));
            var arr = new JArray();
            foreach (var f in findings)
            {
                arr.Add(new JObject
                {
                    // "type" stays for backward compatibility (legacy findings keep their type names);
                    // "category"/"validator" are additive. New validators populate the same shape.
                    ["type"] = f.Category,
                    ["category"] = f.Category,
                    ["validator"] = f.ValidatorId,
                    ["severity"] = f.Severity.ToString(),
                    ["message"] = f.Message,
                    ["stepRefId"] = f.StepRefId,
                    ["stepName"] = f.StepName,
                    ["auxiliaryIndex"] = f.AuxiliaryIndex,
                    ["hasFix"] = f.HasFix,
                    ["fixHint"] = f.FixHint,
                    ["suggestions"] = new JArray(f.Suggestions)
                });
            }

            var errorCount = findings.Count(f => f.Severity == SequenceValidationSeverity.Error);
            var warningCount = findings.Count(f => f.Severity == SequenceValidationSeverity.Warning);

            // SequenceController.Steps is populated only at runtime (InitializeSequence in Start).
            // In Edit mode it is null, so enumerate the child Step components directly — matching
            // how SequenceValidator and the edit tools discover steps — instead of reporting 0.
            var stepCount = target.GetComponentsInChildren<Step>(true).Length;

            var result = new JObject
            {
                ["controller"] = target.name,
                ["controllerRefId"] = target.RefId,
                ["stepCount"] = stepCount,
                ["findingCount"] = findings.Count,
                ["errorCount"] = errorCount,
                ["warningCount"] = warningCount,
                ["valid"] = errorCount == 0,
                ["findings"] = arr,
                ["steps"] = DescribeStepTree(target)
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Enumerates every <see cref="Step"/> under the controller as a flat list carrying each step's
        /// Ref Id, name, type, parent step Ref Id (null at the controller root), and the type of every
        /// auxiliary keyed by its index. This is what lets a caller discover step Ref Ids and existing
        /// auxiliary types without mutating the scene — the gap that previously forced trial-and-error.
        /// </summary>
        private static JArray DescribeStepTree(SequenceController controller)
        {
            var arr = new JArray();
            foreach (var step in controller.GetComponentsInChildren<Step>(true))
            {
                // Nearest ancestor Step, if any; the controller itself is the implicit root (null parent).
                var parentTransform = step.transform.parent;
                var parent = parentTransform != null
                    ? parentTransform.GetComponentInParent<Step>(true)
                    : null;

                var auxArr = new JArray();
                for (int i = 0; i < step.Auxiliaries.Count; i++)
                {
                    var aux = step.Auxiliaries[i];
                    auxArr.Add(new JObject
                    {
                        ["index"] = i,
                        ["type"] = aux != null ? aux.GetType().Name : null
                    });
                }

                arr.Add(new JObject
                {
                    ["refId"] = step.RefId,
                    ["name"] = step.name,
                    ["type"] = step.GetType().Name,
                    ["parentRefId"] = parent != null ? parent.RefId : null,
                    ["auxiliaries"] = auxArr
                });
            }
            return arr;
        }

        // Tools return their own JSON; a tool-level error is a valid result payload (the bridge only
        // wraps thrown exceptions). This keeps "nothing matched" distinct from a transport failure.
        private static string Error(string message)
            => new JObject { ["error"] = message }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
