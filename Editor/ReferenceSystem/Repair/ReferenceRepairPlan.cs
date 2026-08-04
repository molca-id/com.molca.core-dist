using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Molca.Editor.ReferenceSystem.Repair
{
    /// <summary>How much of a plan can be taken back after it is applied.</summary>
    public enum ReferenceRepairReversibility
    {
        /// <summary>Every change is in memory; Ctrl+Z restores all of it.</summary>
        FullyUndoable = 0,

        /// <summary>
        /// Some changes touch assets that must be saved, so Undo restores the in-memory value but the file
        /// on disk has already changed and only version control can restore it.
        /// </summary>
        UndoableExceptSavedAssets = 1,

        /// <summary>Nothing to undo.</summary>
        Empty = 2,
    }

    /// <summary>
    /// An exact, reviewable set of changes derived from one audit snapshot.
    /// </summary>
    /// <remarks>
    /// <para>A plan is the unit of repair, and it is inert: building one changes nothing. It records the
    /// snapshot revision it was derived from, so applying it against a project that has moved on is rejected
    /// rather than silently applied to different data.</para>
    ///
    /// <para>This exists because the previous "fix" paths were unreviewable by construction. A scan would
    /// reassign ids and then offer a blanket string rewrite, with the user's only information being a count.
    /// Here the user sees every object, every property, and every before/after value first.</para>
    /// </remarks>
    public sealed class ReferenceRepairPlan
    {
        /// <summary>Identifier for this plan, so an apply call can name the plan it approved.</summary>
        public string PlanId { get; }

        /// <summary>
        /// <see cref="ReferenceAuditSnapshot.Revision"/> this plan was derived from. Applying against a
        /// different revision is rejected.
        /// </summary>
        public long SourceAuditRevision { get; }

        /// <summary>Every change the plan would make, in a stable order.</summary>
        public IReadOnlyList<ReferenceRepairMutation> Mutations { get; }

        /// <summary>Findings this plan is expected to resolve, by <c>REFnnn</c> code and summary.</summary>
        public IReadOnlyList<ReferenceFinding> ExpectedResolvedFindings { get; }

        /// <summary>Findings this plan deliberately does not address.</summary>
        public IReadOnlyList<ReferenceFinding> ExpectedRemainingFindings { get; }

        /// <summary>Things the user should know before approving, e.g. non-Undo asset saves.</summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>How much of this plan can be taken back.</summary>
        public ReferenceRepairReversibility Reversibility { get; }

        /// <summary>Assets this plan would change, de-duplicated and ordered.</summary>
        public IReadOnlyList<string> AffectedAssetPaths { get; }

        /// <summary>True when there is nothing to do.</summary>
        public bool IsEmpty => Mutations.Count == 0;

        /// <summary>True when every mutation may be applied without a further decision.</summary>
        public bool IsFullyAutomatic =>
            Mutations.All(m => m.Approval == ReferenceRepairApproval.Automatic);

        /// <summary>Mutations whose target asset cannot be written.</summary>
        public IReadOnlyList<ReferenceRepairMutation> NonWritableMutations { get; }

        internal ReferenceRepairPlan(
            long sourceAuditRevision,
            IReadOnlyList<ReferenceRepairMutation> mutations,
            IReadOnlyList<ReferenceFinding> expectedResolvedFindings,
            IReadOnlyList<ReferenceFinding> expectedRemainingFindings,
            IReadOnlyList<string> warnings)
        {
            SourceAuditRevision = sourceAuditRevision;

            // Normalized to List rather than kept as whatever the caller passed, so every consumer sees the
            // same concrete shape. Reflection-based inspectors and assertion libraries look for `Count`,
            // which an array does not have.
            Mutations = (mutations ?? Array.Empty<ReferenceRepairMutation>()).ToList();
            ExpectedResolvedFindings = (expectedResolvedFindings ?? Array.Empty<ReferenceFinding>()).ToList();
            ExpectedRemainingFindings = (expectedRemainingFindings ?? Array.Empty<ReferenceFinding>()).ToList();

            NonWritableMutations = Mutations.Where(m => !m.IsTargetWritable).ToList();

            var allWarnings = new List<string>(warnings ?? Array.Empty<string>());

            var savedAssets = Mutations.Where(m => m.RequiresSave).Select(m => m.AssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            if (savedAssets.Count > 0)
            {
                allWarnings.Add(
                    $"{savedAssets.Count} asset(s) must be saved to persist these changes. Undo restores the "
                    + "in-memory value, but the file on disk will already have changed — use version control "
                    + $"to recover those: {string.Join(", ", savedAssets)}");
            }

            if (NonWritableMutations.Count > 0)
            {
                allWarnings.Add(
                    $"{NonWritableMutations.Count} change(s) target read-only assets and will be skipped.");
            }

            Warnings = allWarnings;

            Reversibility = Mutations.Count == 0
                ? ReferenceRepairReversibility.Empty
                : savedAssets.Count > 0
                    ? ReferenceRepairReversibility.UndoableExceptSavedAssets
                    : ReferenceRepairReversibility.FullyUndoable;

            AffectedAssetPaths = Mutations
                .Select(m => m.AssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            // Derived from the content so the same plan built twice has the same id — which lets an
            // approve-then-apply round trip verify it is applying the plan that was shown.
            PlanId = BuildPlanId(sourceAuditRevision, Mutations);
        }

        /// <summary>An empty plan, for "nothing to repair".</summary>
        /// <param name="sourceAuditRevision">The snapshot revision the (empty) plan was derived from.</param>
        public static ReferenceRepairPlan Empty(long sourceAuditRevision) =>
            new ReferenceRepairPlan(
                sourceAuditRevision,
                Array.Empty<ReferenceRepairMutation>(),
                Array.Empty<ReferenceFinding>(),
                Array.Empty<ReferenceFinding>(),
                Array.Empty<string>());

        /// <summary>
        /// The full human-readable preview: what changes, what it fixes, what it leaves, what to watch out
        /// for.
        /// </summary>
        public string Preview()
        {
            var preview = new StringBuilder();
            preview.AppendLine($"Reference repair plan {PlanId} (audit revision {SourceAuditRevision})");
            preview.AppendLine($"Reversibility: {Reversibility}");
            preview.AppendLine();

            if (IsEmpty)
            {
                preview.AppendLine("No change can be made without guessing which target was intended.");

                // A refusal states its reason here or nowhere. An empty plan built by a refusal carries the
                // explanation in Warnings, and returning before printing them left the one thing the user
                // needed — why this was refused — visible only to a debugger.
                foreach (var warning in Warnings)
                    preview.AppendLine($"  ! {warning}");

                return preview.ToString();
            }

            preview.AppendLine($"Changes ({Mutations.Count}):");
            foreach (var group in Mutations.GroupBy(m => m.Kind))
            {
                preview.AppendLine($"  {group.Key}:");
                foreach (var mutation in group)
                {
                    preview.AppendLine($"    • {mutation.Describe()}");
                    preview.AppendLine($"      {mutation.Reason}");
                }
            }

            if (ExpectedResolvedFindings.Count > 0)
            {
                preview.AppendLine();
                preview.AppendLine($"Expected to resolve ({ExpectedResolvedFindings.Count}):");
                foreach (var finding in ExpectedResolvedFindings)
                    preview.AppendLine($"  • {finding.CodeString} {finding.Title}");
            }

            if (ExpectedRemainingFindings.Count > 0)
            {
                preview.AppendLine();
                preview.AppendLine($"Left for you to decide ({ExpectedRemainingFindings.Count}):");
                foreach (var finding in ExpectedRemainingFindings)
                    preview.AppendLine($"  • {finding.CodeString} {finding.Title} — {finding.Summary}");
            }

            if (Warnings.Count > 0)
            {
                preview.AppendLine();
                preview.AppendLine("Before you approve:");
                foreach (var warning in Warnings)
                    preview.AppendLine($"  ! {warning}");
            }

            return preview.ToString();
        }

        /// <summary>Short one-line summary for a dialog title or a log line.</summary>
        public string DescribeSummary() =>
            IsEmpty
                ? "no automatic repair is possible"
                : $"{Mutations.Count} change(s) across {AffectedAssetPaths.Count} asset(s), "
                  + $"resolving {ExpectedResolvedFindings.Count} finding(s)";

        private static string BuildPlanId(long revision, IReadOnlyList<ReferenceRepairMutation> mutations)
        {
            unchecked
            {
                var hash = 17L;
                hash = hash * 31 + revision;
                foreach (var mutation in mutations)
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(mutation.Describe());
                return $"plan-{revision}-{Math.Abs(hash) % 0xFFFFFF:x6}";
            }
        }

        /// <inheritdoc/>
        public override string ToString() => $"{PlanId}: {DescribeSummary()}";
    }
}
