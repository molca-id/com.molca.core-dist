using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Molca.Editor.Upgrade
{
    /// <summary>How much a piece of leftover 1.x state matters.</summary>
    public enum MolcaUpgradeSeverity
    {
        /// <summary>Worth knowing, nothing is broken.</summary>
        Info,

        /// <summary>Works today but will not survive a later release.</summary>
        Warning,

        /// <summary>Already broken on this version, or will not load.</summary>
        Blocking,
    }

    /// <summary>One thing an upgrading project still carries from 1.x.</summary>
    public sealed class MolcaUpgradeFinding
    {
        /// <summary>Stable dotted id, e.g. <c>colorid.legacy-components</c>.</summary>
        public string Id { get; }

        /// <summary>One line naming what was found.</summary>
        public string Title { get; }

        /// <summary>What it means and what to do, in a sentence or two.</summary>
        public string Detail { get; }

        /// <summary>How much it matters.</summary>
        public MolcaUpgradeSeverity Severity { get; }

        /// <summary>Where it is — asset paths, or <c>file.cs:12</c> for source.</summary>
        public IReadOnlyList<string> Locations { get; }

        /// <summary>
        /// The remediation fix id that resolves this, or <c>null</c> when nothing can.
        /// </summary>
        /// <remarks>
        /// <c>null</c> is a real answer, not a gap: rewriting a consumer's own C# is neither reversible
        /// nor locally decidable, so it is reported precisely and left to them.
        /// </remarks>
        public string FixId { get; }

        /// <summary>Whether a button can resolve this.</summary>
        public bool IsAutoFixable => !string.IsNullOrEmpty(FixId);

        /// <summary>Creates a finding.</summary>
        public MolcaUpgradeFinding(string id, string title, string detail, MolcaUpgradeSeverity severity,
            IReadOnlyList<string> locations = null, string fixId = null)
        {
            Id = id;
            Title = title;
            Detail = detail;
            Severity = severity;
            Locations = locations ?? Array.Empty<string>();
            FixId = fixId;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"[{Severity}] {Id}: {Title}" + (Locations.Count > 0 ? $" ({Locations.Count})" : string.Empty);
    }

    /// <summary>
    /// One system's answer to "is this project still carrying 1.x state?".
    /// </summary>
    /// <remarks>
    /// Discovered by <c>TypeCache</c>, so a system contributes to the upgrade report by existing rather
    /// than by being registered somewhere central. That is what keeps the report honest as systems are
    /// added: the alternative — one class listing every check — is a list somebody forgets to update, and
    /// a missed entry reads as "nothing to do".
    /// </remarks>
    public interface IMolcaUpgradeDetector
    {
        /// <summary>The system this speaks for, e.g. <c>Colour Theme</c>.</summary>
        string System { get; }

        /// <summary>Everything this system finds. Read-only — a detector never writes.</summary>
        /// <returns>Findings, or empty. Never <c>null</c>.</returns>
        IEnumerable<MolcaUpgradeFinding> Detect();
    }

    /// <summary>Everything an upgrading project still carries, across every system.</summary>
    public sealed class MolcaUpgradeReport
    {
        /// <summary>Findings, worst first.</summary>
        public IReadOnlyList<MolcaUpgradeFinding> Findings { get; }

        /// <summary>Detectors that threw, by name; a non-empty list makes the report a lower bound.</summary>
        public IReadOnlyList<string> Failures { get; }

        /// <summary>Whether the report saw everything it meant to.</summary>
        public bool IsConclusive => Failures.Count == 0;

        /// <summary>Whether the project is clean.</summary>
        public bool IsClean => Findings.Count == 0 && IsConclusive;

        /// <summary>Findings a button can resolve.</summary>
        public IEnumerable<MolcaUpgradeFinding> AutoFixable => Findings.Where(f => f.IsAutoFixable);

        /// <summary>Findings that need a person.</summary>
        public IEnumerable<MolcaUpgradeFinding> NeedsAttention => Findings.Where(f => !f.IsAutoFixable);

        /// <summary>Creates a report.</summary>
        public MolcaUpgradeReport(IReadOnlyList<MolcaUpgradeFinding> findings,
            IReadOnlyList<string> failures)
        {
            Findings = findings ?? Array.Empty<MolcaUpgradeFinding>();
            Failures = failures ?? Array.Empty<string>();
        }

        /// <summary>A human-readable summary.</summary>
        /// <returns>Multi-line text, safe to log.</returns>
        public string ToPreview()
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine("[MolcaUpgrade] 1.x → 2.x readiness");

            if (IsClean)
            {
                text.AppendLine("  Nothing left to migrate.");
                return text.ToString();
            }

            text.AppendLine($"  {Findings.Count} finding(s); "
                            + $"{AutoFixable.Count()} can be fixed automatically, "
                            + $"{NeedsAttention.Count()} need a decision.");

            foreach (string failure in Failures)
                text.AppendLine($"  INCONCLUSIVE {failure}");

            foreach (var finding in Findings)
            {
                text.AppendLine();
                text.AppendLine($"  [{finding.Severity}] {finding.Title}");
                text.AppendLine($"    {finding.Detail}");
                text.AppendLine(finding.IsAutoFixable
                    ? $"    Fixable: run '{finding.FixId}'."
                    : "    Not automatable — see the locations below.");

                foreach (string location in finding.Locations.Take(15))
                    text.AppendLine($"      {location}");

                if (finding.Locations.Count > 15)
                    text.AppendLine($"      … and {finding.Locations.Count - 15} more");
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// Asks every system what an upgrading project still carries.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Upgrade/</c>.
    /// <b>Shape:</b> editor-only static service. Read-only.
    /// <para/>
    /// The point of collecting these in one place is ordering: a consumer should not have to know that
    /// content migrates before types are deleted, or that scenes need a different scope from prefabs.
    /// One report, one button, and the systems keep their own knowledge.
    /// <para/>
    /// A detector that throws does not take the report down with it — the rest still runs and the failure
    /// is recorded, because a report that vanishes on one bad system is worse than a partial one that
    /// says so.
    /// </remarks>
    public static class MolcaUpgradeAudit
    {
        /// <summary>Runs every detector.</summary>
        /// <returns>The report; never <c>null</c>.</returns>
        public static MolcaUpgradeReport Run()
        {
            var findings = new List<MolcaUpgradeFinding>();
            var failures = new List<string>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<IMolcaUpgradeDetector>())
            {
                if (type.IsAbstract || type.IsInterface) continue;

                IMolcaUpgradeDetector detector;
                try
                {
                    detector = (IMolcaUpgradeDetector)Activator.CreateInstance(type);
                }
                catch (Exception exception)
                {
                    failures.Add($"{type.Name} could not be created: {exception.Message}");
                    continue;
                }

                try
                {
                    findings.AddRange(detector.Detect().Where(f => f != null));
                }
                catch (Exception exception)
                {
                    failures.Add($"{detector.System} detector threw: {exception.Message}");
                }
            }

            findings.Sort((a, b) =>
            {
                int bySeverity = b.Severity.CompareTo(a.Severity);
                return bySeverity != 0 ? bySeverity : string.CompareOrdinal(a.Id, b.Id);
            });

            return new MolcaUpgradeReport(findings, failures);
        }
    }
}
