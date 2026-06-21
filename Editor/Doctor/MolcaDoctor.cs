using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Runs the Molca convention checks. Used by <see cref="MolcaDoctorWindow"/>
    /// interactively and by CI via <see cref="RunCI"/>.
    /// </summary>
    public static class MolcaDoctor
    {
        /// <summary>All registered checks, in execution order.</summary>
        public static IReadOnlyList<IDoctorCheck> Checks { get; } = new IDoctorCheck[]
        {
            new StaticSingletonUsageCheck(),
            new RuntimeSoWriteCheck(),
            new MissingFinishCallbackCheck(),
            new InjectResolutionCheck(),
            new SceneObjectReferenceCheck(),
            new BuildScenesCheck(),
            new ColorIDReferenceValidityCheck(),
            new ColorIDReferenceEarlyAccessCheck(),
            new DynamicLocalizationLocaleValidityCheck(),
            new DynamicLocalizationInitContractCheck(),
            new VersionSettingsCheck(),
            new BuildProfileCheck(),
            new ContentPackageCheck(),
            new DesignLanguageCheck(),
            new HttpHardcodedUrlCheck(),
            new HttpResponseSuccessMisuseCheck(),
            new HttpUnredactedLoggingCheck(),
            new DataProviderLifetimeTokenCheck(),
            new SequenceValidationCheck(),
        };

        /// <summary>
        /// Runs every enabled check asynchronously and returns all findings, reporting
        /// progress before each check so callers can surface a loading indicator.
        /// </summary>
        /// <param name="enabledIds">Optional whitelist of check ids; null runs all.</param>
        /// <param name="onProgress">
        /// Optional callback invoked on the main thread once per enabled check,
        /// immediately before it runs. Never invoked with a null check.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancels the run cooperatively. Checks observe it mid-scan, so cancellation
        /// takes effect promptly; the findings gathered so far are returned.
        /// </param>
        /// <param name="onStatus">
        /// Optional sink for sub-check progress detail (e.g. "Prefabs 3/12"). A long
        /// check reports through it via <see cref="DoctorContext.ReportStatus"/> so the
        /// caller can show that progress is happening within a single check. Reset to
        /// null automatically as each check begins.
        /// </param>
        /// <returns>All findings, in the order their checks produced them.</returns>
        /// <remarks>
        /// CPU-only checks run on a background thread (the editor stays responsive);
        /// the orchestrator returns to the main thread before each progress callback and
        /// before the next check, so main-thread checks always run on the main thread.
        /// </remarks>
        public static async Awaitable<List<DoctorIssue>> RunAllAsync(
            ISet<string> enabledIds = null,
            Action<DoctorProgress> onProgress = null,
            CancellationToken cancellationToken = default,
            Action<string> onStatus = null)
        {
            var context = new DoctorContext { StatusReporter = onStatus };
            var issues = new List<DoctorIssue>();

            var toRun = (enabledIds == null
                ? Checks
                : Checks.Where(c => enabledIds.Contains(c.Id))).ToList();

            for (int i = 0; i < toRun.Count; i++)
            {
                // A preceding background check resumes us here; force the main thread
                // so progress reporting and main-thread checks are always safe.
                await Awaitable.MainThreadAsync();
                if (cancellationToken.IsCancellationRequested)
                    break;

                var check = toRun[i];
                onStatus?.Invoke(null); // clear stale detail from the previous check
                onProgress?.Invoke(new DoctorProgress(i, toRun.Count, check));

                try
                {
                    var found = await check.RunAsync(context, cancellationToken);
                    if (found != null)
                        issues.AddRange(found);
                }
                catch (OperationCanceledException)
                {
                    break; // cancellation is not an error — stop quietly
                }
                catch (Exception e)
                {
                    // A crashing check must surface as a finding, not abort the run.
                    issues.Add(new DoctorIssue(check.Id, DoctorSeverity.Error,
                        $"Check crashed: {e.GetType().Name}: {e.Message}"));
                }
            }

            // Guarantee the caller resumes on the main thread regardless of where the
            // last check left us.
            await Awaitable.MainThreadAsync();
            return issues;
        }

        /// <summary>
        /// CI entry point. Invoke with (note: no <c>-quit</c> — this method exits the
        /// editor itself once the async run completes, so the editor stays alive to
        /// pump the checks):
        /// <c>Unity -batchmode -executeMethod Molca.Editor.Doctor.MolcaDoctor.RunCI [-doctorReport path]</c>.
        /// Exits 1 if any Error-severity issue is found, 0 otherwise. Warnings and
        /// Infos are printed but do not fail the run.
        /// </summary>
        /// <remarks>
        /// <c>async void</c> is permitted here as a CI entry-point shim; the body is
        /// wrapped in try/catch so no exception escapes into Unity's sync context.
        /// </remarks>
        public static async void RunCI()
        {
            try
            {
                var issues = await RunAllAsync();
                var errors = issues.Count(i => i.Severity == DoctorSeverity.Error);
                var warnings = issues.Count(i => i.Severity == DoctorSeverity.Warning);

                var report = new StringBuilder();
                report.AppendLine($"Molca Doctor — {issues.Count} finding(s): {errors} error(s), {warnings} warning(s).");
                foreach (var issue in issues.OrderByDescending(i => i.Severity))
                    report.AppendLine(issue.ToString());

                Debug.Log(report.ToString());

                // Optional machine-readable report next to the console output.
                var args = Environment.GetCommandLineArgs();
                int reportArg = Array.IndexOf(args, "-doctorReport");
                if (reportArg >= 0 && reportArg + 1 < args.Length)
                {
                    try
                    {
                        File.WriteAllText(args[reportArg + 1], report.ToString());
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[MolcaDoctor] Failed to write report: {e.Message}");
                    }
                }

                if (Application.isBatchMode)
                    EditorApplication.Exit(errors > 0 ? 1 : 0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[MolcaDoctor] CI run failed: {e}");
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
            }
        }
    }
}
