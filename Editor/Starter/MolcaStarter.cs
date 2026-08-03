using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Editor.Mcp;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Starter
{
    /// <summary>What one step did, or would do, in a starter run.</summary>
    public sealed class MolcaStarterStepResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="step">The step it describes.</param>
        /// <param name="outcome">What happened.</param>
        /// <param name="skipped">True when the step reported it was already satisfied.</param>
        public MolcaStarterStepResult(IMolcaStarterStep step, MolcaStarterOutcome outcome, bool skipped)
        {
            Id = step.Id;
            Title = step.Title;
            Outcome = outcome;
            Skipped = skipped;
        }

        /// <summary>The step's id.</summary>
        public string Id { get; }

        /// <summary>The step's title.</summary>
        public string Title { get; }

        /// <summary>What happened.</summary>
        public MolcaStarterOutcome Outcome { get; }

        /// <summary>Whether the step was already satisfied and did nothing.</summary>
        public bool Skipped { get; }
    }

    /// <summary>The result of a starter run or preview.</summary>
    public sealed class MolcaStarterReport
    {
        private readonly List<MolcaStarterStepResult> _steps = new List<MolcaStarterStepResult>();
        private readonly List<string> _createdPaths = new List<string>();

        /// <summary>Whether this was a preview rather than a real run.</summary>
        public bool WasPreview { get; internal set; }

        /// <summary>Every step, in run order.</summary>
        public IReadOnlyList<MolcaStarterStepResult> Steps => _steps;

        /// <summary>Project-relative paths created by the run.</summary>
        public IReadOnlyList<string> CreatedPaths => _createdPaths;

        /// <summary>
        /// The <c>McpUndoStack</c> entry that reverts the run by deleting what it created; <c>null</c> when
        /// nothing was created.
        /// </summary>
        public string UndoEntryId { get; internal set; }

        /// <summary>How many steps changed something.</summary>
        public int ChangedCount => _steps.Count(s => !s.Skipped && s.Outcome.Changed);

        /// <summary>How many steps were already satisfied.</summary>
        public int SkippedCount => _steps.Count(s => s.Skipped);

        internal void Add(MolcaStarterStepResult result)
        {
            _steps.Add(result);
            foreach (var path in result.Outcome.CreatedPaths)
                if (!string.IsNullOrEmpty(path)) _createdPaths.Add(path);
        }

        /// <summary>A one-line summary.</summary>
        /// <returns>Text suitable for a header or a console line.</returns>
        public string Summarize()
        {
            var verb = WasPreview ? "would change" : "changed";
            return $"{ChangedCount} {verb} · {SkippedCount} already set up";
        }
    }

    /// <summary>
    /// Installs the recommended Molca project configuration, generating every asset from code.
    /// </summary>
    /// <remarks>
    /// <para>The counterpart to remediation, and deliberately separate from it: remediation repairs faults,
    /// this one expresses an opinion about what a fully-featured project looks like. It is only ever run by
    /// an explicit click.</para>
    /// <para><b>No asset is copied out of a package.</b> Everything is produced by
    /// <c>ScriptableObject.CreateInstance</c>, so the packages ship no editable configuration: nothing to
    /// re-GUID, nothing to drift from the schema, and nothing a consumer can edit only for the next upgrade
    /// to overwrite it.</para>
    /// <para>Re-running is safe — each step reports whether it is already satisfied.</para>
    /// <para>Editor-only; main thread.</para>
    /// </remarks>
    public static class MolcaStarter
    {
        /// <summary>Project-space folder the starter writes generated configuration into.</summary>
        /// <remarks>
        /// Matches where <c>MolcaProjectSettings</c> already lives, so a project's configuration stays in one
        /// place. Consumer space by construction — the starter never writes inside a package.
        /// </remarks>
        public const string SettingsFolder = "Assets/_Molca/Settings";

        private static List<IMolcaStarterStep> _steps;

        /// <summary>Every registered step, in run order.</summary>
        public static IReadOnlyList<IMolcaStarterStep> Steps
        {
            get { EnsureDiscovered(); return _steps; }
        }

        /// <summary>Clears the discovery cache. Intended for tests.</summary>
        public static void Reset() => _steps = null;

        /// <summary>Whether every registered step is already satisfied.</summary>
        /// <returns><c>true</c> when running the starter would change nothing.</returns>
        public static bool IsFullyConfigured() => Steps.All(SafeIsSatisfied);

        /// <summary>Describes what a run would do, changing nothing.</summary>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The preview report.</returns>
        public static MolcaStarterReport Preview(CancellationToken cancellationToken = default)
            => Run(dryRun: true, null, cancellationToken);

        /// <summary>
        /// Installs the recommended configuration, then records what it created so the run can be reverted.
        /// </summary>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The report.</returns>
        public static MolcaStarterReport Install(CancellationToken cancellationToken = default)
            => Install(null, cancellationToken);

        /// <summary>
        /// Installs one step by id, through the same run and revert-recording path as a full install.
        /// </summary>
        /// <param name="stepId">The <see cref="IMolcaStarterStep.Id"/> to run.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The report, covering only the selected step.</returns>
        /// <remarks>
        /// Exists so a per-row affordance (the onboarding checklist) can run a single step without opening a
        /// second install path that would have to re-implement the <c>McpUndoStack</c> recording — the one
        /// thing that makes a starter run revertible.
        /// </remarks>
        public static MolcaStarterReport InstallStep(string stepId, CancellationToken cancellationToken = default)
            => Install(step => string.Equals(step.Id, stepId, StringComparison.Ordinal), cancellationToken);

        /// <summary>
        /// Installs the steps matching <paramref name="filter"/>, recording what they created.
        /// </summary>
        /// <param name="filter">Which steps to run; <c>null</c> runs every registered step.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The report, covering only the steps the filter selected.</returns>
        public static MolcaStarterReport Install(
            Func<IMolcaStarterStep, bool> filter, CancellationToken cancellationToken = default)
        {
            var report = Run(dryRun: false, filter, cancellationToken);

            if (report.CreatedPaths.Count > 0)
            {
                // One entry for the whole run: reverting a half-configured project one asset at a time is
                // not something a user should have to reason about.
                report.UndoEntryId = McpUndoStack.RecordCreated(
                    report.CreatedPaths[0], "molca.starter",
                    $"Molca starter created {report.CreatedPaths.Count} asset(s)");

                foreach (var extra in report.CreatedPaths.Skip(1))
                    McpUndoStack.RecordCreated(extra, "molca.starter", $"Molca starter created '{extra}'");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return report;
        }

        private static MolcaStarterReport Run(
            bool dryRun, Func<IMolcaStarterStep, bool> filter, CancellationToken cancellationToken)
        {
            var report = new MolcaStarterReport { WasPreview = dryRun };

            foreach (var step in Steps)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (filter != null && !filter(step)) continue;

                if (SafeIsSatisfied(step))
                {
                    report.Add(new MolcaStarterStepResult(
                        step, MolcaStarterOutcome.NoChange("Already set up."), skipped: true));
                    continue;
                }

                MolcaStarterOutcome outcome;
                try
                {
                    outcome = step.Apply(dryRun, cancellationToken);
                }
                catch (Exception ex)
                {
                    // A failing step must not abort the rest: a partially configured project is still
                    // better than one that stopped at the first problem, and the report names what failed.
                    Debug.LogError($"[MolcaStarter] Step '{step.Id}' threw: {ex}");
                    outcome = MolcaStarterOutcome.NoChange($"Failed: {ex.Message}");
                }

                report.Add(new MolcaStarterStepResult(step, outcome, skipped: false));
            }

            return report;
        }

        private static bool SafeIsSatisfied(IMolcaStarterStep step)
        {
            try { return step.IsSatisfied(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MolcaStarter] '{step.Id}' could not report its state: {ex.Message}");
                return false;
            }
        }

        /// <summary>Ensures the starter's settings folder exists.</summary>
        /// <returns>The folder path.</returns>
        internal static string EnsureSettingsFolder()
        {
            if (!System.IO.Directory.Exists(SettingsFolder))
            {
                System.IO.Directory.CreateDirectory(SettingsFolder);
                AssetDatabase.Refresh();
            }
            return SettingsFolder;
        }

        private static void EnsureDiscovered()
        {
            if (_steps != null) return;

            var found = new List<IMolcaStarterStep>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var type in TypeCache.GetTypesDerivedFrom<IMolcaStarterStep>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                try
                {
                    var step = (IMolcaStarterStep)Activator.CreateInstance(type);
                    if (string.IsNullOrWhiteSpace(step.Id) || !seen.Add(step.Id))
                    {
                        Debug.LogWarning(
                            $"[MolcaStarter] Skipping step with missing/duplicate id '{step.Id}' ({type.Name}).");
                        continue;
                    }
                    found.Add(step);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MolcaStarter] Could not instantiate {type.Name}: {ex.Message}");
                }
            }

            _steps = found
                .OrderBy(s => s.Order)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToList();
        }
    }
}
