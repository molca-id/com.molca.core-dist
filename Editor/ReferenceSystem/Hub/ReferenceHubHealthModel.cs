using System;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// The overall state the References header reports. A superset of
    /// <see cref="ReferenceAuditState"/>: the header also has to express states a snapshot cannot, namely
    /// "a scan is in flight" and "this result no longer describes the project".
    /// </summary>
    public enum ReferenceHubHealthState
    {
        /// <summary>No audit has run in this session yet.</summary>
        NotRun = 0,

        /// <summary>An audit is running.</summary>
        Scanning = 1,

        /// <summary>The last audit could not complete part of what it attempted.</summary>
        Stale = 2,

        /// <summary>At least one error finding.</summary>
        Errors = 3,

        /// <summary>Required coverage was not achieved, so nothing can be concluded.</summary>
        Incomplete = 4,

        /// <summary>Warnings only, over complete required coverage.</summary>
        Warnings = 5,

        /// <summary>No findings, complete required coverage, current snapshot.</summary>
        Clean = 6,
    }

    /// <summary>
    /// The immutable header view-model: the one place that decides when the word <b>Clean</b> may appear.
    /// </summary>
    /// <remarks>
    /// This exists as a separate, pure type precisely so that rule is testable. "Clean" is a claim about the
    /// whole project, and the previous tooling made it whenever a scan produced no findings — including when
    /// it had scanned nothing at all. Here it requires three independent things at once: no findings,
    /// complete required coverage, and a snapshot the project has not moved past.
    /// </remarks>
    public sealed class ReferenceHubHealth
    {
        /// <summary>The state to render.</summary>
        public ReferenceHubHealthState State { get; }

        /// <summary>Short label, e.g. <c>Clean</c> or <c>Incomplete</c>.</summary>
        public string Label { get; }

        /// <summary>Error-finding count.</summary>
        public int ErrorCount { get; }

        /// <summary>Warning-finding count.</summary>
        public int WarningCount { get; }

        /// <summary>Discovered provider count.</summary>
        public int ProviderCount { get; }

        /// <summary>Discovered reference-site count.</summary>
        public int SiteCount { get; }

        /// <summary>Fraction of required coverage categories that were scanned, in <c>[0,1]</c>.</summary>
        public float CoverageRatio { get; }

        /// <summary>Coverage description, including categories the scope deliberately left out.</summary>
        public string CoverageDetail { get; }

        /// <summary>When the reported audit completed, in local time. <c>null</c> before the first run.</summary>
        public DateTime? CompletedAt { get; }

        /// <summary><c>Play</c> while the editor is playing, otherwise <c>Edit</c>.</summary>
        public string Mode { get; }

        /// <summary>Why the snapshot is stale, or empty.</summary>
        public string StaleReason { get; }

        /// <summary>The phase caption of an in-flight scan, or empty.</summary>
        public string ScanPhase { get; }

        /// <summary>Progress of an in-flight scan in <c>[0,1]</c>, or null.</summary>
        public float? ScanProgress { get; }

        private ReferenceHubHealth(
            ReferenceHubHealthState state, int errors, int warnings, int providers, int sites,
            float coverageRatio, string coverageDetail, DateTime? completedAt, string mode,
            string staleReason, string scanPhase, float? scanProgress)
        {
            State = state;
            Label = LabelFor(state);
            ErrorCount = errors;
            WarningCount = warnings;
            ProviderCount = providers;
            SiteCount = sites;
            CoverageRatio = coverageRatio;
            CoverageDetail = coverageDetail ?? string.Empty;
            CompletedAt = completedAt;
            Mode = mode ?? "Edit";
            StaleReason = staleReason ?? string.Empty;
            ScanPhase = scanPhase ?? string.Empty;
            ScanProgress = scanProgress;
        }

        /// <summary>
        /// Derives the header state from a snapshot and the surrounding editor conditions.
        /// </summary>
        /// <param name="snapshot">The audit to report. Null is treated as "no audit has run".</param>
        /// <param name="isStale">Whether the service considers the snapshot out of date.</param>
        /// <param name="staleReason">Why it is stale.</param>
        /// <param name="hasRun">Whether any audit has completed in this session.</param>
        /// <param name="isPlaying">Whether the editor is in Play Mode.</param>
        /// <param name="scanPhase">Phase caption of an in-flight scan, or null when none is running.</param>
        /// <param name="scanProgress">Progress of an in-flight scan, or null.</param>
        /// <param name="isScanning">Whether a scan is currently running.</param>
        public static ReferenceHubHealth Describe(
            ReferenceAuditSnapshot snapshot,
            bool isStale,
            string staleReason,
            bool hasRun,
            bool isPlaying,
            string scanPhase = null,
            float? scanProgress = null,
            bool isScanning = false)
        {
            var coverage = snapshot?.Coverage;
            var mode = isPlaying ? "Play" : "Edit";

            // Order matters and encodes the precedence: an in-flight scan describes itself, an untrustworthy
            // result describes its untrustworthiness, and only then do findings and coverage get to speak.
            // Reversing any pair here is how "Clean" starts appearing over results that do not support it.
            ReferenceHubHealthState state;
            if (isScanning)
                state = ReferenceHubHealthState.Scanning;
            else if (!hasRun || snapshot == null)
                state = ReferenceHubHealthState.NotRun;
            else if (isStale)
                state = ReferenceHubHealthState.Stale;
            else if (snapshot.Errors.Count > 0)
                state = ReferenceHubHealthState.Errors;
            else if (coverage != null && !coverage.IsComplete)
                state = ReferenceHubHealthState.Incomplete;
            else if (snapshot.Warnings.Count > 0)
                state = ReferenceHubHealthState.Warnings;
            else
                state = ReferenceHubHealthState.Clean;

            return new ReferenceHubHealth(
                state,
                snapshot?.Errors.Count ?? 0,
                snapshot?.Warnings.Count ?? 0,
                snapshot?.Providers.Count ?? 0,
                snapshot?.Sites.Count ?? 0,
                coverage?.RequiredCompletionRatio ?? 0f,
                coverage?.DescribeGaps() ?? "nothing scanned",
                hasRun && snapshot != null ? snapshot.CompletedAtUtc.ToLocalTime() : (DateTime?)null,
                mode,
                isStale ? staleReason : null,
                scanPhase,
                scanProgress);
        }

        /// <summary>
        /// The counts line, e.g. <c>2 errors · 1 warning · 34 providers · 51 references</c>. Contains no
        /// asset path, so it is safe for a remote-observed surface.
        /// </summary>
        public string DescribeCounts() =>
            $"{ErrorCount} error{Plural(ErrorCount)} · {WarningCount} warning{Plural(WarningCount)} · "
            + $"{ProviderCount} provider{Plural(ProviderCount)} · {SiteCount} reference{Plural(SiteCount)}";

        /// <summary>The coverage line, e.g. <c>coverage 67% — Prefab assets: Skipped (…)</c>.</summary>
        public string DescribeCoverage() =>
            $"coverage {CoverageRatio * 100f:0}% — {CoverageDetail}";

        /// <summary>
        /// A compact status caption suitable for the activity rail: state plus counts, never a path.
        /// </summary>
        public string DescribeForActivityRail()
        {
            switch (State)
            {
                case ReferenceHubHealthState.Scanning:
                    return string.IsNullOrEmpty(ScanPhase) ? "scanning" : $"scanning · {ScanPhase}";
                case ReferenceHubHealthState.NotRun:
                    return "not audited yet";
                case ReferenceHubHealthState.Stale:
                    return "stale — re-run";
                case ReferenceHubHealthState.Errors:
                    return WarningCount > 0
                        ? $"{ErrorCount} error{Plural(ErrorCount)}, {WarningCount} warning{Plural(WarningCount)}"
                        : $"{ErrorCount} error{Plural(ErrorCount)}";
                case ReferenceHubHealthState.Incomplete:
                    return $"incomplete — coverage {CoverageRatio * 100f:0}%";
                case ReferenceHubHealthState.Warnings:
                    return $"{WarningCount} warning{Plural(WarningCount)}";
                default:
                    return "clean";
            }
        }

        private static string LabelFor(ReferenceHubHealthState state) => state switch
        {
            ReferenceHubHealthState.NotRun => "Not audited",
            ReferenceHubHealthState.Scanning => "Scanning",
            ReferenceHubHealthState.Stale => "Stale",
            ReferenceHubHealthState.Errors => "Errors",
            ReferenceHubHealthState.Incomplete => "Incomplete",
            ReferenceHubHealthState.Warnings => "Warnings",
            _ => "Clean",
        };

        private static string Plural(int count) => count == 1 ? string.Empty : "s";

        /// <inheritdoc/>
        public override string ToString() => $"{Label} — {DescribeCounts()}, {DescribeCoverage()}";
    }
}
