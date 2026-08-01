using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Doctor;

namespace Molca.Editor.Remediation
{
    /// <summary>
    /// What a remediation pass actually did: every finding accounted for as applied or declined-with-reason.
    /// </summary>
    /// <remarks>
    /// The report <b>is the product</b> of the "Fix Safe Issues" affordance, not a footnote. A pass that
    /// repairs 6 of 20 findings and reports only "done" is worse than no pass at all, so
    /// <see cref="MolcaRemediationPass"/> guarantees every finding it saw appears exactly once across
    /// <see cref="Applied"/> and <see cref="Declined"/>.
    /// </remarks>
    public sealed class MolcaRemediationReport
    {
        private readonly List<MolcaPlannedFix> _applied = new List<MolcaPlannedFix>();
        private readonly List<MolcaDeclinedFinding> _declined = new List<MolcaDeclinedFinding>();
        private readonly HashSet<FixReversibility> _mechanisms = new HashSet<FixReversibility>();
        private readonly List<string> _undoEntryIds = new List<string>();

        /// <summary>Creates an empty report for a domain and policy.</summary>
        /// <param name="domain">The audit domain key, e.g. <c>references</c>.</param>
        /// <param name="policy">The policy the pass ran under.</param>
        public MolcaRemediationReport(string domain, RemediationPolicy policy)
        {
            Domain = domain;
            Policy = policy;
        }

        /// <summary>The audit domain key.</summary>
        public string Domain { get; }

        /// <summary>The policy the pass ran under.</summary>
        public RemediationPolicy Policy { get; }

        /// <summary>Fixes that changed something, in application order.</summary>
        public IReadOnlyList<MolcaPlannedFix> Applied => _applied;

        /// <summary>Findings left alone, each with a reason a human can act on.</summary>
        public IReadOnlyList<MolcaDeclinedFinding> Declined => _declined;

        /// <summary>The revert mechanisms actually used, so revert guidance is honest.</summary>
        public IReadOnlyCollection<FixReversibility> Mechanisms => _mechanisms;

        /// <summary>
        /// <c>McpUndoStack</c> entry ids created by file-snapshot fixes, in creation order — reverting the
        /// pass means undoing these in reverse in addition to one Unity Undo.
        /// </summary>
        public IReadOnlyList<string> UndoEntryIds => _undoEntryIds;

        /// <summary>Whether any applied fix needs a scene reload before its effect is visible.</summary>
        public bool RequiresSceneReload { get; private set; }

        /// <summary>Coverage gap description when the snapshot was incomplete; otherwise <c>null</c>.</summary>
        public string CoverageNote { get; internal set; }

        /// <summary>Whether the pass refused to run because the domain snapshot was stale.</summary>
        public bool RefusedStaleSnapshot { get; internal set; }

        /// <summary>
        /// Set when the domain's audit threw. The pass stops, but whatever was already applied stays in
        /// <see cref="Applied"/> — a mutated project with no record of the change is the worse outcome.
        /// </summary>
        public string AuditError { get; internal set; }

        /// <summary>How many audit → fix iterations ran before the pass reached a fixpoint.</summary>
        public int Iterations { get; internal set; }

        /// <summary>
        /// True when the pass stopped at <see cref="MolcaRemediationPass.MaxIterations"/> with eligible
        /// findings still outstanding — a fix pair that keeps re-creating each other's findings. Fails loudly
        /// rather than spinning.
        /// </summary>
        public bool HitIterationCap { get; internal set; }

        /// <summary>The finding codes still eligible when the iteration cap was hit; empty otherwise.</summary>
        public IReadOnlyList<string> UnconvergedCodes { get; internal set; } = new List<string>();

        /// <summary>Total findings accounted for.</summary>
        public int TotalAccounted => _applied.Count + _declined.Count;

        /// <summary>Applied counts keyed by finding code.</summary>
        /// <returns>Applied-count per finding code.</returns>
        public Dictionary<string, int> AppliedByCode() =>
            _applied.GroupBy(a => a.Target.FindingCode).ToDictionary(g => g.Key, g => g.Count());

        /// <summary>Declined counts keyed by finding code.</summary>
        /// <returns>Declined-count per finding code.</returns>
        public Dictionary<string, int> DeclinedByCode() =>
            _declined.GroupBy(d => d.Target.FindingCode).ToDictionary(g => g.Key, g => g.Count());

        /// <summary>Records an applied fix.</summary>
        /// <param name="entry">The applied fix and its outcome.</param>
        internal void AddApplied(MolcaPlannedFix entry)
        {
            _applied.Add(entry);
            _mechanisms.Add(entry.Reversibility);
            if (entry.Outcome.RequiresSceneReload) RequiresSceneReload = true;
            if (!string.IsNullOrEmpty(entry.Outcome.UndoEntryId)) _undoEntryIds.Add(entry.Outcome.UndoEntryId);
        }

        /// <summary>Records a declined finding.</summary>
        /// <param name="entry">The declined finding and its reason.</param>
        internal void AddDeclined(MolcaDeclinedFinding entry) => _declined.Add(entry);

        /// <summary>A one-line human summary, e.g. <c>"4 applied · 8 need review"</c>.</summary>
        /// <returns>Summary text suitable for a header or console line.</returns>
        public string Summarize()
        {
            var text = $"{_applied.Count} applied · {_declined.Count} need review";
            if (RefusedStaleSnapshot) text += " · refused: stale snapshot";
            if (!string.IsNullOrEmpty(AuditError)) text += $" · audit failed: {AuditError}";
            if (HitIterationCap) text += $" · did not converge ({string.Join(", ", UnconvergedCodes)})";
            if (!string.IsNullOrEmpty(CoverageNote)) text += $" · coverage: {CoverageNote}";
            return text;
        }
    }
}
