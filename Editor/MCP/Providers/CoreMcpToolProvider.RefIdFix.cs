using System.Linq;
using Molca.Editor.ReferenceSystem;
using Molca.Editor.ReferenceSystem.Repair;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// The <c>molca_fix_refids</c> Action tool: deprecated adapter over the repair-plan system.
    /// </summary>
    /// <remarks>
    /// Kept so existing MCP clients keep working, but it owns no repair logic of its own any more — it plans
    /// with <see cref="ReferenceRepairPlanner.PlanSafeRepairs"/> and applies with
    /// <see cref="ReferenceRepairExecutor"/>, exactly as <c>molca_references_apply_fix</c> does. It
    /// previously reimplemented the rules and got two of them wrong: it detected duplicates on the Ref Id
    /// alone, which re-keyed legal same-id/different-type providers, and it re-keyed duplicates without
    /// checking for inbound references, which silently re-pointed them at the wrong object.
    ///
    /// Prefer <c>molca_references_plan_fix</c> + <c>molca_references_apply_fix</c>: this tool applies
    /// without a review step, which is the thing the plan/apply split exists to prevent.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        private static McpToolDefinition CreateFixRefIdsTool() => new McpToolDefinition(
            name: "molca_fix_refids",
            description: "DEPRECATED — use molca_references_plan_fix then molca_references_apply_fix, which "
                       + "let you review the exact changes first. Applies every unambiguous reference repair "
                       + "in one Unity Undo group: assigns Ref Ids to providers that have none, and re-keys a "
                       + "duplicated (RefType, RefId) only when nothing references it. Duplicates with inbound "
                       + "references, ambiguous targets and missing targets are reported for a human decision, "
                       + "never changed.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            executeAsync: ExecuteFixRefIdsAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static async Awaitable<string> ExecuteFixRefIdsAsync(string argumentsJson)
        {
            // A repair starts from a specific audit, so the plan's preconditions are evaluated against a
            // snapshot rather than against a re-derived, possibly different view of the project.
            var snapshot = await ReferenceAuditService.RefreshAsync(ReferenceAuditService.DefaultScope());
            var plan = ReferenceRepairPlanner.PlanSafeRepairs(snapshot);
            var choices = ReferenceRepairPlanner.DescribeChoices(snapshot);

            if (plan.IsEmpty)
            {
                return new JObject
                {
                    ["deprecated"] = "Use molca_references_plan_fix then molca_references_apply_fix.",
                    ["fixedCount"] = 0,
                    ["changed"] = new JArray(),
                    ["needsDecision"] = Choices(choices),
                    ["auditRevision"] = snapshot.Revision,
                    ["coverageComplete"] = snapshot.Coverage.IsComplete,
                    ["message"] = "No Ref Id could be repaired without guessing. "
                                + $"{choices.Count} case(s) need your decision.",
                }.ToString(Formatting.None);
            }

            var outcome = await ReferenceRepairExecutor.ApplyAsync(plan);

            return new JObject
            {
                ["deprecated"] = "Use molca_references_plan_fix then molca_references_apply_fix.",
                ["fixedCount"] = outcome.Applied.Count,
                ["changed"] = new JArray(outcome.Applied.Select(m => new JObject
                {
                    ["kind"] = m.Kind.ToString(),
                    ["asset"] = m.AssetPath,
                    ["object"] = m.Target.ObjectPath,
                    ["description"] = m.Describe(),
                    ["reason"] = m.Reason,
                })),
                ["skipped"] = new JArray(outcome.Skipped.Select(s => new JObject
                {
                    ["change"] = s.Mutation.Describe(),
                    ["reason"] = s.Reason,
                })),
                ["needsDecision"] = Choices(choices),
                ["introduced"] = Findings(outcome.Introduced),
                ["auditRevision"] = snapshot.Revision,
                ["coverageComplete"] = snapshot.Coverage.IsComplete,
                ["undoGroup"] = outcome.UndoGroupName,
                ["message"] = outcome.Describe(),
            }.ToString(Formatting.None);
        }
    }
}
