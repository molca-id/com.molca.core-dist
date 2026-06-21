using Molca.Editor;
using Molca.Sequence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only counterpart to the step/auxiliary <i>configuration</i> tools (Sprint 25 follow-up):
    /// reports the current serialized field values of a step and each of its auxiliaries, so an assistant
    /// can inspect what is there before editing instead of treating the setters as write-only. Reads via
    /// <see cref="StepFieldEditingService.GetFields"/> / <see cref="AuxiliaryEditingService.GetAuxiliaryFields"/>.
    /// </summary>
    public partial class CoreMcpToolProvider
    {
        private static McpToolDefinition CreateSequenceGetStepFieldsTool() => new McpToolDefinition(
            name: "molca_sequence_get_step_fields",
            description: "Reads the current serialized field values of a step (by Ref Id) on a "
                       + "SequenceController, plus the fields of each of its auxiliaries. The read "
                       + "counterpart to molca_sequence_set_step_fields / molca_sequence_set_auxiliary_fields "
                       + "— call this to see current values before editing. Values are returned in the same "
                       + "string form the setters accept (composite/array values are shown for inspection).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name; omit if exactly one exists.\"}," +
                "\"stepRefId\":{\"type\":\"string\",\"description\":\"Ref Id of the step to read.\"}}," +
                "\"required\":[\"stepRefId\"],\"additionalProperties\":false}",
            execute: ExecuteGetStepFields,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteGetStepFields(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            var step = ResolveStep(controller, args.Value<string>("stepRefId"), out var stepError);
            if (step == null) return Error(stepError);

            var auxiliaries = new JArray();
            for (int i = 0; i < step.Auxiliaries.Count; i++)
            {
                var aux = step.Auxiliaries[i];
                auxiliaries.Add(new JObject
                {
                    ["index"] = i,
                    ["type"] = aux != null ? aux.GetType().Name : "null",
                    ["fields"] = FieldsToJson(AuxiliaryEditingService.GetAuxiliaryFields(step, i))
                });
            }

            return new JObject
            {
                ["controller"] = controller.name,
                ["stepRefId"] = step.RefId,
                ["type"] = step.GetType().Name,
                ["fields"] = FieldsToJson(StepFieldEditingService.GetFields(step)),
                ["auxiliaries"] = auxiliaries
            }.ToString(Formatting.None);
        }

        private static JArray FieldsToJson(System.Collections.Generic.List<StepFieldEditingService.FieldValue> fields)
        {
            var array = new JArray();
            foreach (var f in fields)
                array.Add(new JObject { ["name"] = f.Name, ["type"] = f.Type, ["value"] = f.Value });
            return array;
        }
    }
}
