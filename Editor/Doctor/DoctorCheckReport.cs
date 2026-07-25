using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Completion record reported after each check finishes during
    /// <see cref="MolcaDoctor.RunAllAsync"/>: the findings that check produced and how long it took.
    /// Enables a live, per-check trace log in the Doctor window.
    /// </summary>
    /// <remarks>
    /// Reported on the main thread, once per check, immediately after the check returns (or crashes).
    /// A cancelled run stops <em>before</em> reporting the interrupted check, so a report always
    /// describes a check that ran to completion or threw — never a partial one. Complements
    /// <see cref="DoctorProgress"/>, which is reported <em>before</em> each check begins.
    /// </remarks>
    public readonly struct DoctorCheckReport
    {
        /// <summary>The check that just finished. Never null.</summary>
        public readonly IDoctorCheck Check;

        /// <summary>0-based position of this check within the run.</summary>
        public readonly int Index;

        /// <summary>Total number of enabled checks in this run.</summary>
        public readonly int TotalCount;

        /// <summary>Findings this check produced (never null; empty when the check passed).</summary>
        public readonly IReadOnlyList<DoctorIssue> Findings;

        /// <summary>Wall-clock time the check took, in milliseconds.</summary>
        public readonly double ElapsedMilliseconds;

        /// <summary>
        /// True if the check threw a non-cancellation exception; the crash is surfaced as a single
        /// <see cref="DoctorSeverity.Error"/> finding in <see cref="Findings"/>.
        /// </summary>
        public readonly bool Crashed;

        /// <summary>Creates a completion record.</summary>
        /// <param name="check">The check that finished.</param>
        /// <param name="index">0-based position of the check in the run.</param>
        /// <param name="totalCount">Total enabled checks in the run.</param>
        /// <param name="findings">Findings the check produced; null is treated as empty.</param>
        /// <param name="elapsedMilliseconds">Wall-clock duration of the check.</param>
        /// <param name="crashed">Whether the check threw a non-cancellation exception.</param>
        public DoctorCheckReport(IDoctorCheck check, int index, int totalCount,
            IReadOnlyList<DoctorIssue> findings, double elapsedMilliseconds, bool crashed)
        {
            Check = check;
            Index = index;
            TotalCount = totalCount;
            Findings = findings ?? Array.Empty<DoctorIssue>();
            ElapsedMilliseconds = elapsedMilliseconds;
            Crashed = crashed;
        }

        /// <summary>Number of findings at the given severity.</summary>
        /// <param name="severity">Severity to count.</param>
        /// <returns>The count of findings at <paramref name="severity"/>.</returns>
        public int CountAt(DoctorSeverity severity) => Findings.Count(f => f.Severity == severity);

        /// <summary>True when the check produced no findings.</summary>
        public bool IsClean => Findings.Count == 0;
    }
}
