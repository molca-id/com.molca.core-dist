using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.ReferenceSystem.Repair;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// Answers "can this finding be repaired, and does it need me to decide?" for every finding in one
    /// snapshot, by asking the repair planner once instead of once per row.
    /// </summary>
    /// <remarks>
    /// The index is a projection of the planner, never a second opinion about what is repairable. The whole
    /// reason repair availability is shown in the table is so the user can see which findings the safe batch
    /// will actually touch <i>before</i> approving a plan; if this class decided that for itself, the column
    /// and the plan could disagree, and the column is the more visible of the two.
    ///
    /// Built per snapshot revision and thrown away when a new snapshot arrives.
    /// </remarks>
    public sealed class ReferenceHubRepairIndex
    {
        private readonly Dictionary<string, ReferenceHubRepairAvailability> _byFinding;

        /// <summary>The audit revision this index was built from.</summary>
        public long SourceAuditRevision { get; }

        /// <summary>Number of findings the safe batch would repair without asking anything.</summary>
        public int AutomaticCount { get; }

        /// <summary>Open decisions, most severe first, exactly as the planner describes them.</summary>
        public IReadOnlyList<ReferenceRepairChoice> Choices { get; }

        private ReferenceHubRepairIndex(
            long revision,
            Dictionary<string, ReferenceHubRepairAvailability> byFinding,
            IReadOnlyList<ReferenceRepairChoice> choices,
            int automaticCount)
        {
            SourceAuditRevision = revision;
            _byFinding = byFinding;
            Choices = choices ?? Array.Empty<ReferenceRepairChoice>();
            AutomaticCount = automaticCount;
        }

        /// <summary>An index over nothing, used before the first audit.</summary>
        public static ReferenceHubRepairIndex Empty { get; } = new ReferenceHubRepairIndex(
            0, new Dictionary<string, ReferenceHubRepairAvailability>(StringComparer.Ordinal), null, 0);

        /// <summary>
        /// Builds the index for <paramref name="snapshot"/>.
        /// </summary>
        /// <param name="snapshot">The audit to plan against. Null yields <see cref="Empty"/>.</param>
        /// <returns>An index; never null.</returns>
        public static ReferenceHubRepairIndex Build(ReferenceAuditSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Findings.Count == 0)
                return Empty;

            var map = new Dictionary<string, ReferenceHubRepairAvailability>(StringComparer.Ordinal);

            var plan = ReferenceRepairPlanner.PlanSafeRepairs(snapshot);
            foreach (var finding in plan.ExpectedResolvedFindings)
                map[ReferenceRepairPlanner.FindingIdentity(finding)] = ReferenceHubRepairAvailability.Automatic;

            var choices = ReferenceRepairPlanner.DescribeChoices(snapshot);
            foreach (var choice in choices)
            {
                var key = ReferenceRepairPlanner.FindingIdentity(choice.Finding);

                // A finding the safe batch already covers is not also an open decision. The planner can
                // report both for one code — an unreferenced duplicate is automatic, a referenced one is a
                // choice — and Automatic is the more useful of the two answers to show.
                if (!map.ContainsKey(key))
                    map[key] = ReferenceHubRepairAvailability.RequiresChoice;
            }

            return new ReferenceHubRepairIndex(
                snapshot.Revision, map, choices,
                map.Values.Count(v => v == ReferenceHubRepairAvailability.Automatic));
        }

        /// <summary>Repair availability for one finding.</summary>
        /// <param name="finding">The finding to look up. Null returns <c>None</c>.</param>
        public ReferenceHubRepairAvailability For(ReferenceFinding finding) =>
            finding != null && _byFinding.TryGetValue(ReferenceRepairPlanner.FindingIdentity(finding), out var a)
                ? a
                : ReferenceHubRepairAvailability.None;

        /// <summary>
        /// One-line summary for the repair card, e.g. <c>3 automatic, 2 need a decision</c>.
        /// </summary>
        public string Describe()
        {
            if (AutomaticCount == 0 && Choices.Count == 0)
                return "nothing repairable";

            var parts = new List<string>();
            if (AutomaticCount > 0)
                parts.Add($"{AutomaticCount} automatic");
            if (Choices.Count > 0)
                parts.Add($"{Choices.Count} need{(Choices.Count == 1 ? "s" : "")} a decision");
            return string.Join(", ", parts);
        }
    }
}
