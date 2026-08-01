using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.ReferenceSystem;
using Molca.Editor.ReferenceSystem.Repair;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The plan that <c>molca_references_plan_fix</c> last produced, so
        /// <c>molca_references_apply_fix</c> can apply the plan that was actually shown rather than
        /// re-deriving one from data that may have moved.
        /// </summary>
        private static ReferenceRepairPlan _pendingRepairPlan;

        /// <summary>
        /// The <c>molca_references_plan_fix</c> tool: builds a reference repair plan and returns it for
        /// review. Changes nothing.
        /// </summary>
        private static McpToolDefinition CreateReferencesPlanFixTool() => new McpToolDefinition(
            name: "molca_references_plan_fix",
            description: "Builds a reviewable reference repair plan from a fresh audit and returns it — every "
                       + "object, property and before/after value — without changing anything. Only repairs "
                       + "whose outcome the data determines are planned; a duplicate Ref Id that something "
                       + "references, an ambiguous target, or a missing target are returned under 'choices' "
                       + "for a human decision instead. Apply with molca_references_apply_fix.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"mode\":{\"type\":\"string\",\"enum\":[\"safe\",\"redirect\",\"clear\"]," +
                "\"description\":\"safe (default) plans every unambiguous repair. redirect points one " +
                "reference at one provider and needs siteKey + providerKey. clear unsets one reference and " +
                "needs siteKey.\"}," +
                "\"siteKey\":{\"type\":\"string\",\"description\":\"Reference site key, from molca_references_audit.\"}," +
                "\"providerKey\":{\"type\":\"string\",\"description\":\"Target provider key, from molca_references_audit.\"}}," +
                "\"additionalProperties\":false}",
            executeAsync: ExecuteReferencesPlanFixAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteReferencesPlanFixAsync(string argumentsJson)
        {
            var mode = "safe";
            string siteKey = null;
            string providerKey = null;

            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                mode = args.Value<string>("mode") ?? "safe";
                siteKey = args.Value<string>("siteKey");
                providerKey = args.Value<string>("providerKey");
            }
            catch
            {
                // Defaults: plan every safe repair.
            }

            // A plan is only meaningful against a current audit, so this always refreshes rather than
            // reusing a snapshot the project may have outgrown.
            var snapshot = await ReferenceAuditService.RefreshAsync(ReferenceAuditService.DefaultScope());

            ReferenceRepairPlan plan;
            switch (mode?.ToLowerInvariant())
            {
                case "redirect":
                    if (string.IsNullOrEmpty(siteKey) || string.IsNullOrEmpty(providerKey))
                        return Error("redirect requires both siteKey and providerKey.");
                    plan = ReferenceRepairPlanner.PlanRedirect(snapshot, siteKey, providerKey);
                    break;

                case "clear":
                    if (string.IsNullOrEmpty(siteKey))
                        return Error("clear requires siteKey.");
                    plan = ReferenceRepairPlanner.PlanClear(snapshot, siteKey);
                    break;

                default:
                    plan = ReferenceRepairPlanner.PlanSafeRepairs(snapshot);
                    break;
            }

            _pendingRepairPlan = plan;

            var result = new JObject
            {
                ["planId"] = plan.PlanId,
                ["auditRevision"] = plan.SourceAuditRevision,
                ["summary"] = plan.DescribeSummary(),
                ["isEmpty"] = plan.IsEmpty,
                ["isFullyAutomatic"] = plan.IsFullyAutomatic,
                ["reversibility"] = plan.Reversibility.ToString(),
                ["affectedAssets"] = new JArray(plan.AffectedAssetPaths),
                ["mutations"] = Mutations(plan),
                ["expectedResolved"] = Findings(plan.ExpectedResolvedFindings),
                ["expectedRemaining"] = Findings(plan.ExpectedRemainingFindings),
                ["warnings"] = new JArray(plan.Warnings),
                ["choices"] = Choices(ReferenceRepairPlanner.DescribeChoices(snapshot)),
                ["preview"] = plan.Preview(),
            };

            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// The <c>molca_references_apply_fix</c> tool: applies a named plan as one Undo group and reports
        /// what measurably changed.
        /// </summary>
        private static McpToolDefinition CreateReferencesApplyFixTool() => new McpToolDefinition(
            name: "molca_references_apply_fix",
            description: "Applies a plan from molca_references_plan_fix, by planId, as one Unity Undo group. "
                       + "Rejects the plan if the project changed since it was built. Reports the findings "
                       + "actually fixed, those remaining, and any the repair introduced. Revert with Ctrl+Z; "
                       + "asset files that had to be saved need version control instead.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"planId\":{\"type\":\"string\",\"description\":\"planId returned by molca_references_plan_fix.\"}," +
                "\"saveAssets\":{\"type\":\"boolean\"," +
                "\"description\":\"Save the assets whose changes need saving to persist (default true). " +
                "False applies in memory only and lists them as unsaved.\"}}," +
                "\"required\":[\"planId\"],\"additionalProperties\":false}",
            executeAsync: ExecuteReferencesApplyFixAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static async Awaitable<string> ExecuteReferencesApplyFixAsync(string argumentsJson)
        {
            string planId;
            var saveAssets = true;

            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                planId = args.Value<string>("planId");
                saveAssets = args["saveAssets"]?.Value<bool>() ?? true;
            }
            catch (Exception e)
            {
                return Error($"Could not parse arguments: {e.Message}");
            }

            if (string.IsNullOrEmpty(planId))
                return Error("planId is required. Call molca_references_plan_fix first.");

            var plan = _pendingRepairPlan;
            if (plan == null)
                return Error("No plan is pending. Call molca_references_plan_fix first.");

            // Applying by id rather than by "whatever was last planned" is the point: it guarantees the
            // change applied is the change that was reviewed.
            if (!string.Equals(plan.PlanId, planId, StringComparison.Ordinal))
            {
                return Error(
                    $"planId '{planId}' does not match the pending plan '{plan.PlanId}'. "
                    + "Re-run molca_references_plan_fix and apply the plan it returns.");
            }

            var outcome = await ReferenceRepairExecutor.ApplyAsync(plan, saveAssets);

            // A plan is single-use. Leaving it pending would let the same approval be replayed against a
            // project that the first apply already changed.
            _pendingRepairPlan = null;

            var result = new JObject
            {
                ["planId"] = outcome.PlanId,
                ["rejected"] = outcome.WasRejected,
                ["rejectionReason"] = outcome.RejectionReason,
                ["appliedCount"] = outcome.Applied.Count,
                ["applied"] = new JArray(outcome.Applied.Select(m => m.Describe())),
                ["skipped"] = new JArray(outcome.Skipped.Select(s => new JObject
                {
                    ["change"] = s.Mutation.Describe(),
                    ["reason"] = s.Reason,
                })),
                ["fixed"] = Findings(outcome.Fixed),
                ["remaining"] = Findings(outcome.Remaining),
                ["introduced"] = Findings(outcome.Introduced),
                ["savedAssets"] = new JArray(outcome.SavedAssets),
                ["undoGroup"] = outcome.UndoGroupName,
                ["stateAfter"] = outcome.SnapshotAfter?.State.ToString(),
                ["report"] = outcome.Describe(),
            };

            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        #region Projection helpers

        private static JArray Mutations(ReferenceRepairPlan plan)
        {
            var array = new JArray();
            foreach (var mutation in plan.Mutations)
            {
                var entry = new JObject
                {
                    ["kind"] = mutation.Kind.ToString(),
                    ["approval"] = mutation.Approval.ToString(),
                    ["asset"] = mutation.AssetPath,
                    ["object"] = mutation.Target.ObjectPath,
                    ["description"] = mutation.Describe(),
                    ["reason"] = mutation.Reason,
                    ["undoCovered"] = mutation.IsUndoCovered,
                    ["requiresSave"] = mutation.RequiresSave,
                    ["targetWritable"] = mutation.IsTargetWritable,
                };

                switch (mutation)
                {
                    case ReferenceSitePropertyMutation site:
                        entry["property"] = site.PropertyPath;
                        entry["previous"] = JObject.FromObject(site.PreviousValues);
                        entry["new"] = JObject.FromObject(site.NewValues);
                        break;

                    case ReferenceProviderIdMutation provider:
                        entry["previousRefId"] = provider.PreviousRefId;
                        entry["newRefId"] = provider.NewRefId;
                        entry["refType"] = provider.RefType;
                        break;
                }

                array.Add(entry);
            }

            return array;
        }

        private static JArray Choices(IReadOnlyList<ReferenceRepairChoice> choices)
        {
            var array = new JArray();
            foreach (var choice in choices)
            {
                array.Add(new JObject
                {
                    ["code"] = choice.Finding.CodeString,
                    ["severity"] = choice.Finding.Severity.ToString(),
                    ["asset"] = choice.Finding.AssetPath,
                    ["siteKey"] = choice.SiteKey,
                    ["question"] = choice.Question,
                    ["summary"] = choice.Finding.Summary,
                    ["candidateProviderKeys"] = new JArray(choice.CandidateProviderKeys),
                });
            }

            return array;
        }

        #endregion
    }
}
