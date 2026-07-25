using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Doctor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Automation.BuiltIn
{
    /// <summary>
    /// The flagship Preflight workflow (§11.1): a composed, read-only validation pass that captures
    /// versions, confirms compilation is settled, and runs Molca Doctor, returning one versioned evidence
    /// bundle instead of requiring a caller to scrape Editor logs. This first cut covers the always-safe
    /// read-only steps; the heavier steps (EditMode tests, runtime smoke, a profile build) are batch-mode
    /// extensions because they exceed the interactive request budget (see the await-in-request finding).
    /// </summary>
    /// <remarks>
    /// The Doctor step honors a <c>checkIds</c> argument to scope the sweep (so an interactive CLI run can
    /// stay under the ~30s request cap); with no argument it runs the full registry, appropriate for a
    /// batch-mode preflight.
    /// </remarks>
    public static class PreflightWorkflow
    {
        /// <summary>The stable command id of the Preflight workflow.</summary>
        public const string Id = "molca.preflight";

        /// <summary>Builds the Preflight workflow definition.</summary>
        /// <returns>The workflow definition.</returns>
        public static MolcaWorkflowDefinition Create() => new MolcaWorkflowDefinition(
            id: Id,
            displayName: "Preflight",
            description: "Read-only preflight: captures versions, confirms compilation is settled, and runs Molca Doctor.",
            steps: new[]
            {
                new MolcaWorkflowStep("versions", "Capture project, Core, and Editor versions.", VersionsStep),
                new MolcaWorkflowStep("compilation", "Confirm no compilation is in progress.", CompilationStep),
                new MolcaWorkflowStep("doctor", "Run Molca Doctor convention checks.", DoctorStep),
            },
            mode: MolcaCommandMode.Edit,
            kind: MolcaCommandKind.ReadOnly);

        private static Awaitable<MolcaStepResult> VersionsStep(MolcaCommandContext context)
        {
            var settings = MolcaEditorSettings.Instance;
            var version = settings != null ? settings.VersionSettings : null;
            var core = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Molca.RuntimeManager).Assembly);

            var data = new JObject
            {
                ["projectVersion"] = version != null ? version.GetVersionString() : null,
                ["coreVersion"] = core != null ? core.version : "unknown",
                ["editorVersion"] = Application.unityVersion
            };
            return Completed(MolcaStepResult.Pass(data));
        }

        private static Awaitable<MolcaStepResult> CompilationStep(MolcaCommandContext context)
        {
            var compiling = EditorApplication.isCompiling;
            var data = new JObject { ["isCompiling"] = compiling };
            return Completed(compiling
                ? MolcaStepResult.Fail(new[] { new MolcaDiagnostic("compilation.in_progress",
                    "A script compilation is in progress; re-run once it settles.") }, data)
                : MolcaStepResult.Pass(data));
        }

        private static async Awaitable<MolcaStepResult> DoctorStep(MolcaCommandContext context)
        {
            HashSet<string> enabled = null;
            if (context.Arguments["checkIds"] is JArray ids && ids.Count > 0)
                enabled = new HashSet<string>(ids.Select(t => t.ToString()));

            List<DoctorIssue> issues = await MolcaDoctor.RunAllAsync(
                enabledIds: enabled, cancellationToken: context.CancellationToken);

            var errors = issues.Count(i => i.Severity == DoctorSeverity.Error);
            var warnings = issues.Count(i => i.Severity == DoctorSeverity.Warning);

            var findings = new JArray(issues
                .OrderByDescending(i => i.Severity)
                .Take(50)
                .Select(i => new JObject
                {
                    ["checkId"] = i.CheckId,
                    ["severity"] = i.Severity.ToString(),
                    ["message"] = i.Message,
                    ["path"] = i.Path,
                    ["line"] = i.Line
                }));

            var data = new JObject
            {
                ["findingCount"] = issues.Count,
                ["errorCount"] = errors,
                ["warningCount"] = warnings,
                ["findings"] = findings
            };

            // Surface Doctor warnings as workflow warnings so fail-on-warning can act on them.
            var diagnostics = issues
                .Where(i => i.Severity == DoctorSeverity.Warning)
                .Take(50)
                .Select(i => new MolcaDiagnostic(i.CheckId, i.Message, MolcaDiagnosticSeverity.Warning, i.Path, i.Line))
                .ToList();

            if (errors > 0)
            {
                diagnostics.Insert(0, new MolcaDiagnostic("doctor.errors", $"Doctor found {errors} error(s)."));
                return MolcaStepResult.Fail(diagnostics, data);
            }
            return MolcaStepResult.Pass(data, diagnostics);
        }

        /// <summary>Wraps a synchronous step result in an already-completed awaitable (no yield).</summary>
        private static Awaitable<MolcaStepResult> Completed(MolcaStepResult result)
        {
            var source = new AwaitableCompletionSource<MolcaStepResult>();
            source.SetResult(result);
            return source.Awaitable;
        }
    }
}
