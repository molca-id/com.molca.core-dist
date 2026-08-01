using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Doctor;

namespace Molca.Editor.Remediation
{
    /// <summary>Why a remediation pass did not fix a finding.</summary>
    /// <remarks>
    /// Every declined finding carries one of these plus a detail string. This vocabulary is the reason the
    /// button can be honest: a pass that fixes 6 of 20 findings must be able to say what the other 14 need.
    /// </remarks>
    public enum MolcaDeclineReason
    {
        /// <summary>No fix is registered for the finding code — it is a judgment finding, or none exists yet.</summary>
        NoFixExists,

        /// <summary>A fix exists but the active <see cref="RemediationPolicy"/> excludes it by facet.</summary>
        PolicyExcluded,

        /// <summary>A fix exists but needs caller arguments, so a blanket pass cannot run it.</summary>
        NotDeterministic,

        /// <summary>The correct result is not locally decidable — more than one candidate, or none.</summary>
        AmbiguousTarget,

        /// <summary>A remediation invariant forbids the repair (e.g. re-pointing a duplicated identity).</summary>
        BlockedByInvariant,

        /// <summary>The fix ran and reported that it changed nothing; its message is the detail.</summary>
        FixReportedNotApplied,

        /// <summary>The fix threw. Never silently swallowed — the exception message is the detail.</summary>
        FixThrew,

        /// <summary>The pass was cancelled before reaching this finding.</summary>
        Cancelled,

        /// <summary>
        /// The pass stopped at its iteration cap before reaching this finding — two fixes are re-creating
        /// each other's findings. Reported loudly rather than retried forever.
        /// </summary>
        NotConverged,

        /// <summary>The fix was excluded because the caller's explicit fix-id filter did not include it.</summary>
        NotRequested,
    }

    /// <summary>One finding a pass will not repair, with the reason a human can act on.</summary>
    public sealed class MolcaDeclinedFinding
    {
        /// <summary>Creates a declined entry.</summary>
        /// <param name="target">The finding site.</param>
        /// <param name="reason">Why it was declined.</param>
        /// <param name="detail">Human-readable specifics (candidate list, invariant name, fix message).</param>
        /// <param name="fixId">The fix that was considered, when one was.</param>
        public MolcaDeclinedFinding(
            MolcaFixTarget target, MolcaDeclineReason reason, string detail = null, string fixId = null)
        {
            Target = target;
            Reason = reason;
            Detail = detail;
            FixId = fixId;
        }

        /// <summary>The finding site.</summary>
        public MolcaFixTarget Target { get; }

        /// <summary>Why the pass declined it.</summary>
        public MolcaDeclineReason Reason { get; }

        /// <summary>Human-readable specifics; may be <c>null</c>.</summary>
        public string Detail { get; }

        /// <summary>The fix considered for this target, or <c>null</c> when none was registered.</summary>
        public string FixId { get; }
    }

    /// <summary>One fix a pass intends to apply (plan) or has applied (report).</summary>
    public sealed class MolcaPlannedFix
    {
        /// <summary>Creates a planned/applied entry.</summary>
        /// <param name="target">The finding site.</param>
        /// <param name="fix">The fix selected for it.</param>
        /// <param name="outcome">The dry-run preview outcome, or the real outcome once applied.</param>
        public MolcaPlannedFix(MolcaFixTarget target, IMolcaFix fix, MolcaFixOutcome outcome)
            : this(target, fix.Id, fix.Description, fix.Reversibility, fix.IsDestructive, outcome)
        {
        }

        /// <summary>
        /// Creates a row from explicit values, for a domain whose repair unit is not an
        /// <see cref="IMolcaFix"/>.
        /// </summary>
        /// <param name="target">The finding site.</param>
        /// <param name="fixId">Identifier of the repair, e.g. a reference mutation kind.</param>
        /// <param name="description">What the repair does.</param>
        /// <param name="reversibility">How the change reverts.</param>
        /// <param name="isDestructive">Whether the change discards authored data.</param>
        /// <param name="outcome">The preview or actual outcome.</param>
        /// <remarks>
        /// The reference system's plan-first transaction is the motivating case: its unit is a
        /// <c>ReferenceRepairMutation</c>, which is richer than a fix and must not be re-modelled as one just
        /// to be rendered.
        /// </remarks>
        public MolcaPlannedFix(
            MolcaFixTarget target,
            string fixId,
            string description,
            FixReversibility reversibility,
            bool isDestructive,
            MolcaFixOutcome outcome)
        {
            Target = target;
            FixId = fixId;
            Description = description;
            Reversibility = reversibility;
            IsDestructive = isDestructive;
            Outcome = outcome;
        }

        /// <summary>The finding site.</summary>
        public MolcaFixTarget Target { get; }

        /// <summary>The selected fix's id.</summary>
        public string FixId { get; }

        /// <summary>The selected fix's description.</summary>
        public string Description { get; }

        /// <summary>How the change reverts.</summary>
        public FixReversibility Reversibility { get; }

        /// <summary>Whether the fix discards authored data.</summary>
        public bool IsDestructive { get; }

        /// <summary>The preview (plan) or actual (report) outcome, including before/after.</summary>
        public MolcaFixOutcome Outcome { get; }
    }

    /// <summary>
    /// The previewed result of a remediation pass: what would be fixed, and what would be declined and why.
    /// </summary>
    /// <remarks>
    /// Produced by <see cref="MolcaRemediationPass.Plan"/> without mutating anything (every candidate fix is
    /// invoked in dry-run). A UI shows this before applying; for
    /// <see cref="RemediationPolicy.SafeOnly"/> the confirmation step may be skipped, but for any wider
    /// policy the plan must be confirmed.
    /// </remarks>
    public sealed class MolcaRemediationPlan
    {
        /// <summary>Creates a plan.</summary>
        /// <param name="domain">The audit domain key, e.g. <c>references</c>.</param>
        /// <param name="policy">The policy the plan was computed under.</param>
        /// <param name="fixable">Findings a pass would repair.</param>
        /// <param name="declined">Findings a pass would leave, each with a reason.</param>
        /// <param name="coverageNote">Coverage gap description when the snapshot was incomplete.</param>
        public MolcaRemediationPlan(
            string domain,
            RemediationPolicy policy,
            IReadOnlyList<MolcaPlannedFix> fixable,
            IReadOnlyList<MolcaDeclinedFinding> declined,
            string coverageNote = null)
        {
            Domain = domain;
            Policy = policy;
            Fixable = fixable ?? new List<MolcaPlannedFix>();
            Declined = declined ?? new List<MolcaDeclinedFinding>();
            CoverageNote = coverageNote;
        }

        /// <summary>The audit domain key.</summary>
        public string Domain { get; }

        /// <summary>The policy this plan was computed under.</summary>
        public RemediationPolicy Policy { get; }

        /// <summary>Findings the pass would repair.</summary>
        public IReadOnlyList<MolcaPlannedFix> Fixable { get; }

        /// <summary>Findings the pass would leave alone, each with a reason.</summary>
        public IReadOnlyList<MolcaDeclinedFinding> Declined { get; }

        /// <summary>
        /// Which categories the snapshot did not cover, or <c>null</c> when coverage was complete. Reported
        /// alongside results so "nothing left to fix" is never confused with "nothing was looked at".
        /// </summary>
        public string CoverageNote { get; }

        /// <summary>Total findings the plan accounted for.</summary>
        public int TotalFindings => Fixable.Count + Declined.Count;

        /// <summary>Fixable counts keyed by finding code, for a grouped UI.</summary>
        /// <returns>Applied-count per finding code.</returns>
        public Dictionary<string, int> FixableByCode() =>
            Fixable.GroupBy(f => f.Target.FindingCode)
                   .ToDictionary(g => g.Key, g => g.Count());

        /// <summary>Declined counts keyed by finding code, for a grouped UI.</summary>
        /// <returns>Declined-count per finding code.</returns>
        public Dictionary<string, int> DeclinedByCode() =>
            Declined.GroupBy(d => d.Target.FindingCode)
                    .ToDictionary(g => g.Key, g => g.Count());
    }
}
