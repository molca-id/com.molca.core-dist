using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor;
using Molca.Sequence;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Sequence-authoring <see cref="McpToolKind.Action"/> tools (Sprint 19): create, remove,
    /// duplicate, change-type, and reparent steps on a <see cref="SequenceController"/>. Every tool
    /// routes through <see cref="StepEditingService"/> — the single Undo-grouped CRUD path shared by
    /// the visualizer and graph editor — so each invocation collapses to one Unity Undo group and
    /// every created step receives a fresh Ref Id. All are Edit-mode, allowlist+confirmation gated
    /// (Sprint 17), and revertible through plain Unity Undo.
    /// </summary>
    /// <remarks>
    /// Applying validation fixes (the would-be <c>molca_sequence_fix</c>, plan item 19.6) is already
    /// served by <c>molca_run_doctor_fix</c>, which applies the only auto-fixable
    /// <see cref="SequenceValidator"/> finding (broken auxiliary); no duplicate tool is added here.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── molca_sequence_add_steps (Sprint 19.1) ───────────────────────────────────────────

        /// <summary>
        /// The <c>molca_sequence_add_steps</c> action tool: creates one or more steps under a named
        /// controller via <see cref="StepEditingService.AddSteps"/> as one undo group. Earlier entries
        /// can parent later ones only by referencing an already-existing step's Ref Id.
        /// </summary>
        private static McpToolDefinition CreateSequenceAddStepsTool() => new McpToolDefinition(
            name: "molca_sequence_add_steps",
            description: "Creates one or more steps on a SequenceController in the loaded scene(s). Each "
                       + "entry has a step 'type' (class name, e.g. 'Step' or a Step subclass), an optional "
                       + "'parent' (Ref Id of an existing step; omit for the controller root), and an "
                       + "optional 'name'. Created steps receive fresh Ref Ids. One undo group; revert "
                       + "with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name; omit if exactly one exists.\"}," +
                "\"steps\":{\"type\":\"array\",\"description\":\"Steps to create, in order.\",\"items\":{" +
                "\"type\":\"object\",\"properties\":{" +
                "\"type\":{\"type\":\"string\",\"description\":\"Concrete Step type name (or full name).\"}," +
                "\"parent\":{\"type\":\"string\",\"description\":\"Ref Id of an existing parent step; omit for the controller root.\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"GameObject name; omit to derive from the type.\"}}," +
                "\"required\":[\"type\"],\"additionalProperties\":false}}}," +
                "\"required\":[\"steps\"],\"additionalProperties\":false}",
            execute: ExecuteSequenceAddSteps,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteSequenceAddSteps(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            if (!(args["steps"] is JArray stepsArray) || stepsArray.Count == 0)
                return Error("'steps' must be a non-empty array.");

            // Validate every entry up front so a bad type/parent does not leave a partial batch.
            var requests = new List<StepCreationRequest>();
            for (int i = 0; i < stepsArray.Count; i++)
            {
                var entry = stepsArray[i] as JObject;
                var typeName = entry?.Value<string>("type");
                var stepType = ResolveStepType(typeName, out var typeError);
                if (stepType == null) return Error($"steps[{i}]: {typeError}");

                Step parent = null;
                var parentRefId = entry.Value<string>("parent");
                if (!string.IsNullOrWhiteSpace(parentRefId))
                {
                    parent = FindStepByRefId(controller, parentRefId);
                    if (parent == null)
                        return Error($"steps[{i}]: no step with Ref Id '{parentRefId}' under controller '{controller.name}'.");
                }

                requests.Add(new StepCreationRequest(stepType, parent, entry.Value<string>("name")));
            }

            var created = StepEditingService.AddSteps(controller, requests);
            return new JObject
            {
                ["controller"] = controller.name,
                ["createdCount"] = created.Count,
                ["created"] = DescribeSteps(created)
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── molca_sequence_remove_steps (Sprint 19.2) ────────────────────────────────────────

        /// <summary>
        /// The <c>molca_sequence_remove_steps</c> action tool: deletes steps (and their children) by
        /// Ref Id via <see cref="StepEditingService.RemoveSteps"/> as one undo group.
        /// </summary>
        private static McpToolDefinition CreateSequenceRemoveStepsTool() => new McpToolDefinition(
            name: "molca_sequence_remove_steps",
            description: "Deletes steps (and their descendants) from a SequenceController by Ref Id. Steps "
                       + "nested inside other named steps are removed with their parent. One undo group; "
                       + "revert with Ctrl+Z.",
            inputSchemaJson: StepRefIdsSchema(extra: null),
            execute: ExecuteSequenceRemoveSteps,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteSequenceRemoveSteps(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            var steps = ResolveSteps(controller, ReadStringArray(args, "stepRefIds"), out var notFound, out var argError);
            if (argError != null) return Error(argError);

            int removed = StepEditingService.RemoveSteps(steps);
            return new JObject
            {
                ["controller"] = controller.name,
                ["removedCount"] = removed,
                ["notFound"] = new JArray(notFound)
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── molca_sequence_duplicate_steps (Sprint 19.3) ─────────────────────────────────────

        /// <summary>
        /// The <c>molca_sequence_duplicate_steps</c> action tool: clones step subtrees by Ref Id via
        /// <see cref="StepEditingService.DuplicateSteps"/>; every cloned step gets a fresh Ref Id.
        /// </summary>
        private static McpToolDefinition CreateSequenceDuplicateStepsTool() => new McpToolDefinition(
            name: "molca_sequence_duplicate_steps",
            description: "Duplicates step subtrees on a SequenceController by Ref Id, placing each clone "
                       + "next to its original. Every step in a clone receives a fresh Ref Id. One undo "
                       + "group; revert with Ctrl+Z.",
            inputSchemaJson: StepRefIdsSchema(extra: null),
            execute: ExecuteSequenceDuplicateSteps,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteSequenceDuplicateSteps(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            var steps = ResolveSteps(controller, ReadStringArray(args, "stepRefIds"), out var notFound, out var argError);
            if (argError != null) return Error(argError);

            var clones = StepEditingService.DuplicateSteps(steps);
            return new JObject
            {
                ["controller"] = controller.name,
                ["duplicatedCount"] = clones.Count,
                ["created"] = DescribeSteps(clones),
                ["notFound"] = new JArray(notFound)
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── molca_sequence_change_type (Sprint 19.4) ─────────────────────────────────────────

        /// <summary>
        /// The <c>molca_sequence_change_type</c> action tool: converts steps to another step type via
        /// <see cref="StepEditingService.ChangeStepTypes"/>, preserving Ref Id, step id, and auxiliaries.
        /// </summary>
        private static McpToolDefinition CreateSequenceChangeTypeTool() => new McpToolDefinition(
            name: "molca_sequence_change_type",
            description: "Converts steps on a SequenceController to another step 'newType' (class name). "
                       + "Ref Id, step id, and auxiliaries are preserved. Steps already of the target type "
                       + "are skipped. One undo group; revert with Ctrl+Z.",
            inputSchemaJson: StepRefIdsSchema(extra:
                "\"newType\":{\"type\":\"string\",\"description\":\"Concrete Step type name (or full name) to convert to.\"}",
                extraRequired: "newType"),
            execute: ExecuteSequenceChangeType,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteSequenceChangeType(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            var newType = ResolveStepType(args.Value<string>("newType"), out var typeError);
            if (newType == null) return Error(typeError);

            var steps = ResolveSteps(controller, ReadStringArray(args, "stepRefIds"), out var notFound, out var argError);
            if (argError != null) return Error(argError);

            // Capture the pre-conversion type names: the old component is destroyed by the conversion.
            var oldTypeByRefId = steps.ToDictionary(s => s.RefId, s => s.GetType().Name);

            var pairs = StepEditingService.ChangeStepTypes(steps, newType);
            var converted = new JArray();
            foreach (var pair in pairs)
            {
                var step = pair.Value;
                converted.Add(new JObject
                {
                    ["refId"] = step.RefId,
                    ["name"] = step.name,
                    ["oldType"] = oldTypeByRefId.TryGetValue(step.RefId, out var ot) ? ot : null,
                    ["newType"] = newType.Name
                });
            }

            return new JObject
            {
                ["controller"] = controller.name,
                ["convertedCount"] = pairs.Count,
                ["converted"] = converted,
                ["notFound"] = new JArray(notFound)
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── molca_sequence_reparent (Sprint 19.5) ────────────────────────────────────────────

        /// <summary>
        /// The <c>molca_sequence_reparent</c> action tool: moves/reorders steps under a new parent via
        /// <see cref="StepEditingService.ReparentSteps"/>. Edges = execution flow, matching the graph editor.
        /// </summary>
        private static McpToolDefinition CreateSequenceReparentTool() => new McpToolDefinition(
            name: "molca_sequence_reparent",
            description: "Moves steps under a new parent step (by Ref Id) at an optional sibling index, or "
                       + "to the controller root when 'newParent' is omitted. Moving a step into its own "
                       + "subtree is rejected. One undo group; revert with Ctrl+Z.",
            inputSchemaJson: StepRefIdsSchema(extra:
                "\"newParent\":{\"type\":\"string\",\"description\":\"Ref Id of the new parent step; omit for the controller root.\"}," +
                "\"siblingIndex\":{\"type\":\"integer\",\"description\":\"Index of the first moved step among the parent's children; omit to append.\"}"),
            execute: ExecuteSequenceReparent,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteSequenceReparent(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var controller = ResolveController(args.Value<string>("controller"), out var error);
            if (controller == null) return Error(error);

            var steps = ResolveSteps(controller, ReadStringArray(args, "stepRefIds"), out var notFound, out var argError);
            if (argError != null) return Error(argError);

            Transform newParent = controller.transform;
            var newParentRefId = args.Value<string>("newParent");
            if (!string.IsNullOrWhiteSpace(newParentRefId))
            {
                var parentStep = FindStepByRefId(controller, newParentRefId);
                if (parentStep == null)
                    return Error($"No step with Ref Id '{newParentRefId}' under controller '{controller.name}'.");
                newParent = parentStep.transform;
            }

            int siblingIndex = args.Value<int?>("siblingIndex") ?? -1;
            int moved = StepEditingService.ReparentSteps(steps, newParent, siblingIndex);
            return new JObject
            {
                ["controller"] = controller.name,
                ["movedCount"] = moved,
                ["newParent"] = string.IsNullOrWhiteSpace(newParentRefId) ? "(root)" : newParentRefId,
                ["notFound"] = new JArray(notFound)
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── molca_sequence_fix (Sprint 19.6) ─────────────────────────────────────────────────

        /// <summary>
        /// The <c>molca_sequence_fix</c> action tool: the write counterpart to read-only
        /// <c>molca_validate_sequence</c>. Applies a <see cref="SequenceValidator"/> fix action (today the
        /// broken-auxiliary fix) to a selected finding. Shares its implementation with the
        /// <c>molca_run_doctor_fix</c> alias (see <see cref="ExecuteSequenceFix"/> in the Actions partial).
        /// </summary>
        private static McpToolDefinition CreateSequenceFixTool() => new McpToolDefinition(
            name: "molca_sequence_fix",
            description: "Applies a fix for a sequence validation finding from molca_validate_sequence. "
                       + "Currently supports the broken-auxiliary fix: reassigns a step auxiliary to a valid "
                       + "type. Identify the controller, the step Ref Id, the auxiliary index, and the new "
                       + "auxiliary type name. A scene backup is taken first; revert with molca_undo_last_action. "
                       + "Prefer molca_sequence_remediate, which runs the full validate→fix→re-validate loop "
                       + "(this tool is a thin subset kept for back-compat).",
            inputSchemaJson: SequenceFixSchema,
            execute: ExecuteSequenceFix,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.FileSnapshot);

        // ── Shared plumbing (Sprint 19.7) ────────────────────────────────────────────────────

        private static JObject ParseArgs(string argumentsJson)
        {
            try { return JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson); }
            catch { return new JObject(); }
        }

        /// <summary>
        /// Resolves the target controller by Ref Id or name, or the sole controller when none is named.
        /// Sets <paramref name="error"/> (and returns null) on no/ambiguous/unmatched controller.
        /// </summary>
        private static SequenceController ResolveController(string wanted, out string error)
        {
            error = null;
            var controllers = UnityEngine.Object.FindObjectsByType<SequenceController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (controllers.Length == 0)
            {
                error = "No SequenceController found in the loaded scene(s).";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(wanted))
            {
                var match = controllers.FirstOrDefault(c => c.RefId == wanted || c.name == wanted);
                if (match == null)
                    error = $"No SequenceController matched '{wanted}'. Available: {DescribeControllers(controllers)}.";
                return match;
            }

            if (controllers.Length == 1) return controllers[0];

            error = $"Multiple SequenceControllers found; specify 'controller'. Available: {DescribeControllers(controllers)}.";
            return null;
        }

        private static string DescribeControllers(SequenceController[] controllers)
            => string.Join(", ", controllers.Select(c => $"{c.name} (refId:{c.RefId})"));

        /// <summary>
        /// Resolves step Ref Ids to <see cref="Step"/> instances under the controller, recording any that
        /// did not match. Sets <paramref name="argError"/> when the 'stepRefIds' argument is missing/empty.
        /// </summary>
        private static List<Step> ResolveSteps(SequenceController controller, IList<string> refIds,
            out List<string> notFound, out string argError)
        {
            argError = null;
            notFound = new List<string>();
            if (refIds == null || refIds.Count == 0)
            {
                argError = "'stepRefIds' must be a non-empty array.";
                return new List<Step>();
            }

            var all = controller.GetComponentsInChildren<Step>(true);
            var found = new List<Step>();
            foreach (var id in refIds)
            {
                var step = all.FirstOrDefault(s => s.RefId == id);
                if (step != null) found.Add(step);
                else notFound.Add(id);
            }
            return found;
        }

        private static Step FindStepByRefId(SequenceController controller, string refId)
            => controller.GetComponentsInChildren<Step>(true).FirstOrDefault(s => s.RefId == refId);

        /// <summary>
        /// Resolves a concrete <see cref="Step"/>-derived type by simple or full name (including
        /// <see cref="Step"/> itself). Sets <paramref name="error"/> on unknown/abstract types.
        /// </summary>
        private static Type ResolveStepType(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "step type name is required.";
                return null;
            }

            var concrete = TypeCache.GetTypesDerivedFrom<Step>()
                .Append(typeof(Step))
                .Where(t => !t.IsAbstract)
                .ToList();
            var match = concrete.FirstOrDefault(t => t.Name == name || t.FullName == name);
            if (match == null)
                error = $"no concrete Step type named '{name}'. Available: "
                        + string.Join(", ", concrete.Select(t => t.Name).OrderBy(n => n)) + ".";
            return match;
        }

        private static List<string> ReadStringArray(JObject args, string key)
        {
            if (!(args[key] is JArray arr)) return null;
            return arr.Select(t => t.Type == JTokenType.String ? t.Value<string>() : t.ToString())
                      .Where(s => !string.IsNullOrWhiteSpace(s))
                      .ToList();
        }

        private static JArray DescribeSteps(IEnumerable<Step> steps)
        {
            var arr = new JArray();
            foreach (var s in steps.Where(s => s != null))
            {
                arr.Add(new JObject
                {
                    ["refId"] = s.RefId,
                    ["name"] = s.name,
                    ["type"] = s.GetType().Name
                });
            }
            return arr;
        }

        /// <summary>
        /// Builds the shared input schema for tools whose arguments are a controller + a 'stepRefIds'
        /// array, optionally with one or more <paramref name="extra"/> property fragments.
        /// </summary>
        private static string StepRefIdsSchema(string extra, string extraRequired = null)
        {
            var properties =
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name; omit if exactly one exists.\"}," +
                "\"stepRefIds\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Ref Ids of the target steps.\"}";
            if (!string.IsNullOrEmpty(extra)) properties += "," + extra;

            var required = "\"stepRefIds\"";
            if (!string.IsNullOrEmpty(extraRequired)) required += $",\"{extraRequired}\"";

            return "{\"type\":\"object\",\"properties\":{" + properties + "}," +
                   "\"required\":[" + required + "],\"additionalProperties\":false}";
        }
    }
}
