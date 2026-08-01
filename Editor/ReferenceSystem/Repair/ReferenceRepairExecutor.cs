using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem.Repair
{
    /// <summary>Why a planned mutation was not applied.</summary>
    public sealed class ReferenceRepairSkip
    {
        /// <summary>The mutation that was skipped.</summary>
        public ReferenceRepairMutation Mutation { get; }

        /// <summary>Why, in the user's terms.</summary>
        public string Reason { get; }

        internal ReferenceRepairSkip(ReferenceRepairMutation mutation, string reason)
        {
            Mutation = mutation;
            Reason = reason ?? string.Empty;
        }

        /// <inheritdoc/>
        public override string ToString() => $"{Mutation.Describe()} — {Reason}";
    }

    /// <summary>
    /// What actually happened when a plan was applied, verified against a fresh audit.
    /// </summary>
    /// <remarks>
    /// The result is measured, not predicted. A repair reports the findings that are <i>actually</i> gone,
    /// the ones that remain, and — the reason this matters — any that the repair itself introduced.
    /// </remarks>
    public sealed class ReferenceRepairResult
    {
        /// <summary>The plan that was applied.</summary>
        public string PlanId { get; }

        /// <summary>True when the plan was rejected outright and nothing was changed.</summary>
        public bool WasRejected { get; }

        /// <summary>Why the plan was rejected. Empty when it was applied.</summary>
        public string RejectionReason { get; }

        /// <summary>Mutations that were applied.</summary>
        public IReadOnlyList<ReferenceRepairMutation> Applied { get; }

        /// <summary>Mutations that were not applied, with reasons.</summary>
        public IReadOnlyList<ReferenceRepairSkip> Skipped { get; }

        /// <summary>Findings present before and absent after.</summary>
        public IReadOnlyList<ReferenceFinding> Fixed { get; }

        /// <summary>Findings still present after.</summary>
        public IReadOnlyList<ReferenceFinding> Remaining { get; }

        /// <summary>Findings absent before and present after — a repair that made things worse.</summary>
        public IReadOnlyList<ReferenceFinding> Introduced { get; }

        /// <summary>The audit taken after applying, or null when the plan was rejected.</summary>
        public ReferenceAuditSnapshot SnapshotAfter { get; }

        /// <summary>Undo group name, for telling the user what Ctrl+Z will take back.</summary>
        public string UndoGroupName { get; }

        /// <summary>Assets saved to disk, which Undo cannot restore.</summary>
        public IReadOnlyList<string> SavedAssets { get; }

        internal ReferenceRepairResult(
            string planId,
            bool wasRejected,
            string rejectionReason,
            IReadOnlyList<ReferenceRepairMutation> applied,
            IReadOnlyList<ReferenceRepairSkip> skipped,
            IReadOnlyList<ReferenceFinding> fixedFindings,
            IReadOnlyList<ReferenceFinding> remaining,
            IReadOnlyList<ReferenceFinding> introduced,
            ReferenceAuditSnapshot snapshotAfter,
            string undoGroupName,
            IReadOnlyList<string> savedAssets)
        {
            PlanId = planId;
            WasRejected = wasRejected;
            RejectionReason = rejectionReason ?? string.Empty;

            // Normalized to List so every collection on a result has the same concrete shape whether the
            // plan was applied or rejected. Reflection-based inspectors look for `Count`, which an array
            // does not have, and a result that behaves differently depending on its outcome is a trap.
            Applied = (applied ?? Array.Empty<ReferenceRepairMutation>()).ToList();
            Skipped = (skipped ?? Array.Empty<ReferenceRepairSkip>()).ToList();
            Fixed = (fixedFindings ?? Array.Empty<ReferenceFinding>()).ToList();
            Remaining = (remaining ?? Array.Empty<ReferenceFinding>()).ToList();
            Introduced = (introduced ?? Array.Empty<ReferenceFinding>()).ToList();
            SnapshotAfter = snapshotAfter;
            UndoGroupName = undoGroupName ?? string.Empty;
            SavedAssets = (savedAssets ?? Array.Empty<string>()).ToList();
        }

        internal static ReferenceRepairResult Rejected(string planId, string reason) =>
            new ReferenceRepairResult(
                planId, wasRejected: true, reason, null, null, null, null, null, null, null, null);

        /// <summary>Full human-readable report of what happened.</summary>
        public string Describe()
        {
            if (WasRejected)
                return $"[ReferenceRepair] Plan {PlanId} rejected; nothing was changed. {RejectionReason}";

            var report = new StringBuilder();
            report.AppendLine($"[ReferenceRepair] Plan {PlanId}: applied {Applied.Count} change(s).");

            if (Applied.Count > 0)
            {
                foreach (var mutation in Applied)
                    report.AppendLine($"  ✓ {mutation.Describe()}");
            }

            if (Skipped.Count > 0)
            {
                report.AppendLine($"Skipped {Skipped.Count}:");
                foreach (var skip in Skipped)
                    report.AppendLine($"  – {skip}");
            }

            report.AppendLine($"Fixed {Fixed.Count} finding(s); {Remaining.Count} remain.");

            if (Introduced.Count > 0)
            {
                report.AppendLine($"WARNING — this repair introduced {Introduced.Count} new finding(s):");
                foreach (var finding in Introduced)
                    report.AppendLine($"  ! {finding.ToMessage()}");
            }

            if (SavedAssets.Count > 0)
            {
                report.AppendLine(
                    $"Saved {SavedAssets.Count} asset(s) to disk; Undo will not restore those files. "
                    + $"Use version control: {string.Join(", ", SavedAssets)}");
            }

            if (!string.IsNullOrEmpty(UndoGroupName))
                report.AppendLine($"Undo with Ctrl+Z (\"{UndoGroupName}\").");

            return report.ToString();
        }

        /// <inheritdoc/>
        public override string ToString() => Describe();
    }

    /// <summary>
    /// Applies a <see cref="ReferenceRepairPlan"/> as a transaction: verify, apply, re-audit, report.
    /// </summary>
    /// <remarks>
    /// The order matters and is the whole design. Preconditions are checked against the <i>live</i> project
    /// before anything is written, so a plan built against data that has since moved is rejected instead of
    /// applied to something the user never reviewed. Changes then go through one Undo group. Afterwards the
    /// project is re-audited and the result is compared to the audit taken before, so the report states what
    /// measurably changed rather than what was intended — including any finding the repair introduced.
    /// </remarks>
    public static class ReferenceRepairExecutor
    {
        /// <summary>
        /// Applies <paramref name="plan"/>.
        /// </summary>
        /// <param name="plan">The plan to apply.</param>
        /// <param name="saveAssets">
        /// Whether to save the assets whose changes need saving to persist. False applies the in-memory
        /// change and lists the assets as unsaved, leaving the decision with the caller.
        /// </param>
        /// <param name="progress">Optional progress sink for the re-audit.</param>
        /// <param name="cancellationToken">
        /// Cancels the <i>re-audit</i>. Application itself is not cancellable: abandoning it halfway would
        /// leave the project in a state no plan describes.
        /// </param>
        /// <returns>What happened. Never null.</returns>
        public static async Awaitable<ReferenceRepairResult> ApplyAsync(
            ReferenceRepairPlan plan,
            bool saveAssets = true,
            Action<string, float> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));

            if (plan.IsEmpty)
                return ReferenceRepairResult.Rejected(plan.PlanId, "The plan contains no changes.");

            // 1. The plan must describe the project as it is now. ReferenceAuditService.Current is the
            //    snapshot the plan was built from unless something has changed since.
            var before = ReferenceAuditService.Current;
            if (before.Revision != plan.SourceAuditRevision)
            {
                return ReferenceRepairResult.Rejected(
                    plan.PlanId,
                    $"The plan was built from audit revision {plan.SourceAuditRevision} but the current "
                    + $"revision is {before.Revision}. Re-run the audit and rebuild the plan so you approve "
                    + "changes to the project as it actually is.");
            }

            if (ReferenceAuditService.IsStale)
            {
                return ReferenceRepairResult.Rejected(
                    plan.PlanId,
                    $"The audit this plan was built from is stale ({ReferenceAuditService.StaleReason}). "
                    + "Re-run the audit and rebuild the plan.");
            }

            // 2. Resolve every target and verify every precondition before writing anything.
            var resolved = new List<(ReferenceRepairMutation Mutation, UnityEngine.Object Target)>();
            var skipped = new List<ReferenceRepairSkip>();

            foreach (var mutation in plan.Mutations)
            {
                if (!mutation.IsTargetWritable)
                {
                    skipped.Add(new ReferenceRepairSkip(mutation, "the target asset is read-only"));
                    continue;
                }

                var target = mutation.Target.TryResolve();
                if (target == null)
                {
                    skipped.Add(new ReferenceRepairSkip(
                        mutation, "the target object could not be resolved; its scene may be closed"));
                    continue;
                }

                if (!mutation.VerifyPrecondition(target, out var failure))
                {
                    skipped.Add(new ReferenceRepairSkip(mutation, failure));
                    continue;
                }

                resolved.Add((mutation, target));
            }

            if (resolved.Count == 0)
            {
                return new ReferenceRepairResult(
                    plan.PlanId, false, string.Empty,
                    Array.Empty<ReferenceRepairMutation>(), skipped,
                    Array.Empty<ReferenceFinding>(), before.Findings, Array.Empty<ReferenceFinding>(),
                    before, string.Empty, Array.Empty<string>());
            }

            // 3. One Undo group for everything, so the user takes back a repair rather than a mutation.
            var undoGroupName = $"Molca reference repair ({plan.PlanId})";
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(undoGroupName);
            var undoGroup = Undo.GetCurrentGroup();

            var applied = new List<ReferenceRepairMutation>();
            foreach (var (mutation, target) in resolved)
            {
                Undo.RecordObject(target, undoGroupName);

                if (mutation.TryApply(target, out var failure))
                    applied.Add(mutation);
                else
                    skipped.Add(new ReferenceRepairSkip(mutation, failure));
            }

            Undo.CollapseUndoOperations(undoGroup);

            // 4. Save only the assets the plan said needed saving, and only those.
            var savedAssets = new List<string>();
            if (saveAssets)
            {
                foreach (var (mutation, target) in resolved.Where(r => applied.Contains(r.Mutation) && r.Mutation.RequiresSave))
                {
                    try
                    {
                        AssetDatabase.SaveAssetIfDirty(target);
                        if (!string.IsNullOrEmpty(mutation.AssetPath))
                            savedAssets.Add(mutation.AssetPath);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning(
                            $"[ReferenceRepair] Could not save '{mutation.AssetPath}': {e.Message}. "
                            + "The change is applied in memory but not persisted.");
                    }
                }
            }

            // 5. Re-audit and compare. The report is measured against the project, not asserted.
            ReferenceAuditService.Invalidate($"reference repair {plan.PlanId} changed {applied.Count} object(s)");
            var after = await ReferenceAuditService.RefreshAsync(
                before.Scope, progress, cancellationToken);

            var beforeIdentities = Identities(before.Findings);
            var afterIdentities = Identities(after.Findings);

            var fixedFindings = before.Findings
                .Where(f => !afterIdentities.Contains(ReferenceRepairPlanner.FindingIdentity(f)))
                .ToList();
            var introduced = after.Findings
                .Where(f => !beforeIdentities.Contains(ReferenceRepairPlanner.FindingIdentity(f)))
                .ToList();

            return new ReferenceRepairResult(
                plan.PlanId, false, string.Empty,
                applied, skipped,
                fixedFindings, after.Findings, introduced,
                after,
                applied.Count > 0 ? undoGroupName : string.Empty,
                savedAssets.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList());
        }

        private static HashSet<string> Identities(IEnumerable<ReferenceFinding> findings) =>
            new HashSet<string>(
                findings.Select(ReferenceRepairPlanner.FindingIdentity), StringComparer.Ordinal);
    }
}
