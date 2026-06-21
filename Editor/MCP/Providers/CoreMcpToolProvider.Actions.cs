using System.Linq;
using Molca.Editor.Validation;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        // ── molca_run_doctor_fix (Sprint 17.3) ───────────────────────────────────────────────

        // The shared input schema for the sequence-fix tools (canonical molca_sequence_fix + its
        // back-compat alias molca_run_doctor_fix). Two modes: a single broken-auxiliary fix
        // (stepRefId + auxiliaryIndex + newType), or fixAll:true to auto-fix every auto-fixable
        // finding on a controller (empty/duplicate Ref Ids, inactive parents).
        private const string SequenceFixSchema =
            "{\"type\":\"object\",\"properties\":{" +
            "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name.\"}," +
            "\"fixAll\":{\"type\":\"boolean\",\"description\":\"Auto-fix every auto-fixable finding on the controller (empty/duplicate Ref Ids, inactive parents). Ignores the broken-auxiliary fields.\"}," +
            "\"stepRefId\":{\"type\":\"string\",\"description\":\"Ref Id of the step holding the broken auxiliary (single-fix mode).\"}," +
            "\"auxiliaryIndex\":{\"type\":\"integer\",\"description\":\"Index of the auxiliary on the step (single-fix mode).\"}," +
            "\"newType\":{\"type\":\"string\",\"description\":\"Name (or full name) of the StepAuxiliary type to assign (single-fix mode).\"}}," +
            "\"additionalProperties\":false}";

        /// <summary>
        /// The <c>molca_run_doctor_fix</c> action tool (Sprint 17.3): a back-compat alias of
        /// <c>molca_sequence_fix</c> (Sprint 19.6). Both apply the broken-auxiliary fix
        /// (<see cref="SequenceValidator.TryFixBrokenAuxiliary"/> via <c>AuxiliaryTypeFixerUtility</c>) to
        /// a selected finding on a sequence controller — the one auto-fixable finding the framework
        /// exposes today — through the shared <see cref="ExecuteSequenceFix"/>. Retained because clients
        /// may already have it on their action allowlist. Gated as an Action.
        /// </summary>
        private static McpToolDefinition CreateDoctorFixTool() => new McpToolDefinition(
            name: "molca_run_doctor_fix",
            description: "Alias of molca_sequence_fix. Applies a fix for a sequence validation finding "
                       + "(currently the broken-auxiliary fix: reassigns a step auxiliary to a valid type). "
                       + "Identify the controller, the step Ref Id, the auxiliary index, and the new "
                       + "auxiliary type name.",
            inputSchemaJson: SequenceFixSchema,
            execute: ExecuteSequenceFix,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.FileSnapshot);

        /// <summary>
        /// Shared implementation behind <c>molca_sequence_fix</c> and its <c>molca_run_doctor_fix</c>
        /// alias: applies a <see cref="SequenceValidator"/> fix action (the broken-auxiliary fix) to a
        /// selected finding, snapshotting the scene file first so the change can be reverted.
        /// </summary>
        private static string ExecuteSequenceFix(string argumentsJson)
        {
            var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var target = ResolveController(args.Value<string>("controller"), out var controllerError);
            if (target == null) return Error(controllerError);

            // Fix-all mode: auto-fix every auto-fixable finding (empty/duplicate Ref Id, inactive parent).
            if (args.Value<bool?>("fixAll") == true)
                return ExecuteSequenceFixAll(target);

            var stepRefId = args.Value<string>("stepRefId");
            var auxIndex = args.Value<int?>("auxiliaryIndex") ?? -1;
            var newTypeName = args.Value<string>("newType");

            if (string.IsNullOrWhiteSpace(stepRefId) || string.IsNullOrWhiteSpace(newTypeName))
                return Error("'stepRefId' and 'newType' are required (or pass 'fixAll':true).");

            // Resolve the StepAuxiliary-derived type by name.
            var newType = TypeCache.GetTypesDerivedFrom<StepAuxiliary>()
                .FirstOrDefault(t => t.Name == newTypeName || t.FullName == newTypeName);
            if (newType == null)
                return Error($"No StepAuxiliary type named '{newTypeName}'.");

            // Resolve the live finding through the registry and apply via the registry's BrokenAuxiliaryFix
            // (single source of fix truth, Sprint 41) rather than calling SequenceValidator directly.
            var finding = SequenceValidatorRegistry.Run(target).FirstOrDefault(f =>
                f.Category == nameof(SequenceFindingType.BrokenAuxiliary) && f.Step != null
                && f.Step.RefId == stepRefId && f.AuxiliaryIndex == auxIndex);
            if (finding == null)
                return Error($"No fixable broken-auxiliary finding for step '{stepRefId}' index {auxIndex}.");

            // Back up the scene file before the YAML rewrite so the change can be reverted.
            var scenePath = finding.Step.gameObject.scene.path;
            var undoId = McpUndoStack.Snapshot(scenePath, "molca_sequence_fix",
                $"Auxiliary fix on '{stepRefId}' index {auxIndex} → {newType.Name} ({System.IO.Path.GetFileName(scenePath)})");

            var auxFix = SequenceFixRegistry.FixesFor(nameof(SequenceFindingType.BrokenAuxiliary)).FirstOrDefault();
            var fixContext = new SequenceValidationContext(target, target.GetComponentsInChildren<Step>(true));
            var ok = auxFix != null
                     && auxFix.Apply(finding, fixContext, new JObject { ["newType"] = newTypeName }).Applied;
            if (!ok)
                McpUndoStack.Discard(undoId); // no change made — drop the redundant backup

            return new JObject
            {
                ["fixed"] = ok,
                ["controller"] = target.name,
                ["stepRefId"] = stepRefId,
                ["auxiliaryIndex"] = auxIndex,
                ["newType"] = newType.Name,
                ["revertible"] = ok && undoId != null,
                ["message"] = ok
                    ? "Auxiliary type reassigned (scene reload may be needed). Revert via molca_undo_last_action or the 'Revert last MCP action' button."
                    : "Fix could not be applied (see Console)."
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Fix-all mode for <c>molca_sequence_fix</c>: applies <see cref="SequenceValidator.TryAutoFix"/>
        /// to every auto-fixable finding on the controller (empty/duplicate Ref Ids, inactive parents).
        /// Broken auxiliaries are excluded — they need a replacement type and so require the single-fix
        /// path. In-memory + Unity-Undo revertible (no scene snapshot).
        /// </summary>
        private static string ExecuteSequenceFixAll(SequenceController target)
        {
            // Route through the single SequenceFixRegistry SafeOnly pass (Sprint 41) so fixAll can never
            // drift from molca_sequence_remediate's safe pass. Output shape is preserved for back-compat.
            var findings = SequenceValidatorRegistry.Run(target);
            int autoFixableCount = findings.Count(f =>
                SequenceFixRegistry.FixesFor(f.Category)
                    .Any(x => x.IsDeterministic && SequenceFixRegistry.PolicyAllows(RemediationPolicy.SafeOnly, x)));

            var pass = SequenceFixRegistry.ApplyFixes(target, findings, RemediationPolicy.SafeOnly);

            var byType = new JObject();
            foreach (var kvp in pass.AppliedByCategory)
                byType[kvp.Key] = kvp.Value;

            return new JObject
            {
                ["controller"] = target.name,
                ["mode"] = "fixAll",
                ["autoFixableCount"] = autoFixableCount,
                ["fixedCount"] = pass.TotalApplied,
                ["fixedByType"] = byType,
                ["revertible"] = pass.TotalApplied > 0,
                ["message"] = pass.TotalApplied > 0
                    ? $"Auto-fixed {pass.TotalApplied} finding(s). Revert with Ctrl+Z. Broken auxiliaries (if any) need molca_sequence_fix with a replacement type."
                    : "No auto-fixable findings."
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── molca_trigger_build (Sprint 17.4) ────────────────────────────────────────────────

        /// <summary>
        /// The <c>molca_trigger_build</c> action tool (Sprint 17.4): kicks a build profile through
        /// <c>BuildManager.BuildAsync</c> (which runs the pre-build Doctor gate). Async; Action — runs
        /// only when allowlisted and confirmed.
        /// </summary>
        private static McpToolDefinition CreateTriggerBuildTool() => new McpToolDefinition(
            name: "molca_trigger_build",
            description: "Triggers a build for a named build profile via BuildManager (with the pre-build "
                       + "Doctor gate). Returns the build result, output path, error count and size.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"profile\":{\"type\":\"string\",\"description\":\"Build profile name.\"}}," +
                "\"required\":[\"profile\"],\"additionalProperties\":false}",
            executeAsync: ExecuteTriggerBuildAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible); // a build cannot be undone

        private static async Awaitable<string> ExecuteTriggerBuildAsync(string argumentsJson)
        {
            var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var profile = args.Value<string>("profile");
            if (string.IsNullOrWhiteSpace(profile))
                return Error("'profile' is required.");

            var report = await BuildManager.BuildAsync(profile, runPreBuildChecks: true);
            if (report == null)
                return Error($"Build did not run (unknown profile '{profile}' or aborted by the pre-build gate).");

            var summary = report.summary;
            return new JObject
            {
                ["profile"] = profile,
                ["result"] = summary.result.ToString(),
                ["outputPath"] = summary.outputPath,
                ["totalErrors"] = (long)summary.totalErrors,
                ["totalWarnings"] = (long)summary.totalWarnings,
                ["totalSizeBytes"] = (long)summary.totalSize
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── molca_undo_last_action (Sprint 17) ───────────────────────────────────────────────

        /// <summary>
        /// The <c>molca_undo_last_action</c> action tool: reverts the most recent file-snapshot action
        /// when one exists, otherwise falls back to Unity's editor undo stack for UnityUndo actions.
        /// Itself gated as an Action (allowlist + confirmation); irreversible (redo is owned by the editor).
        /// </summary>
        private static McpToolDefinition CreateUndoLastTool() => new McpToolDefinition(
            name: "molca_undo_last_action",
            description: "Reverts the most recent revertible MCP action. File-snapshot actions restore "
                       + "their backup; UnityUndo actions use the editor undo stack. Builds are not revertible.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: _ =>
            {
                var message = McpUndoStack.HasEntries
                    ? McpUndoStack.UndoLast()
                    : UndoLastUnityAction();
                return new JObject
                {
                    ["reverted"] = message.StartsWith("Reverted"),
                    ["message"] = message,
                    ["remaining"] = McpUndoStack.Entries.Count
                }.ToString(Newtonsoft.Json.Formatting.None);
            },
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string UndoLastUnityAction()
        {
            try
            {
                Undo.PerformUndo();
                return "Reverted: latest Unity undo group";
            }
            catch (System.Exception ex)
            {
                return $"Revert failed: {ex.Message}";
            }
        }
    }
}
