using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>Whether the audit actually looked at a category of input.</summary>
    public enum ReferenceCoverageStatus
    {
        /// <summary>Fully scanned. Findings for this category are authoritative.</summary>
        Scanned = 0,

        /// <summary>Deliberately not scanned (configuration or scope). Findings cannot be complete.</summary>
        Skipped = 1,

        /// <summary>Scanning was attempted and failed. Treat as unknown, never as clean.</summary>
        Failed = 2,
    }

    /// <summary>
    /// What the audit did — or did not — look at, for one category of input.
    /// </summary>
    /// <remarks>
    /// Coverage is a first-class result, not a log line. Before this existed, "no findings" could mean
    /// "the project is healthy" or "prefab scanning was switched off and no build scenes were enabled",
    /// and the two were indistinguishable.
    /// </remarks>
    public sealed class ReferenceCoverageEntry
    {
        /// <summary>Category name, e.g. <c>Scenes (open)</c> or <c>Prefab assets</c>.</summary>
        public string Category { get; }

        /// <summary>Whether the category was scanned.</summary>
        public ReferenceCoverageStatus Status { get; }

        /// <summary>Number of assets or objects covered.</summary>
        public int Count { get; }

        /// <summary>Why the category was skipped or failed. Empty when scanned.</summary>
        public string Reason { get; }

        /// <summary>
        /// True when this category must be complete before the project can be called clean. A skipped
        /// optional category is reported but does not by itself downgrade the audit.
        /// </summary>
        public bool IsRequired { get; }

        /// <summary>
        /// Records what one input category contributed.
        /// </summary>
        /// <param name="category">Category name shown in the coverage report.</param>
        /// <param name="status">Whether the category was scanned.</param>
        /// <param name="count">Number of assets or objects covered.</param>
        /// <param name="reason">Why it was skipped or failed. Ignored when scanned.</param>
        /// <param name="isRequired">Whether a gap here prevents a clean result.</param>
        public ReferenceCoverageEntry(
            string category, ReferenceCoverageStatus status, int count, string reason = null, bool isRequired = true)
        {
            Category = category ?? string.Empty;
            Status = status;
            Count = count;
            Reason = reason ?? string.Empty;
            IsRequired = isRequired;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            Status == ReferenceCoverageStatus.Scanned
                ? $"{Category}: {Count} scanned"
                : $"{Category}: {Status} ({Reason})";
    }

    /// <summary>
    /// The coverage half of an audit result: every category the engine considered, and whether the
    /// overall picture is complete enough to call the project clean.
    /// </summary>
    public sealed class ReferenceCoverage
    {
        /// <summary>One entry per considered input category, in declaration order.</summary>
        public IReadOnlyList<ReferenceCoverageEntry> Entries { get; }

        /// <summary>
        /// True only when every required category was scanned. When false, zero findings means
        /// <c>Incomplete</c>, never <c>Clean</c>.
        /// </summary>
        public bool IsComplete { get; }

        /// <summary>Required categories that were skipped or failed. Never null.</summary>
        public IReadOnlyList<ReferenceCoverageEntry> Gaps { get; }

        /// <summary>Fraction of required categories that were scanned, in <c>[0,1]</c>.</summary>
        public float RequiredCompletionRatio { get; }

        /// <summary>
        /// True when a category was <i>attempted and failed</i>, as opposed to deliberately skipped.
        /// </summary>
        /// <remarks>
        /// The distinction matters and is easy to get wrong. A skipped category means this audit did not
        /// promise to cover it, and re-running the same scope will skip it again — so skipping is a reason to
        /// report incompleteness, not a reason to distrust the run. A <i>failure</i> means the run did not
        /// achieve what it set out to do (an asset that would not load, a cancellation), and re-running may
        /// well produce a better answer. Only failures should invalidate a cached snapshot.
        /// </remarks>
        public bool HasFailures { get; }

        /// <summary>
        /// Aggregates coverage entries and derives whether the picture is complete.
        /// </summary>
        /// <param name="entries">The considered categories, in declaration order.</param>
        public ReferenceCoverage(IReadOnlyList<ReferenceCoverageEntry> entries)
        {
            Entries = entries ?? Array.Empty<ReferenceCoverageEntry>();
            var required = Entries.Where(e => e.IsRequired).ToList();
            Gaps = required.Where(e => e.Status != ReferenceCoverageStatus.Scanned).ToList();
            IsComplete = Gaps.Count == 0;
            HasFailures = Entries.Any(e => e.Status == ReferenceCoverageStatus.Failed);
            RequiredCompletionRatio = required.Count == 0
                ? 1f
                : (required.Count - Gaps.Count) / (float)required.Count;
        }

        /// <summary>An empty coverage set, which is trivially complete. Used by pure-analysis callers.</summary>
        public static ReferenceCoverage Empty { get; } = new ReferenceCoverage(Array.Empty<ReferenceCoverageEntry>());

        /// <summary>Convenience factory for a fixed set of entries.</summary>
        /// <param name="entries">The considered categories.</param>
        public static ReferenceCoverage Of(params ReferenceCoverageEntry[] entries) =>
            new ReferenceCoverage(entries ?? Array.Empty<ReferenceCoverageEntry>());

        /// <summary>
        /// Human-readable summary of the gaps, for a header caption or a Doctor message.
        /// </summary>
        /// <remarks>
        /// Optional skips are named too, in parentheses. They do not make the audit incomplete, but leaving
        /// them out of the caption would let "complete" read as "everything was looked at" when it means
        /// "everything this scope promised was looked at".
        /// </remarks>
        public string DescribeGaps()
        {
            var optional = Entries
                .Where(e => !e.IsRequired && e.Status != ReferenceCoverageStatus.Scanned)
                .Select(e => e.Category)
                .ToList();

            var required = Gaps.Count == 0 ? "complete" : string.Join("; ", Gaps.Select(g => g.ToString()));

            return optional.Count == 0
                ? required
                : $"{required} (not in scope: {string.Join(", ", optional)})";
        }
    }
}
