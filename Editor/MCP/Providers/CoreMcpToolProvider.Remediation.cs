using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Remediation;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_remediation_plan</c> tool: previews what a remediation pass would repair across one
        /// or every audit domain, changing nothing.
        /// </summary>
        /// <remarks>
        /// Read-only by construction — <see cref="MolcaRemediationPass.Plan"/> invokes every candidate fix in
        /// dry-run. The declined list is returned in full rather than summarised, because an agent choosing
        /// what to do next needs the reasons, not a count.
        /// </remarks>
        private static McpToolDefinition CreateRemediationPlanTool() => new McpToolDefinition(
            name: "molca_remediation_plan",
            description: "Previews remediation for one audit domain, or every domain when none is named: "
                       + "which findings a pass would repair, and which it would decline and why. Changes "
                       + "nothing. Domains are project-wide audits (bootstrap, network, content, colorid); "
                       + "reference repair has its own plan/apply pair because it is a revision-pinned "
                       + "transaction, and sequence remediation targets one controller. Apply with "
                       + "molca_remediation_apply.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"domain\":{\"type\":\"string\",\"description\":\"Domain id (e.g. network). Omit for every domain.\"}," +
                "\"policy\":{\"type\":\"string\",\"enum\":[\"SafeOnly\",\"DeterministicReversible\",\"All\"]," +
                "\"description\":\"Which fixes to consider. SafeOnly (default) is deterministic, " +
                "non-destructive and Unity-Undo revertible.\"}}," +
                "\"additionalProperties\":false}",
            executeAsync: ExecuteRemediationPlanAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static
#pragma warning disable CS1998 // Planning is synchronous; the signature is the provider's async contract.
            async Awaitable<string> ExecuteRemediationPlanAsync(string argumentsJson)
#pragma warning restore CS1998
        {
            if (!TryReadRemediationArgs(argumentsJson, out var domains, out var policy, out var error))
                return error;

            var results = new JArray();
            foreach (var domain in domains)
            {
                MolcaRemediationPlan plan;
                try
                {
                    plan = MolcaRemediationPass.Plan(domain.CreateRequest(policy));
                }
                catch (Exception ex)
                {
                    results.Add(new JObject
                    {
                        ["domain"] = domain.Id,
                        ["error"] = $"Planning failed: {ex.Message}",
                    });
                    continue;
                }

                results.Add(new JObject
                {
                    ["domain"] = domain.Id,
                    ["label"] = domain.Label,
                    ["policy"] = plan.Policy.ToString(),
                    ["totalFindings"] = plan.TotalFindings,
                    ["coverageNote"] = plan.CoverageNote,
                    ["fixable"] = Fixes(plan.Fixable),
                    ["declined"] = Declines(plan.Declined),
                });
            }

            return new JObject
            {
                ["policy"] = policy.ToString(),
                ["domains"] = results,
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// The <c>molca_remediation_apply</c> tool: runs a remediation pass and reports every finding as
        /// applied or declined-with-reason.
        /// </summary>
        /// <remarks>
        /// One Unity Undo group per domain, so a caller can revert a single domain's pass. Fixes that revert
        /// by file snapshot are excluded from the default policy and surface their
        /// <c>McpUndoStack</c> entry ids when a wider policy is requested.
        /// </remarks>
        private static McpToolDefinition CreateRemediationApplyTool() => new McpToolDefinition(
            name: "molca_remediation_apply",
            description: "Applies remediation for one audit domain, or every domain when none is named, as "
                       + "one Unity Undo group per domain. Reports what was applied and what was declined, "
                       + "each with a reason. Defaults to the safe policy: deterministic, non-destructive, "
                       + "Ctrl+Z revertible. Pass dryRun to preview without writing.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"domain\":{\"type\":\"string\",\"description\":\"Domain id (e.g. network). Omit for every domain.\"}," +
                "\"policy\":{\"type\":\"string\",\"enum\":[\"SafeOnly\",\"DeterministicReversible\",\"All\"]," +
                "\"description\":\"Which fixes may auto-apply. SafeOnly is the default and the only policy " +
                "that should run unattended; wider policies include destructive and file-rewriting fixes.\"}," +
                "\"fixIds\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
                "\"description\":\"Restrict the pass to these fix ids. Use with a wider policy to apply a " +
                "reviewed subset.\"}," +
                "\"dryRun\":{\"type\":\"boolean\",\"description\":\"Preview only; identical to molca_remediation_plan.\"}}," +
                "\"additionalProperties\":false}",
            executeAsync: ExecuteRemediationApplyAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            // Declares the widest thing the tool can do, not the common case. Under the default SafeOnly
            // policy every fix reverts with Ctrl+Z, but a caller may pass a wider policy that runs
            // file-rewriting and asset-creating fixes. Understating that would tell a caller Ctrl+Z is
            // enough when it is not; overstating the ceremony only costs them an unnecessary
            // molca_undo_last_action. The report states the mechanisms actually used, per pass.
            reversibility: McpToolReversibility.FileSnapshot);

        private static
#pragma warning disable CS1998 // The pass is synchronous; the signature is the provider's async contract.
            async Awaitable<string> ExecuteRemediationApplyAsync(string argumentsJson)
#pragma warning restore CS1998
        {
            if (!TryReadRemediationArgs(argumentsJson, out var domains, out var policy, out var error))
                return error;

            IReadOnlyCollection<string> fixIds = null;
            var dryRun = false;
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                dryRun = args["dryRun"]?.Value<bool>() ?? false;
                var ids = args["fixIds"] as JArray;
                if (ids != null && ids.Count > 0)
                    fixIds = ids.Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            }
            catch (Exception ex)
            {
                return Error($"Could not parse arguments: {ex.Message}");
            }

            // A dry run is exactly a plan; routing it here rather than duplicating the preview keeps the two
            // tools from drifting apart in what they consider fixable.
            if (dryRun) return await ExecuteRemediationPlanAsync(argumentsJson);

            var results = new JArray();
            foreach (var domain in domains)
            {
                MolcaRemediationReport report;
                try
                {
                    var request = domain.CreateRequest(policy);
                    if (fixIds != null) request.FixIdFilter = fixIds;
                    report = MolcaRemediationPass.Apply(request);
                }
                catch (Exception ex)
                {
                    results.Add(new JObject
                    {
                        ["domain"] = domain.Id,
                        ["error"] = $"Remediation failed: {ex.Message}",
                    });
                    continue;
                }

                results.Add(new JObject
                {
                    ["domain"] = report.Domain,
                    ["label"] = domain.Label,
                    ["policy"] = report.Policy.ToString(),
                    ["summary"] = report.Summarize(),
                    ["appliedCount"] = report.Applied.Count,
                    ["applied"] = Fixes(report.Applied),
                    ["declined"] = Declines(report.Declined),
                    ["coverageNote"] = report.CoverageNote,
                    ["refusedStaleSnapshot"] = report.RefusedStaleSnapshot,
                    ["iterations"] = report.Iterations,
                    ["didNotConverge"] = report.HitIterationCap,
                    ["unconvergedCodes"] = new JArray(report.UnconvergedCodes),
                    ["requiresSceneReload"] = report.RequiresSceneReload,
                    ["revertMechanisms"] = new JArray(report.Mechanisms.Select(m => m.ToString())),
                    ["undoEntryIds"] = new JArray(report.UndoEntryIds),
                });
            }

            return new JObject
            {
                ["policy"] = policy.ToString(),
                ["domains"] = results,
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        #region Argument and projection helpers

        /// <summary>
        /// Resolves the requested domains and policy, or produces the error payload to return.
        /// </summary>
        /// <remarks>
        /// An unknown domain name is an error listing what does exist, rather than a silent empty run — the
        /// failure mode where a caller believes a sweep happened and nothing did.
        /// </remarks>
        private static bool TryReadRemediationArgs(
            string argumentsJson,
            out IReadOnlyList<MolcaRemediationDomain> domains,
            out RemediationPolicy policy,
            out string error)
        {
            domains = Array.Empty<MolcaRemediationDomain>();
            policy = RemediationPolicy.SafeOnly;
            error = null;

            string requested = null;
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                requested = args.Value<string>("domain");

                var policyText = args.Value<string>("policy");
                if (!string.IsNullOrWhiteSpace(policyText)
                    && !Enum.TryParse(policyText, ignoreCase: true, out policy))
                {
                    error = Error(
                        $"Unknown policy '{policyText}'. Expected SafeOnly, DeterministicReversible or All.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = Error($"Could not parse arguments: {ex.Message}");
                return false;
            }

            var all = MolcaRemediationDomains.All;
            if (string.IsNullOrWhiteSpace(requested))
            {
                domains = all;
                return true;
            }

            var match = all.FirstOrDefault(
                d => string.Equals(d.Id, requested, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                error = Error(
                    $"Unknown remediation domain '{requested}'. Registered domains: "
                    + (all.Count == 0 ? "(none)" : string.Join(", ", all.Select(d => d.Id))));
                return false;
            }

            domains = new[] { match };
            return true;
        }

        private static JArray Fixes(IReadOnlyList<MolcaPlannedFix> rows) => new JArray(rows.Select(row =>
            new JObject
            {
                ["code"] = row.Target.FindingCode,
                ["path"] = row.Target.Path,
                ["fixId"] = row.FixId,
                ["description"] = row.Description,
                ["message"] = row.Outcome.Message,
                ["before"] = row.Outcome.Before,
                ["after"] = row.Outcome.After,
                ["reverts"] = row.Reversibility.ToString(),
                ["destructive"] = row.IsDestructive,
            }));

        private static JArray Declines(IReadOnlyList<MolcaDeclinedFinding> rows) => new JArray(rows.Select(row =>
            new JObject
            {
                ["code"] = row.Target.FindingCode,
                ["path"] = row.Target.Path,
                ["reason"] = row.Reason.ToString(),
                ["detail"] = row.Detail,
                ["candidateFixId"] = row.FixId,
                ["message"] = row.Target.Message,
            }));

        #endregion
    }
}
