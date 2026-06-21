using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Newtonsoft.Json;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Sequence step/auxiliary <i>configuration</i> Action tools (Sprint 20.3/20.4): set step fields,
    /// and add / remove / configure auxiliaries. The configuration counterpart to Sprint 19's
    /// structural tools, routed through <see cref="StepFieldEditingService"/> and
    /// <see cref="AuxiliaryEditingService"/> so every write is one Unity Undo group. All Edit-mode,
    /// allowlist+confirmation gated, revertible via Ctrl+Z.
    /// </summary>
    public partial class CoreMcpToolProvider
    {
        // ── molca_sequence_set_step_fields (Sprint 20.3) ─────────────────────────────────────

        private static McpToolDefinition CreateSequenceSetStepFieldsTool() => new McpToolDefinition(
            name: "molca_sequence_set_step_fields",
            description: "Sets serialized fields on a step (by Ref Id) on a SequenceController. 'fields' is "
                       + "an object of fieldName -> value; values are coerced by field type: string/number/"
                       + "bool, enum name, Object by instance id or asset path, SceneObjectReference by Ref Id, "
                       + "Vector2/3/4/Quaternion/Rect/Bounds and Color as a JSON number array ([x,y,z]; Color "
                       + "also accepts a '#RRGGBB' string), and array/list fields as a JSON array of element "
                       + "values. Composite serializable fields can be set with a nested JSON object whose "
                       + "keys are the sub-field names, and lists of objects with a JSON array of such "
                       + "objects (e.g. a DynamicLocalization 'title' via "
                       + "{\"translations\":[{\"languageCode\":\"en\",\"text\":\"...\"}]}). Unknown or "
                       + "read-only fields are reported back as rejected. One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name; omit if exactly one exists.\"}," +
                "\"stepRefId\":{\"type\":\"string\",\"description\":\"Ref Id of the step to configure.\"}," +
                "\"fields\":{\"type\":\"object\",\"description\":\"fieldName -> value map.\"}}," +
                "\"required\":[\"stepRefId\",\"fields\"],\"additionalProperties\":false}",
            execute: ExecuteSetStepFields,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteSetStepFields(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            var step = ResolveStep(controller, args.Value<string>("stepRefId"), out var stepError);
            if (step == null) return Error(stepError);

            if (!(args["fields"] is JObject fieldsObj) || !fieldsObj.HasValues)
                return Error("'fields' must be a non-empty object of fieldName -> value.");

            var fields = ToFieldNodeMap(fieldsObj);
            var result = StepFieldEditingService.SetFields(step, fields);

            return new JObject
            {
                ["controller"] = controller.name,
                ["stepRefId"] = step.RefId,
                ["applied"] = new JArray(result.Applied),
                ["rejected"] = RejectedToJson(result.Rejected),
                ["writableFields"] = result.Rejected.Count > 0
                    ? new JArray(StepFieldEditingService.GetWritableFields(step))
                    : new JArray()
            }.ToString(Formatting.None);
        }

        // ── molca_sequence_add_auxiliary (Sprint 20.4) ───────────────────────────────────────

        private static McpToolDefinition CreateSequenceAddAuxiliaryTool() => new McpToolDefinition(
            name: "molca_sequence_add_auxiliary",
            description: "Adds a StepAuxiliary of the given 'type' (class name or full name) to a step "
                       + "(by Ref Id). Returns the new auxiliary's index. One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name.\"}," +
                "\"stepRefId\":{\"type\":\"string\",\"description\":\"Ref Id of the step.\"}," +
                "\"type\":{\"type\":\"string\",\"description\":\"Concrete StepAuxiliary type name (or full name).\"}}," +
                "\"required\":[\"stepRefId\",\"type\"],\"additionalProperties\":false}",
            execute: ExecuteAddAuxiliary,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteAddAuxiliary(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            var step = ResolveStep(controller, args.Value<string>("stepRefId"), out var stepError);
            if (step == null) return Error(stepError);

            var auxType = ResolveAuxiliaryType(args.Value<string>("type"), out var typeError);
            if (auxType == null) return Error(typeError);

            int index = AuxiliaryEditingService.AddAuxiliary(step, auxType);
            if (index < 0) return Error("Failed to add auxiliary.");

            return new JObject
            {
                ["controller"] = controller.name,
                ["stepRefId"] = step.RefId,
                ["index"] = index,
                ["type"] = auxType.Name,
                ["auxiliaryCount"] = step.Auxiliaries.Count
            }.ToString(Formatting.None);
        }

        // ── molca_sequence_remove_auxiliary (Sprint 20.4) ────────────────────────────────────

        private static McpToolDefinition CreateSequenceRemoveAuxiliaryTool() => new McpToolDefinition(
            name: "molca_sequence_remove_auxiliary",
            description: "Removes the auxiliary at 'index' from a step (by Ref Id). One undo group; "
                       + "revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name.\"}," +
                "\"stepRefId\":{\"type\":\"string\",\"description\":\"Ref Id of the step.\"}," +
                "\"index\":{\"type\":\"integer\",\"description\":\"Index of the auxiliary to remove.\"}}," +
                "\"required\":[\"stepRefId\",\"index\"],\"additionalProperties\":false}",
            execute: ExecuteRemoveAuxiliary,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteRemoveAuxiliary(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            var step = ResolveStep(controller, args.Value<string>("stepRefId"), out var stepError);
            if (step == null) return Error(stepError);

            int index = args.Value<int?>("index") ?? -1;
            bool removed = AuxiliaryEditingService.RemoveAuxiliary(step, index);
            if (!removed) return Error($"No auxiliary at index {index} (step has {step.Auxiliaries.Count}).");

            return new JObject
            {
                ["controller"] = controller.name,
                ["stepRefId"] = step.RefId,
                ["removedIndex"] = index,
                ["auxiliaryCount"] = step.Auxiliaries.Count
            }.ToString(Formatting.None);
        }

        // ── molca_sequence_set_auxiliary_fields (Sprint 20.4) ────────────────────────────────

        private static McpToolDefinition CreateSequenceSetAuxiliaryFieldsTool() => new McpToolDefinition(
            name: "molca_sequence_set_auxiliary_fields",
            description: "Sets serialized fields on the auxiliary at 'index' on a step (by Ref Id). 'fields' "
                       + "is an object of fieldName -> value, coerced by field type (same coercions as "
                       + "molca_sequence_set_step_fields: scalars, enums, Object refs, Vector/Color/Rect via "
                       + "JSON number array, array/list fields via JSON array, composite fields via a nested "
                       + "JSON object, and lists of objects via a JSON array of objects — e.g. a StepInfo "
                       + "title/description DynamicLocalization via "
                       + "{\"translations\":[{\"languageCode\":\"en\",\"text\":\"...\"}]}). Unknown fields "
                       + "are reported as rejected. One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name.\"}," +
                "\"stepRefId\":{\"type\":\"string\",\"description\":\"Ref Id of the step.\"}," +
                "\"index\":{\"type\":\"integer\",\"description\":\"Index of the auxiliary to configure.\"}," +
                "\"fields\":{\"type\":\"object\",\"description\":\"fieldName -> value map.\"}}," +
                "\"required\":[\"stepRefId\",\"index\",\"fields\"],\"additionalProperties\":false}",
            execute: ExecuteSetAuxiliaryFields,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteSetAuxiliaryFields(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            var step = ResolveStep(controller, args.Value<string>("stepRefId"), out var stepError);
            if (step == null) return Error(stepError);

            int index = args.Value<int?>("index") ?? -1;
            if (index < 0 || index >= step.Auxiliaries.Count)
                return Error($"No auxiliary at index {index} (step has {step.Auxiliaries.Count}).");

            if (!(args["fields"] is JObject fieldsObj) || !fieldsObj.HasValues)
                return Error("'fields' must be a non-empty object of fieldName -> value.");

            var result = AuxiliaryEditingService.SetAuxiliaryFields(step, index, ToFieldNodeMap(fieldsObj));
            return new JObject
            {
                ["controller"] = controller.name,
                ["stepRefId"] = step.RefId,
                ["index"] = index,
                ["applied"] = new JArray(result.Applied),
                ["rejected"] = RejectedToJson(result.Rejected),
                ["writableFields"] = result.Rejected.Count > 0
                    ? new JArray(AuxiliaryEditingService.GetWritableFields(step, index))
                    : new JArray()
            }.ToString(Formatting.None);
        }

        // ── Shared config plumbing ───────────────────────────────────────────────────────────

        /// <summary>Resolves a single step by Ref Id under the controller, with an error message on miss.</summary>
        private static Step ResolveStep(SequenceController controller, string stepRefId, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(stepRefId))
            {
                error = "'stepRefId' is required.";
                return null;
            }
            var step = FindStepByRefId(controller, stepRefId);
            if (step == null)
                error = $"No step with Ref Id '{stepRefId}' under controller '{controller.name}'.";
            return step;
        }

        /// <summary>Resolves a concrete <see cref="StepAuxiliary"/> type by simple or full name.</summary>
        private static Type ResolveAuxiliaryType(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "auxiliary type name is required.";
                return null;
            }
            var concrete = TypeCache.GetTypesDerivedFrom<StepAuxiliary>()
                .Where(t => !t.IsAbstract)
                .ToList();
            var match = concrete.FirstOrDefault(t => t.Name == name || t.FullName == name);
            if (match == null)
                error = $"no concrete StepAuxiliary type named '{name}'. Available: "
                        + string.Join(", ", concrete.Select(t => t.Name).OrderBy(n => n)) + ".";
            return match;
        }

        /// <summary>
        /// Converts a JSON fields object into a fieldName -> <see cref="FieldNode"/> map. Nested JSON
        /// objects become composite nodes and JSON arrays become list nodes, so composite serializable
        /// fields and lists-of-objects (e.g. a <c>DynamicLocalization</c> with per-language
        /// <c>translations</c>) can be authored — not just flat scalars.
        /// </summary>
        private static Dictionary<string, FieldNode> ToFieldNodeMap(JObject fields)
        {
            var map = new Dictionary<string, FieldNode>();
            foreach (var pair in fields)
                map[pair.Key] = ToFieldNode(pair.Value);
            return map;
        }

        /// <summary>Recursively maps a JSON token to a neutral <see cref="FieldNode"/> for coercion.</summary>
        private static FieldNode ToFieldNode(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    var members = new Dictionary<string, FieldNode>();
                    foreach (var pair in obj)
                        members[pair.Key] = ToFieldNode(pair.Value);
                    return FieldNode.FromMembers(members);

                case JArray arr:
                    // An all-scalar array stays a list of scalar nodes; the coercion layer folds it back
                    // to the comma form for numeric-tuple fields (Vector*/Color/Rect) or writes it
                    // element-wise for real array/list fields. Object elements recurse.
                    return FieldNode.FromList(arr.Select(ToFieldNode).ToList());

                default:
                    return FieldNode.FromScalar(ScalarToString(token));
            }
        }

        /// <summary>Renders a JSON scalar to the string form the coercion layer expects (invariant).</summary>
        private static string ScalarToString(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.String: return token.Value<string>();
                case JTokenType.Boolean: return token.Value<bool>() ? "true" : "false";
                case JTokenType.Null: return "null";
                case JTokenType.Integer:
                    return token.Value<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JTokenType.Float:
                    return token.Value<double>().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JTokenType.Array:
                    // Vectors/colors/rects and array/list fields are authored as JSON arrays; flatten to
                    // the comma-separated token form the coercion layer parses (e.g. [1,2,3] -> "1,2,3").
                    return string.Join(",", ((JArray)token).Select(ScalarToString));
                default:
                    // Objects are not coercible into a single field; pass a form that will be rejected
                    // with a clear field-type error rather than silently dropped.
                    return token.ToString(Formatting.None);
            }
        }

        private static JArray RejectedToJson(IReadOnlyList<KeyValuePair<string, string>> rejected)
        {
            var arr = new JArray();
            foreach (var pair in rejected)
                arr.Add(new JObject { ["field"] = pair.Key, ["reason"] = pair.Value });
            return arr;
        }
    }
}
