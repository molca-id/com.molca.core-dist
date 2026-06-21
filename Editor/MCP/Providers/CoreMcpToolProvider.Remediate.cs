using System.Linq;
using Molca.Editor.Validation;
using Molca.Sequence;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_sequence_remediate</c> tool (Sprint 38): runs the validation registry, optionally
        /// applies all safe fixes (and/or one explicit opt-in fix), re-validates, and reports before/after
        /// plus the residual findings with their remediation suggestions. Composes the validate→fix→
        /// re-validate loop in one call; the shipped <c>molca_sequence_fix</c> is untouched.
        /// </summary>
        private static McpToolDefinition CreateRemediateSequenceTool() => new McpToolDefinition(
            name: "molca_sequence_remediate",
            description: "Validates a SequenceController, applies safe fixes (regenerate empty/duplicate "
                       + "Ref Ids, re-enable inactive parents) in one undoable pass, then re-validates and "
                       + "returns before/after counts plus residual findings (each with fixHint and Ref Id "
                       + "suggestions). Optionally apply one explicit fix via 'fix' (e.g. clear a broken "
                       + "reference, or reassign a broken auxiliary type). Reference targets are never "
                       + "blindly rebound — use the suggestions to rebind via molca_sequence_set_step_fields, "
                       + "then re-run until valid. Revert with molca_undo_last_action / Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"controller\":{\"type\":\"string\",\"description\":\"Controller Ref Id or GameObject name.\"}," +
                "\"applySafeFixes\":{\"type\":\"boolean\",\"description\":\"Apply all safe fixes (default true).\"}," +
                "\"fix\":{\"type\":\"object\",\"description\":\"One explicit (opt-in) fix to apply.\",\"properties\":{" +
                "\"category\":{\"type\":\"string\",\"description\":\"Finding category to fix (e.g. UnresolvedReference, BrokenAuxiliary).\"}," +
                "\"stepRefId\":{\"type\":\"string\",\"description\":\"Ref Id of the step the finding is on.\"}," +
                "\"auxiliaryIndex\":{\"type\":\"integer\",\"description\":\"Auxiliary index (broken-auxiliary fixes).\"}," +
                "\"args\":{\"type\":\"object\",\"description\":\"Fix-specific arguments (e.g. {\\\"newType\\\":\\\"...\\\"}).\"}}}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteRemediateSequence,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteRemediateSequence(string argumentsJson)
        {
            var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var target = ResolveController(args.Value<string>("controller"), out var controllerError);
            if (target == null) return Error(controllerError);

            var before = SequenceValidatorRegistry.Run(target);
            var applied = new JArray();
            var reverts = new JArray();
            bool requiresReload = false;

            // Explicit opt-in fix first (it references a finding from the current state). Reported as its
            // own revert phase — we never fold a file-snapshot fix into the Unity-Undo group.
            var fixSpec = args["fix"] as JObject;
            if (fixSpec != null)
            {
                var explicitResult = ApplyExplicitFix(target, before, fixSpec, out var explicitError);
                if (explicitError != null) return Error(explicitError);
                applied.Add(explicitResult);
                if (explicitResult.Value<bool>("applied"))
                {
                    requiresReload |= explicitResult.Value<bool>("requiresSceneReload");
                    AddRevert(reverts, explicitResult.Value<string>("mechanism"), explicitResult.Value<string>("handle"));
                }
            }

            // Safe blanket pass (default on) — facet-selected SafeOnly policy (Unity-Undo, non-destructive).
            if (args.Value<bool?>("applySafeFixes") != false)
            {
                var pass = SequenceFixRegistry.ApplyFixes(
                    target, SequenceValidatorRegistry.Run(target), RemediationPolicy.SafeOnly);
                foreach (var kvp in pass.AppliedByCategory)
                    applied.Add(new JObject { ["category"] = kvp.Key, ["count"] = kvp.Value, ["policy"] = "SafeOnly" });
                requiresReload |= pass.RequiresSceneReload;
                foreach (var mechanism in pass.Mechanisms)
                    AddRevert(reverts, mechanism.ToString(),
                        mechanism == FixReversibility.UnityUndo ? "editor-undo" : null);
            }

            var context = new SequenceValidationContext(target, target.GetComponentsInChildren<Step>(true));
            var after = SequenceValidatorRegistry.Run(target);
            SequenceFindingEnricher.Enrich(after, context); // attach suggestions/fixHint on demand (Sprint 41)

            int beforeErr = before.Count(f => f.Severity == SequenceValidationSeverity.Error);
            int beforeWarn = before.Count(f => f.Severity == SequenceValidationSeverity.Warning);
            int afterErr = after.Count(f => f.Severity == SequenceValidationSeverity.Error);
            int afterWarn = after.Count(f => f.Severity == SequenceValidationSeverity.Warning);

            return new JObject
            {
                ["controller"] = target.name,
                ["controllerRefId"] = target.RefId,
                ["before"] = new JObject { ["errorCount"] = beforeErr, ["warningCount"] = beforeWarn },
                ["applied"] = applied,
                ["after"] = new JObject
                {
                    ["errorCount"] = afterErr,
                    ["warningCount"] = afterWarn,
                    ["valid"] = afterErr == 0
                },
                ["requiresSceneReload"] = requiresReload,
                ["reverts"] = reverts,
                ["residual"] = new JArray(after.Select(SerializeFinding)),
                ["message"] = requiresReload
                    ? "Some fixes rewrote scene YAML — reload the scene to see them. Revert each phase per 'reverts'."
                    : "Revert via 'reverts' (Ctrl+Z / molca_undo_last_action)."
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // Adds a distinct revert entry. Each mechanism is reported once so the caller never has to guess
        // that a single "undo" spans both the Unity stack and a file snapshot.
        private static void AddRevert(JArray reverts, string mechanism, string handle)
        {
            if (string.IsNullOrEmpty(mechanism)) return;
            if (reverts.Any(r => r.Value<string>("mechanism") == mechanism)) return;
            reverts.Add(new JObject
            {
                ["mechanism"] = mechanism,
                ["handle"] = handle,
                ["description"] = mechanism == nameof(FixReversibility.FileSnapshot)
                    ? "Restore the scene-file snapshot via molca_undo_last_action."
                    : "Revert the in-memory changes with Ctrl+Z or molca_undo_last_action."
            });
        }

        // Applies one explicit (opt-in) fix to the matching finding. Snapshots the scene file first for
        // the broken-auxiliary YAML rewrite (which Unity Undo cannot revert), mirroring molca_sequence_fix.
        private static JObject ApplyExplicitFix(
            SequenceController target, System.Collections.Generic.List<SequenceValidationFinding> findings,
            JObject fixSpec, out string error)
        {
            error = null;
            var category = fixSpec.Value<string>("category");
            var stepRefId = fixSpec.Value<string>("stepRefId");
            var auxIndex = fixSpec.Value<int?>("auxiliaryIndex") ?? -1;
            var fixArgs = fixSpec["args"] as JObject;

            if (string.IsNullOrWhiteSpace(category))
            {
                error = "'fix.category' is required.";
                return null;
            }

            var finding = findings.FirstOrDefault(f =>
                f.Category == category
                && (string.IsNullOrEmpty(stepRefId) || f.StepRefId == stepRefId)
                && (auxIndex < 0 || f.AuxiliaryIndex == auxIndex));
            if (finding == null)
            {
                error = $"No '{category}' finding matched (stepRefId:'{stepRefId}', auxiliaryIndex:{auxIndex}).";
                return null;
            }

            var fix = SequenceFixRegistry.FixesFor(category).FirstOrDefault();
            if (fix == null)
            {
                error = $"No fix is registered for category '{category}'.";
                return null;
            }

            var context = new SequenceValidationContext(target, target.GetComponentsInChildren<Step>(true));

            // YAML-rewriting fixes aren't Unity-Undo revertible — snapshot the scene file first.
            string snapshotId = null;
            bool yamlFix = category == nameof(Molca.Editor.SequenceFindingType.BrokenAuxiliary);
            if (yamlFix && finding.Step != null)
            {
                var scenePath = finding.Step.gameObject.scene.path;
                snapshotId = McpUndoStack.Snapshot(scenePath, "molca_sequence_remediate",
                    $"{category} fix on '{stepRefId}' ({System.IO.Path.GetFileName(scenePath)})");
            }

            var outcome = fix.Apply(finding, context, fixArgs);
            if (!outcome.Applied && snapshotId != null)
                McpUndoStack.Discard(snapshotId); // no change — drop the redundant backup

            return new JObject
            {
                ["category"] = category,
                ["fix"] = fix.Id,
                ["stepRefId"] = finding.StepRefId,
                ["applied"] = outcome.Applied,
                ["destructive"] = fix.IsDestructive,
                ["mechanism"] = fix.Reversibility.ToString(),
                ["handle"] = yamlFix ? snapshotId : "editor-undo",
                ["requiresSceneReload"] = outcome.RequiresSceneReload,
                ["message"] = outcome.Message
            };
        }

        private static JObject SerializeFinding(SequenceValidationFinding f) => new()
        {
            ["category"] = f.Category,
            ["validator"] = f.ValidatorId,
            ["severity"] = f.Severity.ToString(),
            ["message"] = f.Message,
            ["stepRefId"] = f.StepRefId,
            ["stepName"] = f.StepName,
            ["auxiliaryIndex"] = f.AuxiliaryIndex,
            ["fixHint"] = f.FixHint,
            ["suggestions"] = new JArray(f.Suggestions)
        };
    }
}
