using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Remediation;

namespace Molca.Editor.ReferenceSystem.Repair
{
    /// <summary>
    /// Projects the reference system's plan-first repair transaction into the shared remediation plan/report
    /// shape, so one Hub affordance and one report layout serve every domain.
    /// </summary>
    /// <remarks>
    /// <para>Reference repair is deliberately <b>not</b> re-implemented as a set of
    /// <see cref="IMolcaFix"/> instances. <see cref="ReferenceRepairPlanner"/> →
    /// <see cref="ReferenceRepairPlan"/> → <see cref="ReferenceRepairExecutor"/> is already plan-first,
    /// revision-pinned and transactional — properties a per-finding fix contract cannot express. This class
    /// is a pure mapping: it renders that machinery in the unified vocabulary and never plans, mutates or
    /// re-orders anything.</para>
    /// <para>Because it is a projection, the invariants of the reference system survive untouched: a finding
    /// the planner refuses to touch appears as a declined row carrying the planner's own reason, and no
    /// duplicate id with inbound references is ever presented as fixable.</para>
    /// </remarks>
    public static class ReferenceRemediationProjection
    {
        /// <summary>The domain key used in reports and undo group names.</summary>
        public const string Domain = "references";

        /// <summary>The finding-code prefix reference findings are namespaced under.</summary>
        public const string CodePrefix = "reference.";

        /// <summary>Builds the unified finding code for a reference finding code.</summary>
        /// <param name="code">The reference finding code.</param>
        /// <returns>The namespaced code, e.g. <c>reference.DuplicateProvider</c>.</returns>
        public static string CodeFor(ReferenceFindingCode code) => CodePrefix + code;

        /// <summary>
        /// Projects one finding as a fix target. Used for reporting only — the reference domain's mutations
        /// are carried by its own plan, not by a fix registered against this code.
        /// </summary>
        /// <param name="finding">The finding to project.</param>
        /// <returns>The target.</returns>
        public static MolcaFixTarget ToTarget(ReferenceFinding finding)
        {
            if (finding == null) throw new ArgumentNullException(nameof(finding));
            return new MolcaFixTarget(
                CodeFor(finding.Code),
                string.IsNullOrEmpty(finding.AssetPath) ? finding.SourceSiteKey : finding.AssetPath,
                string.IsNullOrEmpty(finding.Summary) ? finding.Title : $"{finding.Title} — {finding.Summary}",
                finding.SourceSiteKey,
                finding);
        }

        /// <summary>
        /// Renders a repair plan as a <see cref="MolcaRemediationPlan"/>: the plan's automatic mutations
        /// become fixable rows, and every finding it leaves becomes a declined row with the reason.
        /// </summary>
        /// <param name="snapshot">The audit the plan was derived from.</param>
        /// <param name="plan">The repair plan to render.</param>
        /// <param name="isStale">Whether the audit service considers the snapshot stale.</param>
        /// <param name="coverageNote">Coverage gap description, or <c>null</c> when coverage is complete.</param>
        /// <returns>The projected plan.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="plan"/> is null.</exception>
        public static MolcaRemediationPlan ToPlan(
            ReferenceAuditSnapshot snapshot,
            ReferenceRepairPlan plan,
            bool isStale = false,
            string coverageNote = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var fixable = new List<MolcaPlannedFix>();
            var declined = new List<MolcaDeclinedFinding>();

            // A mutation, not a finding, is the unit the planner reasons about — several mutations can serve
            // one finding — so rows are keyed by mutation and attributed to the finding they resolve.
            var resolvedByCode = plan.ExpectedResolvedFindings
                .GroupBy(f => f.Code)
                .ToDictionary(g => g.Key, g => new Queue<ReferenceFinding>(g));

            foreach (var mutation in plan.Mutations)
            {
                var target = TargetFor(mutation, AttributeFinding(mutation, resolvedByCode));

                if (!mutation.IsTargetWritable)
                {
                    declined.Add(new MolcaDeclinedFinding(target, MolcaDeclineReason.BlockedByInvariant,
                        "The owning asset is read-only (package-owned or locked), so the change is skipped."));
                    continue;
                }

                if (mutation.Approval == ReferenceRepairApproval.RequiresUserChoice)
                {
                    declined.Add(new MolcaDeclinedFinding(target, MolcaDeclineReason.AmbiguousTarget,
                        mutation.Reason));
                    continue;
                }

                fixable.Add(RowFor(target, mutation, mutation.Describe()));
            }

            foreach (var finding in plan.ExpectedRemainingFindings)
                declined.Add(DeclineRemaining(finding, snapshot));

            return new MolcaRemediationPlan(
                Domain,
                RemediationPolicy.SafeOnly,
                isStale ? new List<MolcaPlannedFix>() : fixable,
                isStale ? PlanBlockedByStaleness(fixable, declined) : declined,
                coverageNote);
        }

        /// <summary>
        /// Renders an applied repair transaction as a <see cref="MolcaRemediationReport"/>.
        /// </summary>
        /// <param name="result">The executor's result.</param>
        /// <param name="coverageNote">Coverage gap description, or <c>null</c>.</param>
        /// <returns>The projected report.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="result"/> is null.</exception>
        public static MolcaRemediationReport ToReport(
            ReferenceRepairResult result, string coverageNote = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var report = new MolcaRemediationReport(Domain, RemediationPolicy.SafeOnly)
            {
                CoverageNote = coverageNote,
                Iterations = 1,
            };

            if (result.WasRejected)
            {
                // The executor rejects a plan whose revision has moved or whose audit went stale. That is the
                // same refusal the shared driver makes, so it maps onto the same reported state.
                report.RefusedStaleSnapshot = true;
                foreach (var finding in result.Remaining)
                    report.AddDeclined(new MolcaDeclinedFinding(
                        ToTarget(finding), MolcaDeclineReason.BlockedByInvariant, result.RejectionReason));
                return report;
            }

            var fixedByCode = result.Fixed
                .GroupBy(f => f.Code)
                .ToDictionary(g => g.Key, g => new Queue<ReferenceFinding>(g));

            foreach (var mutation in result.Applied)
            {
                var target = TargetFor(mutation, AttributeFinding(mutation, fixedByCode));
                report.AddApplied(RowFor(target, mutation, mutation.Describe()));
            }

            foreach (var skip in result.Skipped)
                report.AddDeclined(new MolcaDeclinedFinding(
                    TargetFor(skip.Mutation, null),
                    MolcaDeclineReason.FixReportedNotApplied,
                    skip.Reason));

            foreach (var finding in result.Remaining)
                report.AddDeclined(DeclineRemaining(finding, result.SnapshotAfter));

            // A repair that introduces a finding is the failure mode the transaction exists to expose. It is
            // reported as an outstanding decision, never folded into the applied count.
            foreach (var finding in result.Introduced)
                report.AddDeclined(new MolcaDeclinedFinding(
                    ToTarget(finding), MolcaDeclineReason.BlockedByInvariant,
                    $"Introduced by this repair: {finding.CodeString} {finding.Title}. Review it before continuing."));

            return report;
        }

        /// <summary>
        /// Builds the target for a mutation, preferring the finding it resolves so the row is labelled with
        /// the user-facing <c>REFnnn</c> code, and falling back to the mutation kind when the planner made a
        /// change no single finding owns.
        /// </summary>
        /// <remarks>
        /// The mutation's own description is used as the property path: several mutations can target one asset,
        /// and the description is what distinguishes them, so signatures stay unique per change.
        /// </remarks>
        private static MolcaFixTarget TargetFor(ReferenceRepairMutation mutation, ReferenceFinding finding)
            => finding != null
                ? new MolcaFixTarget(
                    CodeFor(finding.Code),
                    string.IsNullOrEmpty(finding.AssetPath) ? finding.SourceSiteKey : finding.AssetPath,
                    string.IsNullOrEmpty(finding.Summary) ? finding.Title : $"{finding.Title} — {finding.Summary}",
                    mutation.Describe(),
                    mutation)
                : new MolcaFixTarget(
                    CodePrefix + mutation.Kind, mutation.AssetPath, mutation.Reason,
                    mutation.Describe(), mutation);

        /// <summary>
        /// Renders one mutation as a plan/report row. Reversibility follows the mutation's own honesty about
        /// saved assets: a change that must be written to disk is not covered by Ctrl+Z alone.
        /// </summary>
        private static MolcaPlannedFix RowFor(
            MolcaFixTarget target, ReferenceRepairMutation mutation, string message)
            => new MolcaPlannedFix(
                target,
                $"{Domain}.{mutation.Kind}",
                mutation.Reason,
                mutation.RequiresSave
                    ? Molca.Editor.Doctor.FixReversibility.FileSnapshot
                    : Molca.Editor.Doctor.FixReversibility.UnityUndo,
                mutation.Kind == ReferenceRepairKind.ClearReference,
                new MolcaFixOutcome(true, message));

        private static IReadOnlyList<MolcaDeclinedFinding> PlanBlockedByStaleness(
            IReadOnlyList<MolcaPlannedFix> wouldBeFixable,
            IReadOnlyList<MolcaDeclinedFinding> alreadyDeclined)
        {
            var blocked = new List<MolcaDeclinedFinding>(alreadyDeclined);
            foreach (var row in wouldBeFixable)
                blocked.Add(new MolcaDeclinedFinding(row.Target, MolcaDeclineReason.BlockedByInvariant,
                    "The audit is stale; re-run it and rebuild the plan before applying anything."));
            return blocked;
        }

        private static MolcaDeclinedFinding DeclineRemaining(
            ReferenceFinding finding, ReferenceAuditSnapshot snapshot)
        {
            // "No fix exists" and "a fix exists but this asset cannot be written" send the user to entirely
            // different actions, so a finding the planner skipped purely because its provider is package-owned
            // must say so. The planner drops those silently, and only the snapshot records why.
            if (IsReadOnlyProvider(finding, snapshot))
                return new MolcaDeclinedFinding(ToTarget(finding), MolcaDeclineReason.BlockedByInvariant,
                    "The owning asset is read-only (package-owned or in a read-only layer), so nothing here "
                    + "can be rewritten. Move the object into project-owned content to repair it.");

            // The reason vocabulary mirrors the reference system's own rules rather than being re-derived:
            // these codes are runtime failures the resolver refuses to guess at, so no fix may exist.
            switch (finding.Code)
            {
                case ReferenceFindingCode.DuplicateProvider:
                case ReferenceFindingCode.AmbiguousLegacyFallback:
                case ReferenceFindingCode.WrongRuntimeType:
                case ReferenceFindingCode.MissingProvider:
                    return new MolcaDeclinedFinding(ToTarget(finding), MolcaDeclineReason.AmbiguousTarget,
                        string.IsNullOrEmpty(finding.Summary)
                            ? "The intended target is not recoverable from the data — choose it explicitly."
                            : finding.Summary);
                case ReferenceFindingCode.CoveragePartial:
                case ReferenceFindingCode.AssetScanFailed:
                case ReferenceFindingCode.SceneUnavailable:
                    return new MolcaDeclinedFinding(ToTarget(finding), MolcaDeclineReason.BlockedByInvariant,
                        string.IsNullOrEmpty(finding.Summary)
                            ? "Coverage evidence, not a repairable finding."
                            : finding.Summary);
                default:
                    return new MolcaDeclinedFinding(ToTarget(finding), MolcaDeclineReason.NoFixExists,
                        string.IsNullOrEmpty(finding.Summary)
                            ? "No automatic repair exists for this finding."
                            : finding.Summary);
            }
        }

        /// <summary>
        /// Whether every provider this finding names is read-only, which is why the planner produced no
        /// mutation for it.
        /// </summary>
        private static bool IsReadOnlyProvider(ReferenceFinding finding, ReferenceAuditSnapshot snapshot)
        {
            if (snapshot == null || finding.CandidateProviderKeys.Count == 0) return false;

            var providers = finding.CandidateProviderKeys
                .Select(snapshot.FindProvider)
                .Where(p => p != null)
                .ToList();
            return providers.Count > 0 && providers.All(p => p.IsReadOnly);
        }

        private static ReferenceFinding AttributeFinding(
            ReferenceRepairMutation mutation,
            Dictionary<ReferenceFindingCode, Queue<ReferenceFinding>> byCode)
        {
            var code = ExpectedCodeFor(mutation.Kind);
            return code.HasValue && byCode.TryGetValue(code.Value, out var queue) && queue.Count > 0
                ? queue.Dequeue()
                : null;
        }

        private static ReferenceFindingCode? ExpectedCodeFor(ReferenceRepairKind kind)
        {
            switch (kind)
            {
                case ReferenceRepairKind.AssignMissingProviderId:
                    return ReferenceFindingCode.ProviderIdMissing;
                case ReferenceRepairKind.RekeyUnreferencedDuplicate:
                    return ReferenceFindingCode.DuplicateProvider;
                case ReferenceRepairKind.RedirectReference:
                    return ReferenceFindingCode.MissingProvider;
                case ReferenceRepairKind.ClearReference:
                    return ReferenceFindingCode.MissingProvider;
                case ReferenceRepairKind.RefreshStaleMetadata:
                default:
                    // Stale metadata is raised under two codes; the projection does not guess which.
                    return null;
            }
        }
    }
}
