using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Editor.Doctor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_scene_fix</c> tool (Sprint 55): applies a registered <see cref="ISceneFix"/> to one
        /// mechanical scene-audit finding target, then re-runs the relevant <c>scene-*</c> check to confirm
        /// the finding cleared — the <c>validate → fix → re-validate</c> loop for the auditor. Judgment
        /// findings (over-budget aggregates, subsystem-placement) have no fix and are reported as such.
        /// Edit/Action, behind the allowlist + confirmation; supports a dry-run. Discovered via convention.
        /// </summary>
        private static McpToolDefinition CreateSceneFixTool() => new McpToolDefinition(
            name: "molca_scene_fix",
            description: "Apply a safe, mechanical fix to a scene-audit finding from molca_scene_audit, then "
                       + "re-validate. Provide 'target' (the finding's path) and either 'fix' (a fix id) or "
                       + "'checkId' (the finding's checkId; the matching fix is chosen). Fixable: "
                       + "scene-instancing-budget (enable GPU Instancing), scene-lighting-budget (Contribute "
                       + "GI), scene-texture-budget (reduce max texture size), scene-structure (add a LODGroup "
                       + "scaffold). Set 'dryRun' to report the change without writing. Judgment findings "
                       + "(triangle totals, subsystem-placement) report no automatic fix. Revert object edits "
                       + "with Ctrl+Z / molca_undo_last_action; texture-size changes via molca_undo_last_action.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"The finding's path (asset path or 'scene :: hierarchy/path').\"}," +
                "\"fix\":{\"type\":\"string\",\"description\":\"Explicit fix id (e.g. scene.enable-instancing).\"}," +
                "\"checkId\":{\"type\":\"string\",\"description\":\"The finding's checkId; the matching fix is chosen when 'fix' is omitted.\"}," +
                "\"dryRun\":{\"type\":\"boolean\",\"description\":\"Report what would change without writing (default false).\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            executeAsync: ExecuteSceneFixAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static async Awaitable<string> ExecuteSceneFixAsync(string argumentsJson)
        {
            JObject args;
            try { args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson); }
            catch { return Error("Invalid JSON arguments."); }

            var target = args.Value<string>("target");
            if (string.IsNullOrWhiteSpace(target)) return Error("'target' is required (the finding's path).");
            var dryRun = args.Value<bool?>("dryRun") ?? false;

            var fix = ResolveSceneFix(args, out var resolveMessage);
            if (fix == null) return Error(resolveMessage);

            SceneFixOutcome outcome;
            try { outcome = fix.Apply(target, dryRun, CancellationToken.None); }
            catch (System.Exception e) { return Error($"Fix '{fix.Id}' threw: {e.Message}"); }

            var result = new JObject
            {
                ["fix"] = fix.Id,
                ["checkId"] = fix.HandledCheckId,
                ["target"] = target,
                ["dryRun"] = dryRun,
                ["applied"] = outcome.Applied,
                ["message"] = outcome.Message,
                ["reversibility"] = fix.Reversibility.ToString()
            };
            if (!string.IsNullOrEmpty(outcome.Before)) result["before"] = outcome.Before;
            if (!string.IsNullOrEmpty(outcome.After)) result["after"] = outcome.After;
            if (!string.IsNullOrEmpty(outcome.UndoEntryId)) result["undoEntryId"] = outcome.UndoEntryId;

            // Re-validate only after a real write, to confirm the finding cleared.
            if (outcome.Applied && !dryRun)
            {
                var remaining = await MolcaDoctor.RunAllAsync(new HashSet<string> { fix.HandledCheckId });
                var stillFlagged = remaining.Count(i => i.Path == target);
                result["clearedFinding"] = stillFlagged == 0;
                result["remainingForTarget"] = stillFlagged;
                result["remainingForCheck"] = remaining.Count;
            }

            return result.ToString(Formatting.None);
        }

        /// <summary>Resolves the requested <see cref="ISceneFix"/> from an explicit id or a finding checkId.</summary>
        private static ISceneFix ResolveSceneFix(JObject args, out string message)
        {
            message = null;
            var fixId = args.Value<string>("fix");
            if (!string.IsNullOrWhiteSpace(fixId))
            {
                var byId = SceneFixRegistry.ById(fixId);
                if (byId == null) message = $"No scene fix with id '{fixId}'. Known fixes: {KnownFixIds()}.";
                return byId;
            }

            var checkId = args.Value<string>("checkId");
            if (string.IsNullOrWhiteSpace(checkId))
            {
                message = "Provide 'fix' (a fix id) or 'checkId' (the finding's checkId).";
                return null;
            }

            var forCheck = SceneFixRegistry.ForCheck(checkId);
            if (forCheck.Count == 0)
            {
                message = $"No automatic fix for '{checkId}' — it is a judgment finding (review it manually).";
                return null;
            }
            return forCheck[0];
        }

        private static string KnownFixIds() => string.Join(", ", SceneFixRegistry.All.Select(f => f.Id));
    }
}
