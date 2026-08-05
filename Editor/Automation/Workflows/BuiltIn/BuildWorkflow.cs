using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Doctor;
using Molca.Settings;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Molca.Editor.Automation.BuiltIn
{
    /// <summary>
    /// The Build workflow (§11.4): an action that runs the build-relevant Molca Doctor gate and then
    /// wraps <see cref="BuildManager.BuildAsync"/>, returning a structured bundle (resolved profile/target,
    /// version/build number, gate result, <see cref="BuildReport"/> summary, output path, size, duration,
    /// warnings/errors, and whether the active target was restored) instead of a caller scraping the log.
    /// It never reproduces build-profile logic — <see cref="BuildManager"/> owns that.
    /// </summary>
    /// <remarks>
    /// This is an irreversible action (it runs an external build) and requires confirmation; a headless
    /// caller passes the confirm flag. A real build far exceeds the ~30s interactive request cap, so it is
    /// a batch-mode operation (the await-in-request finding) — and when a build target switch is needed
    /// outside Unity batch mode, <see cref="BuildManager"/> defers across a domain reload and returns null,
    /// which this workflow reports as a non-completion rather than a crash. Bundle hashing is deliberately
    /// left to the separately-permissioned deployment step (§11.5), not produced here.
    /// </remarks>
    public static class BuildWorkflow
    {
        /// <summary>The stable command id of the Build workflow.</summary>
        public const string Id = "molca.build";

        /// <summary>Builds the Build workflow definition.</summary>
        /// <returns>The workflow definition.</returns>
        public static MolcaWorkflowDefinition Create() => new MolcaWorkflowDefinition(
            id: Id,
            displayName: "Build",
            description: "Run the pre-build Doctor gate, then build the named profile via BuildManager and return a structured build report.",
            steps: new[]
            {
                new MolcaWorkflowStep("gate", "Run the build-relevant Molca Doctor checks as a pre-build gate.", GateStep),
                new MolcaWorkflowStep("build", "Build the resolved profile and summarize the BuildReport.", BuildStep),
            },
            mode: MolcaCommandMode.Edit,
            kind: MolcaCommandKind.Action,
            resourceClaims: new[] { MolcaResourceClaim.BuildPipeline, MolcaResourceClaim.BuildTarget },
            reversibility: MolcaCommandReversibility.None,
            requiresConfirmation: true);

        /// <summary>Runs the build-relevant Doctor checks; any Error halts before the build runs.</summary>
        /// <remarks>
        /// Through <see cref="MolcaBuildGate"/>, which owns which checks gate a build. This step used to
        /// keep its own copy of that list under a comment noting it mirrored BuildManager's — meaning the
        /// evidence bundle could claim a gate the build itself no longer ran. The build below is invoked
        /// with its own pre-build checks disabled so the gate still runs exactly once.
        /// </remarks>
        private static async Awaitable<MolcaStepResult> GateStep(MolcaCommandContext context)
        {
            var gate = await MolcaBuildGate.RunAsync(context.CancellationToken);

            var errors = gate.Errors;
            var warnings = gate.Warnings;

            var diagnostics = errors
                .Select(i => new MolcaDiagnostic(i.CheckId, i.Message, MolcaDiagnosticSeverity.Error, i.Path, i.Line))
                .Concat(warnings.Select(i => new MolcaDiagnostic(i.CheckId, i.Message, MolcaDiagnosticSeverity.Warning, i.Path, i.Line)))
                .ToList();

            var data = new JObject
            {
                ["passed"] = errors.Count == 0,
                ["errorCount"] = errors.Count,
                ["warningCount"] = warnings.Count
            };

            return errors.Count > 0
                ? MolcaStepResult.Fail(diagnostics, data)
                : MolcaStepResult.Pass(data, diagnostics);
        }

        /// <summary>
        /// Resolves the profile, captures the active target, builds with BuildManager's own gate disabled
        /// (the gate step already ran), then folds the <see cref="BuildReport"/> summary and version
        /// metadata into the evidence bundle.
        /// </summary>
        private static async Awaitable<MolcaStepResult> BuildStep(MolcaCommandContext context)
        {
            var editorSettings = MolcaEditorSettings.Instance;
            var buildSettings = editorSettings != null ? editorSettings.BuildSettings : null;
            var versionSettings = editorSettings != null ? editorSettings.VersionSettings : null;
            if (buildSettings == null)
            {
                return MolcaStepResult.Fail("build.no_settings",
                    "No BuildSettings asset found in Molca Editor Settings — cannot resolve a profile.");
            }

            var profileName = context.Arguments["profile"]?.ToString();
            var profile = buildSettings.GetProfile(profileName); // empty name resolves to the first profile
            if (profile == null)
            {
                return MolcaStepResult.Fail("build.profile_not_found",
                    $"Build profile '{profileName}' was not found.");
            }

            var targetBefore = EditorUserBuildSettings.activeBuildTarget;

            BuildReport report = await BuildManager.BuildAsync(
                profile.name, runPreBuildChecks: false, cancellationToken: context.CancellationToken);

            var targetAfter = EditorUserBuildSettings.activeBuildTarget;

            var versionInfo = new JObject
            {
                ["version"] = versionSettings != null ? versionSettings.GetVersionString() : null,
                ["buildNumber"] = versionSettings != null ? versionSettings.GetBuildNumberString() : null,
                ["fullVersion"] = versionSettings != null ? versionSettings.GetFullVersionString() : null
            };

            // BuildManager returns null when the gate failed, config was invalid, or a target switch was
            // deferred across a domain reload — none of which we can complete inside this request.
            if (report == null)
            {
                var pending = new JObject
                {
                    ["profile"] = profile.name,
                    ["requestedTarget"] = profile.target.ToString(),
                    ["activeTargetBefore"] = targetBefore.ToString(),
                    ["restoreOriginalTarget"] = profile.restoreOriginalTarget,
                    ["version"] = versionInfo
                };
                return MolcaStepResult.Fail(new[] { new MolcaDiagnostic("build.not_completed",
                    "The build did not complete in this request (invalid config, or a build-target switch was deferred across a domain reload). See the Editor log.") }, pending);
            }

            var summary = report.summary;
            var data = new JObject
            {
                ["profile"] = profile.name,
                ["target"] = summary.platform.ToString(),
                ["requestedTarget"] = profile.target.ToString(),
                ["result"] = summary.result.ToString(),
                ["version"] = versionInfo,
                ["outputPath"] = summary.outputPath,
                ["totalSizeBytes"] = (long)summary.totalSize,
                ["totalErrors"] = summary.totalErrors,
                ["totalWarnings"] = summary.totalWarnings,
                ["durationSeconds"] = summary.totalTime.TotalSeconds,
                ["activeTargetBefore"] = targetBefore.ToString(),
                ["activeTargetAfter"] = targetAfter.ToString(),
                ["targetRestored"] = targetAfter == targetBefore
            };

            if (summary.result == BuildResult.Succeeded)
                return MolcaStepResult.Pass(data);

            return MolcaStepResult.Fail(new[] { new MolcaDiagnostic("build.failed",
                $"Build finished with result '{summary.result}' ({summary.totalErrors} error(s).") }, data);
        }
    }
}
