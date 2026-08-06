using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEditor.Build;
using UnityEditor;
using UnityEditor.Build.Reporting;
using Molca.Settings;

namespace Molca.Editor
{
    [InitializeOnLoad]
    public static class BuildManager
    {
        private const string PendingBuildProfileKey = "Molca.BuildManager.PendingProfile";
        private const string PendingBuildRestoreTargetKey = "Molca.BuildManager.RestoreTarget";
        private const string PendingApplyProfileKey = "Molca.BuildManager.PendingApplyProfile";

        // Session token shared by both pending-operation paths. Persisted in EditorPrefs (survives the
        // domain reload after a target switch) and mirrored in SessionState (cleared on editor restart),
        // so a token mismatch on resume means the editor was closed mid-switch and the request is stale.
        private const string PendingBuildSessionKey = "Molca.BuildManager.PendingSession";

        static BuildManager()
        {
            EditorApplication.delayCall += TryResumePendingBuild;
        }

        /// <summary>
        /// Builds the given profile <em>without</em> the pre-build Doctor gate.
        /// </summary>
        /// <param name="profileName">The build profile to build.</param>
        /// <returns>
        /// The <see cref="BuildReport"/> from BuildPipeline, or <c>null</c> when the build
        /// did not run (missing settings/profile, target-switch failure, a failed pre-build step, or a
        /// deferred editor build pending a target switch). CI callers must treat <c>null</c> or a
        /// non-Succeeded result as failure.
        /// </returns>
        /// <remarks>
        /// Prefer <see cref="BuildAsync"/>, which runs <see cref="MolcaBuildGate"/> first. This overload
        /// exists for callers that have already run the gate themselves (the Build automation workflow)
        /// or that genuinely want it skipped. It is not the CI entry point —
        /// <see cref="CommandLineBuild"/> gates before calling it — because "the build path CI happens to
        /// use" and "the build path that skips the checks" being the same method is how a project ends up
        /// shipping releases that were never checked.
        /// </remarks>
        public static BuildReport Build(string profileName)
        {
            return Build(profileName, null);
        }

        /// <summary>
        /// Runs the build-relevant Molca Doctor gate, then builds <paramref name="profileName"/> when
        /// no Error-severity issue is found. This is the async build entry point — the Doctor checks
        /// are async (main-thread affinity), so they cannot be awaited from the synchronous
        /// <see cref="Build(string)"/>, which remains available for callers that do not want the gate.
        /// </summary>
        /// <param name="profileName">The build profile to build.</param>
        /// <param name="runPreBuildChecks">When true (default), runs the pre-build Doctor gate first.</param>
        /// <param name="cancellationToken">Cancels the pre-build checks.</param>
        /// <returns>
        /// The <see cref="BuildReport"/>, or <c>null</c> when the gate failed, configuration was
        /// invalid, or the build was deferred for a target switch (see <see cref="Build(string)"/>).
        /// </returns>
        public static async Awaitable<BuildReport> BuildAsync(
            string profileName, bool runPreBuildChecks = true, CancellationToken cancellationToken = default)
        {
            if (runPreBuildChecks)
            {
                var gate = await MolcaBuildGate.RunAsync(cancellationToken);
                if (!gate.Passed)
                {
                    Debug.LogError(gate.DescribeFailure());
                    RecordAttempt(profileName, null, MolcaBuildOutcome.Refused,
                        $"pre-build gate refused the build: {gate.Errors.Count} Doctor error(s) — " +
                        string.Join("; ", gate.Errors.Select(e => e.CheckId).Distinct()),
                        reasonCode: MolcaBuildReasonCode.DoctorGate);
                    return null;
                }
            }

            // RunAllAsync resumes on the main thread, so the synchronous build runs on the main thread.
            return Build(profileName);
        }

        /// <summary>
        /// Applies the profile's settings (target, version, PlayerSettings, RuntimeManager, GlobalSettings) without building.
        /// </summary>
        public static void ApplyProfile(string profileName)
        {
            var buildSettings = MolcaEditorSettings.Instance.BuildSettings;
            var versionSettings = MolcaEditorSettings.Instance.VersionSettings;

            if (buildSettings == null || versionSettings == null)
            {
                Debug.LogError("Build or Version settings not found in Editor Settings!");
                return;
            }

            var profile = buildSettings.GetProfile(profileName);
            if (profile == null)
            {
                Debug.LogError($"Build profile '{profileName}' not found!");
                return;
            }

            var targetGroup = BuildPipeline.GetBuildTargetGroup(profile.target);

            if (EditorUserBuildSettings.activeBuildTarget != profile.target)
            {
                if (Application.isBatchMode)
                {
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(targetGroup, profile.target))
                    {
                        Debug.LogError($"Failed to switch active build target to {profile.target}. Aborting apply.");
                        return;
                    }
                }
                else
                {
                    MolcaEditorPrefs.SetString(PendingApplyProfileKey, profileName);
                    MarkPendingSession();
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(targetGroup, profile.target))
                    {
                        MolcaEditorPrefs.DeleteKey(PendingApplyProfileKey);
                        MolcaEditorPrefs.DeleteKey(PendingBuildSessionKey);
                        Debug.LogError($"Failed to switch active build target to {profile.target}. Aborting apply.");
                    }
                    else
                    {
                        Debug.Log($"Switching to {profile.target}. Profile will be applied automatically after recompile.");
                    }
                    return;
                }
            }

            versionSettings.SyncToUnityPlayerSettings(profile.target);
            PlayerSettings.companyName = Molca.MolcaProjectSettings.Instance.CompanyName;

            if (profile.il2cpp)
            {
                PlayerSettings.SetScriptingBackend(
                    NamedBuildTarget.FromBuildTargetGroup(targetGroup),
                    ScriptingImplementation.IL2CPP);
            }
            else
            {
                PlayerSettings.SetScriptingBackend(
                    NamedBuildTarget.FromBuildTargetGroup(targetGroup),
                    ScriptingImplementation.Mono2x);
            }

            if (!string.IsNullOrWhiteSpace(profile.defineSymbols))
            {
                PlayerSettings.SetScriptingDefineSymbols(
                    NamedBuildTarget.FromBuildTargetGroup(targetGroup),
                    profile.defineSymbols);
            }

            bool isMobileTarget = profile.target == BuildTarget.Android || profile.target == BuildTarget.iOS;
            if (isMobileTarget && !string.IsNullOrWhiteSpace(profile.applicationIdentifierOverride))
            {
                PlayerSettings.SetApplicationIdentifier(
                    NamedBuildTarget.FromBuildTargetGroup(targetGroup),
                    profile.applicationIdentifierOverride.Trim());
            }

            var projectSettings = Molca.MolcaProjectSettings.Instance;
            if (profile.runtimeManager != null)
            {
                projectSettings.RuntimeManager = profile.runtimeManager;
            }
            if (profile.globalSettings != null)
            {
                projectSettings.GlobalSettings = profile.globalSettings;
            }
            if (profile.runtimeManager != null || profile.globalSettings != null)
            {
                EditorUtility.SetDirty(projectSettings);
                AssetDatabase.SaveAssets();
            }

            Debug.Log($"Applied profile '{profileName}' (target: {profile.target}).");
        }

        private static BuildReport Build(string profileName, BuildTarget? restoreTarget)
        {
            // Get settings from editor settings
            var buildSettings = MolcaEditorSettings.Instance.BuildSettings;
            var versionSettings = MolcaEditorSettings.Instance.VersionSettings;

            if (buildSettings == null || versionSettings == null)
            {
                Debug.LogError("Build or Version settings not found in Editor Settings!");
                RecordAttempt(profileName, null, MolcaBuildOutcome.Refused,
                    "Build or Version settings are not assigned on the Molca Editor Settings asset.");
                return null;
            }

            // Get build profile
            var profile = buildSettings.GetProfile(profileName);
            if (profile == null)
            {
                Debug.LogError($"Build profile '{profileName}' not found!");
                RecordAttempt(profileName, null, MolcaBuildOutcome.Refused,
                    $"build profile '{profileName}' does not exist in '{buildSettings.name}'.",
                    reasonCode: MolcaBuildReasonCode.ProfileNotFound);
                return null;
            }
            var targetGroup = BuildPipeline.GetBuildTargetGroup(profile.target);

            // Switch to target if needed (ensures correct scripting defines/imports/addressables)
            if (EditorUserBuildSettings.activeBuildTarget != profile.target)
            {
                if (Application.isBatchMode)
                {
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(targetGroup, profile.target))
                    {
                        Debug.LogError($"Failed to switch active build target to {profile.target}. Aborting build.");
                        RecordAttempt(profileName, profile.target, MolcaBuildOutcome.Refused,
                            $"could not switch the active build target to {profile.target} — is the platform module installed?",
                            reasonCode: MolcaBuildReasonCode.TargetSwitchFailed);
                        return null;
                    }
                }
                else
                {
                    // Defer build until the editor finishes switching/recompiling.
                    var restoreTargetValue = profile.restoreOriginalTarget ? EditorUserBuildSettings.activeBuildTarget : (BuildTarget?)null;
                    SetPendingBuild(profileName, restoreTargetValue);
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(targetGroup, profile.target))
                    {
                        ClearPendingBuild();
                        Debug.LogError($"Failed to switch active build target to {profile.target}. Aborting build.");
                        RecordAttempt(profileName, profile.target, MolcaBuildOutcome.Refused,
                            $"could not switch the active build target to {profile.target} — is the platform module installed?",
                            reasonCode: MolcaBuildReasonCode.TargetSwitchFailed);
                    }
                    else
                    {
                        Debug.Log($"Switched active build target to {profile.target}. Build will resume automatically.");
                        // Recorded so the Hub can say what it is waiting for. The resumed build appends its
                        // own record, so this row is superseded rather than left as the last word.
                        RecordAttempt(profileName, profile.target, MolcaBuildOutcome.Refused,
                            $"deferred while the editor switches to {profile.target}; resumes automatically after the reload.",
                            reasonCode: MolcaBuildReasonCode.TargetSwitchDeferred);
                    }
                    return null;
                }
            }

            // A session for exactly this build. Gates Unity discovers by type (localization, references)
            // are handed no context, so they read the running build's facts from the session — which
            // expires here rather than living on as a static latch nobody is sure who clears.
            BuildReport report;
            var buildContext = new MolcaBuildContext(profile);
            using (MolcaBuildSession.Begin(buildContext))
            {
                report = RunResolvedBuild(profile, targetGroup, versionSettings, buildContext);
            }

            // Restore original build target if requested (editor only)
            if (!Application.isBatchMode && restoreTarget.HasValue && restoreTarget.Value != EditorUserBuildSettings.activeBuildTarget)
            {
                var restoreGroup = BuildPipeline.GetBuildTargetGroup(restoreTarget.Value);
                if (EditorUserBuildSettings.SwitchActiveBuildTarget(restoreGroup, restoreTarget.Value))
                {
                    Debug.Log($"Restored active build target to {restoreTarget.Value}.");
                }
                else
                {
                    Debug.LogWarning($"Failed to restore active build target to {restoreTarget.Value}.");
                }
            }

            return report;
        }

        /// <summary>
        /// Runs the build for an already-resolved profile whose target is already active, inside an open
        /// <see cref="MolcaBuildSession"/>.
        /// </summary>
        /// <param name="profile">The resolved build profile.</param>
        /// <param name="targetGroup">The target group for <paramref name="profile"/>'s target.</param>
        /// <param name="versionSettings">The project's version settings.</param>
        /// <param name="buildContext">The context for this build; steps record facts on it.</param>
        /// <returns>The build report, or null when a pre-build step or gate aborted the build.</returns>
        private static BuildReport RunResolvedBuild(
            BuildSettings.BuildProfile profile,
            BuildTargetGroup targetGroup,
            VersionSettings versionSettings,
            MolcaBuildContext buildContext)
        {
            // Version name, platform version codes, and the runtime build-info asset are applied by
            // BuildVersionPreprocessor (IPreprocessBuildWithReport) during BuildPipeline.BuildPlayer,
            // so they also cover File > Build and CI builds — no explicit sync needed here.

            // Get company name from settings
            PlayerSettings.companyName = Molca.MolcaProjectSettings.Instance.CompanyName;

            // Get project name from settings
            string projectName = Molca.MolcaProjectSettings.Instance.ProjectName;
            string versionString = versionSettings.GetBundleVersionString(profile.target);

            Debug.Log($"Version set to: {versionString}");

            // Setup build options
            var buildOptions = BuildOptions.None;
            if (profile.developmentBuild)
                buildOptions |= BuildOptions.Development;
            if (profile.allowDebugging)
                buildOptions |= BuildOptions.AllowDebugging;
            if (profile.compress)
                buildOptions |= BuildOptions.CompressWithLz4HC;
            if (profile.autoRunPlayer)
                buildOptions |= BuildOptions.AutoRunPlayer;
            if (profile.showBuiltPlayer)
                buildOptions |= BuildOptions.ShowBuiltPlayer;
            if (profile.cleanBuildCache)
                buildOptions |= BuildOptions.CleanBuildCache;
            if (profile.connectWithProfiler)
                buildOptions |= BuildOptions.ConnectWithProfiler;
            if (profile.deepProfiling)
                buildOptions |= BuildOptions.EnableDeepProfilingSupport;
            if (profile.strictMode)
                buildOptions |= BuildOptions.StrictMode;
            if (profile.detailedBuildReport)
                buildOptions |= BuildOptions.DetailedBuildReport;

            // Pre-build gates run before any PlayerSettings/EditorUserBuildSettings mutation, so an
            // aborted build needs no restore — no scripting backend, signing secrets, app-id, or
            // Android format changes have been applied yet.

            // Scene reference gate: missing, duplicated, ambiguous or wrongly-typed reference targets, plus
            // incomplete scan coverage, from the shared read-only audit of the build scenes.
            //
            // Runs here as well as in ReferenceBuildGate (the global IPreprocessBuildWithReport) so a
            // reference problem aborts before any player setting is mutated and the message names the
            // finding codes. The gate skips its own audit when this one passed, so the work is not
            // duplicated.
            // The profile's own scene set, resolved before the reference gate so the gate audits the scenes
            // this build actually ships. Auditing the global enabled list instead would fail the build in
            // ReferenceBuildSceneAudit — correctly, since a scene it never looked at is being built.
            if (!profile.TryResolveScenePaths(out var profileScenes, out var sceneFailure))
            {
                Debug.LogError($"[BuildManager] Build aborted: {sceneFailure}");
                RecordAttempt(profile.name, profile.target, MolcaBuildOutcome.Refused, sceneFailure,
                    reasonCode: MolcaBuildReasonCode.SceneSetUnresolvable);
                return null;
            }

            var referenceErrors = SceneReferenceBuildValidator.Validate(profileScenes);
            if (referenceErrors.Count > 0)
            {
                Debug.LogError(
                    $"[BuildManager] Build aborted: {referenceErrors.Count} scene reference problem(s):\n  " +
                    string.Join("\n  ", referenceErrors));
                RecordAttempt(profile.name, profile.target, MolcaBuildOutcome.Refused,
                    $"{referenceErrors.Count} scene reference problem(s) — see the Reference audit.",
                    reasonCode: MolcaBuildReasonCode.SceneReferences);
                return null;
            }

            // Everything about this profile that can be known to be impossible, refused while the editor
            // is still untouched — before the content build below spends minutes and before any
            // PlayerSettings mutation needs restoring.
            if (!TryValidateProfileForBuild(profile, out var profileFailure))
            {
                Debug.LogError($"[BuildManager] Build aborted: {profileFailure}");
                RecordAttempt(profile.name, profile.target, MolcaBuildOutcome.Refused, profileFailure,
                    reasonCode: MolcaBuildReasonCode.ProfileInvalid);
                return null;
            }

            // Registered pre-build steps (Addressables content among them). This used to be a branch
            // per system, named here in the build core; a system that could not edit this file had
            // nowhere to put its pre-build work but a global build callback with a hand-picked order.
            // Steps declare their own order and record what they did on the build context.
            if (!MolcaBuildStepRegistry.RunAll(buildContext, out var stepFailure))
            {
                Debug.LogError($"[BuildManager] Build aborted: {stepFailure}");
                RecordAttempt(profile.name, profile.target, MolcaBuildOutcome.Refused, stepFailure,
                    reasonCode: MolcaBuildReasonCode.PreBuildStep);
                return null;
            }

            // Handle IL2CPP setting (requires changing PlayerSettings)
            if (profile.il2cpp)
            {
                PlayerSettings.SetScriptingBackend(
                    NamedBuildTarget.FromBuildTargetGroup(targetGroup),
                    ScriptingImplementation.IL2CPP);
            }
            else
            {
                PlayerSettings.SetScriptingBackend(
                    NamedBuildTarget.FromBuildTargetGroup(targetGroup),
                    ScriptingImplementation.Mono2x);
            }

            if (!string.IsNullOrWhiteSpace(profile.defineSymbols))
            {
                PlayerSettings.SetScriptingDefineSymbols(
                    NamedBuildTarget.FromBuildTargetGroup(targetGroup),
                    profile.defineSymbols);
            }

            // Android output format (AAB is required for Google Play uploads) and CPU architectures.
            if (profile.target == BuildTarget.Android)
            {
                EditorUserBuildSettings.buildAppBundle = profile.buildAppBundle;
                PlayerSettings.Android.targetArchitectures = profile.androidArchitectures;
            }

            // Apply per-profile signing. Captured originals are restored in the finally below so the
            // keystore passwords (sourced from environment variables) never persist in the editor.
            var signingRestore = ApplySigning(profile);

            // Override application identifier for Android/iOS when specified
            string originalApplicationIdentifier = null;
            bool isMobileTarget = profile.target == BuildTarget.Android || profile.target == BuildTarget.iOS;
            if (isMobileTarget && !string.IsNullOrWhiteSpace(profile.applicationIdentifierOverride))
            {
                var namedTarget = NamedBuildTarget.FromBuildTargetGroup(targetGroup);
                originalApplicationIdentifier = PlayerSettings.GetApplicationIdentifier(namedTarget);
                PlayerSettings.SetApplicationIdentifier(namedTarget, profile.applicationIdentifierOverride.Trim());
            }

            // Setup build target
            BuildTarget buildTarget = profile.target;
            string fullVersionString = versionSettings.GetFullVersionString();
            // Captured with the version string, before the build runs. BuildVersionPostprocessor
            // advances the build number inside BuildPipeline.BuildPlayer, so re-reading it afterwards
            // for the manifest reported the *next* build's number beside this build's version.
            string builtBuildNumber = versionSettings.GetBuildNumberString();
            string buildPath = GetBuildPath(buildTarget, projectName, fullVersionString, profile.outputPath, profile.name, profile.buildAppBundle);

            Debug.Log($"Starting {profile.name} build...");
            Debug.Log($"Target: {buildTarget}");
            Debug.Log($"Output: {buildPath}");

            // Log active build options
            var activeOptions = new System.Collections.Generic.List<string>();
            if (profile.developmentBuild) activeOptions.Add("Development");
            if (profile.allowDebugging) activeOptions.Add("Debugging");
            if (profile.il2cpp) activeOptions.Add("IL2CPP");
            if (profile.compress) activeOptions.Add("Compress");
            if (profile.autoRunPlayer) activeOptions.Add("AutoRun");
            if (profile.showBuiltPlayer) activeOptions.Add("ShowBuilt");
            if (profile.cleanBuildCache) activeOptions.Add("CleanCache");
            if (profile.connectWithProfiler) activeOptions.Add("Profiler");
            if (profile.deepProfiling) activeOptions.Add("DeepProfiling");
            if (profile.strictMode) activeOptions.Add("StrictMode");
            if (profile.detailedBuildReport) activeOptions.Add("DetailedReport");

            Debug.Log($"Build Options: {string.Join(", ", activeOptions)}");

            // The profile's scene set when it declares one, otherwise the enabled Build Settings scenes.
            string[] scenes = profileScenes ?? EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (profileScenes != null)
                Debug.Log($"Scenes: {profileScenes.Length} from profile '{profile.name}' (Build Settings list ignored).");

            // Apply profile RuntimeManager/GlobalSettings to MolcaProjectSettings for the build
            var projectSettings = Molca.MolcaProjectSettings.Instance;
            var originalRuntimeManager = projectSettings.RuntimeManager;
            var originalGlobalSettings = projectSettings.GlobalSettings;
            if (profile.runtimeManager != null)
            {
                projectSettings.RuntimeManager = profile.runtimeManager;
            }
            if (profile.globalSettings != null)
            {
                projectSettings.GlobalSettings = profile.globalSettings;
            }
            if (profile.runtimeManager != null || profile.globalSettings != null)
            {
                EditorUtility.SetDirty(projectSettings);
                AssetDatabase.SaveAssets();
            }

            BuildReport report = null;
            try
            {
                // Perform build
                report = BuildPipeline.BuildPlayer(
                    scenes,
                    buildPath,
                    buildTarget,
                    buildOptions
                );
            }
            finally
            {
                // Restore original RuntimeManager/GlobalSettings after build
                if (profile.runtimeManager != null || profile.globalSettings != null)
                {
                    projectSettings.RuntimeManager = originalRuntimeManager;
                    projectSettings.GlobalSettings = originalGlobalSettings;
                    EditorUtility.SetDirty(projectSettings);
                    AssetDatabase.SaveAssets();
                }

                // Restore original application identifier for Android/iOS
                if (isMobileTarget && originalApplicationIdentifier != null)
                {
                    PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.FromBuildTargetGroup(targetGroup), originalApplicationIdentifier);
                }

                // Restore signing config (and clear any secrets) regardless of build outcome.
                RestoreSigning(signingRestore);
            }

            // Handle build result
            if (report.summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"Build completed successfully!\nOutput: {buildPath}\nSize: {report.summary.totalSize / 1024f / 1024f:F2} MB");
                WriteBuildManifest(profile, report, buildPath, scenes, activeOptions, fullVersionString, builtBuildNumber, versionSettings);

                var detail =
                    $"built in {report.summary.totalTime.TotalSeconds:F0}s · " +
                    $"{report.summary.totalSize / 1024f / 1024f:F1} MB · {buildPath}";

                // The record is built before the post steps run so they can read it, and appended after so
                // it can carry what they reported.
                var record = CreateRecord(
                    profile.name, profile.target, MolcaBuildOutcome.Succeeded, detail, report, versionSettings,
                    buildNumber: builtBuildNumber);

                var postContext = new MolcaPostBuildContext(profile, buildPath, record, buildContext);
                if (!MolcaBuildStepRegistry.RunAllPost(postContext, out var postFailures))
                {
                    // The player exists; this is not a build failure. Reported at error severity because
                    // an unpublished artifact or an unuploaded symbol file is a real problem, and recorded
                    // on the build row so it is not only in a console someone has since cleared.
                    Debug.LogError(
                        $"[BuildManager] The build succeeded, but {postFailures.Count} post-build step(s) failed:\n  " +
                        string.Join("\n  ", postFailures));
                    record.detail = detail + $" · {postFailures.Count} post-build step(s) failed: " +
                        string.Join("; ", postFailures);
                }

                MolcaBuildRecordStore.Append(record);
            }
            else
            {
                Debug.LogError($"Build failed with {report.summary.totalErrors} errors and {report.summary.totalWarnings} warnings.");
                foreach (var step in report.steps)
                {
                    if (step.messages.Any(m => m.type == LogType.Error))
                    {
                        Debug.LogError($"Step '{step.name}' failed:");
                        foreach (var message in step.messages.Where(m => m.type == LogType.Error))
                        {
                            Debug.LogError(message.content);
                        }
                    }
                }

                // A gate that refused by throwing BuildFailedException is handed back as a Failed report,
                // indistinguishable from a compile error — so the gate names itself on the way out and this
                // reads what it recorded. Without that, every refusal and every broken build would share
                // one reason, and the only way to tell them apart would be to parse the console text this
                // design keeps on the machine.
                string refusal = MolcaBuildRefusal.Recorded;
                var outcome = string.IsNullOrEmpty(refusal)
                    ? MolcaBuildOutcome.Failed
                    : MolcaBuildOutcome.Refused;

                var failureRecord = CreateRecord(
                    profile.name, profile.target, outcome,
                    $"{report.summary.result} · {report.summary.totalErrors} error(s) — see the Console.",
                    report, versionSettings,
                    string.IsNullOrEmpty(refusal) ? MolcaBuildReasonCode.BuildFailed : refusal,
                    // Unchanged by a failed build — the postprocessor advances nothing unless the build
                    // succeeded — but passed for the same reason as the success path, so the record's
                    // number stays the attempt's own if that ever ceases to be true.
                    buildNumber: builtBuildNumber);

                MolcaBuildRecordStore.Append(failureRecord);

                // Reported to the control plane here rather than through IMolcaPostBuildStep, because post
                // steps run only when an artifact exists — that is their contract and it is the right one.
                // A build with no token minted reports nothing, which is every refusal that happened before
                // the license gate ran; that gap is documented rather than papered over.
                ReportOutcomeToControlPlane(failureRecord, buildContext, profile);
            }

            return report;
        }

        /// <summary>
        /// Records one build attempt in <see cref="MolcaBuildRecordStore"/>.
        /// </summary>
        /// <param name="profileName">The profile that was asked for.</param>
        /// <param name="target">The resolved target, or null when it never resolved.</param>
        /// <param name="outcome">How the attempt ended.</param>
        /// <param name="detail">One line saying what happened.</param>
        /// <param name="report">The build report, when one exists.</param>
        /// <remarks>
        /// Every exit from the build path passes through here, including the ones that produce no artifact.
        /// A build that a gate refused is the case most worth recording and the one a manifest beside the
        /// output can never describe.
        /// </remarks>
        /// <summary>
        /// Reports a build attempt that produced no artifact to the control plane.
        /// </summary>
        /// <param name="record">The local record for the attempt.</param>
        /// <param name="buildContext">The running build's context, carrying the minted build-token id.</param>
        /// <param name="profile">The profile that was built, for the scene count.</param>
        /// <remarks>
        /// <para>
        /// Silent when no build token was minted, which is not an error: <c>File &gt; Build</c>, a project
        /// that is not connected, a distribution with licensing unconfigured, and every refusal that
        /// happened before the license gate ran all land here. There is nothing on the control plane for
        /// such an attempt to be a record of, because the row hangs off the token.
        /// </para>
        /// <para>
        /// Failures never propagate. A build that already failed must not also produce an exception from the
        /// code that reports it having failed.
        /// </para>
        /// </remarks>
        private static void ReportOutcomeToControlPlane(
            MolcaBuildRecord record, MolcaBuildContext buildContext, BuildSettings.BuildProfile profile)
        {
            string buildId = buildContext?.GetValue(Licensing.ControlPlaneBuildRecorder.BuildIdKey);
            if (string.IsNullOrEmpty(buildId)) return;

            try
            {
                int sceneCount = profile != null &&
                    profile.TryResolveScenePaths(out var scenes, out _) && scenes != null
                        ? scenes.Length
                        : EditorBuildSettings.scenes.Count(scene => scene.enabled);

                Licensing.ControlPlaneBuildRecorder.Queue(buildId, record, sceneCount);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    $"[BuildManager] Could not report the build outcome to the control plane: {exception.Message}");
            }
        }

        private static void RecordAttempt(
            string profileName, BuildTarget? target, MolcaBuildOutcome outcome, string detail,
            BuildReport report = null, string reasonCode = null) =>
            MolcaBuildRecordStore.Append(
                CreateRecord(profileName, target, outcome, detail, report, versionSettings: null, reasonCode));

        /// <summary>
        /// Builds the record for one build attempt without persisting it.
        /// </summary>
        /// <param name="profileName">The profile that was asked for.</param>
        /// <param name="target">The resolved target, or null when it never resolved.</param>
        /// <param name="outcome">How the attempt ended.</param>
        /// <param name="detail">One line saying what happened.</param>
        /// <param name="report">The build report, when one exists.</param>
        /// <param name="versionSettings">
        /// The version settings to read; resolved from Editor Settings when null.
        /// </param>
        /// <param name="reasonCode">Why a non-successful attempt ended the way it did.</param>
        /// <param name="buildNumber">
        /// The build number this attempt actually carried, captured before the build ran. Falls back to
        /// the asset's current number when null — correct for every attempt that never reached
        /// <c>BuildPipeline.BuildPlayer</c>, and wrong for one that did: <c>BuildVersionPostprocessor</c>
        /// advances the number inside that call, so a record built afterwards reported the <em>next</em>
        /// build's number beside this build's version. The manifest already guarded against this; the
        /// record — the thing the Hub's history list and the control plane read — did not.
        /// </param>
        /// <returns>The unpersisted record.</returns>
        /// <remarks>
        /// Separate from <see cref="RecordAttempt"/> so a successful build can hand the record to its post
        /// steps and append it once they have had their say.
        /// </remarks>
        private static MolcaBuildRecord CreateRecord(
            string profileName, BuildTarget? target, MolcaBuildOutcome outcome, string detail,
            BuildReport report, VersionSettings versionSettings, string reasonCode = null,
            string buildNumber = null)
        {
            versionSettings ??= MolcaEditorSettings.Instance != null
                ? MolcaEditorSettings.Instance.VersionSettings
                : null;

            GitLogReader.ReadProvenance(
                Directory.GetParent(Application.dataPath)?.FullName, out var commit, out var branch);

            return new MolcaBuildRecord
            {
                profile = string.IsNullOrEmpty(profileName) ? "(none)" : profileName,
                target = target?.ToString() ?? string.Empty,
                outcome = outcome.ToString(),
                semanticVersion = versionSettings != null ? versionSettings.GetSemanticVersion() : string.Empty,
                buildNumber = buildNumber
                    ?? (versionSettings != null ? versionSettings.GetBuildNumberString() : string.Empty),
                commit = commit,
                branch = branch,
                outputPath = report != null ? report.summary.outputPath : string.Empty,
                totalSizeBytes = report != null ? (long)report.summary.totalSize : 0L,
                durationSeconds = report != null ? report.summary.totalTime.TotalSeconds : 0d,
                timestampUtc = System.DateTime.UtcNow.ToString("o"),
                detail = detail ?? string.Empty,
                // A successful build has no reason to record; anything else always has one, so a failure
                // can never read as having happened for no reason. The fallback is deliberately the vaguest
                // code rather than the free text in `detail`: `detail` may name a scene or a path, and this
                // field is the only part of a failure that leaves the machine.
                reasonCode = outcome == MolcaBuildOutcome.Succeeded
                    ? string.Empty
                    : (string.IsNullOrEmpty(reasonCode) ? "unspecified" : reasonCode),
            };
        }

        /// <summary>
        /// Refuses a build this profile cannot produce, before anything has been mutated.
        /// </summary>
        /// <param name="profile">The profile about to be built.</param>
        /// <param name="failure">Why the build cannot run; null when it can.</param>
        /// <returns>True when the build may proceed.</returns>
        /// <remarks>
        /// Both checks here used to happen too late to matter. An unsupported target was discovered by
        /// <see cref="GetBuildPath"/> after PlayerSettings had been rewritten, and missing signing
        /// passwords were a <em>warning</em> logged from <see cref="ApplySigning"/> while the build carried
        /// on — so a profile that asked to be signed with a release keystore produced an artifact signed
        /// with Unity's debug keystore instead, indistinguishable from a real one until a store rejected
        /// it or, worse, accepted it. Every other gate in this system fails closed; signing is the one
        /// where failing open is least defensible.
        /// </remarks>
        internal static bool TryValidateProfileForBuild(BuildSettings.BuildProfile profile, out string failure)
        {
            failure = null;
            if (profile == null)
            {
                failure = "no profile.";
                return false;
            }

            if (!IsOutputTargetSupported(profile.target))
            {
                failure = UnsupportedTargetMessage(profile.target);
                return false;
            }

            if (profile.useCustomSigning && profile.target == BuildTarget.Android)
            {
                var missing = new System.Collections.Generic.List<string>();
                if (string.IsNullOrWhiteSpace(profile.androidKeystorePath))
                    missing.Add("Keystore Path is empty");
                else if (!File.Exists(AbsoluteKeystorePath(profile.androidKeystorePath)))
                    missing.Add($"keystore file '{AbsoluteKeystorePath(profile.androidKeystorePath)}' does not exist");

                if (string.IsNullOrWhiteSpace(profile.androidKeyaliasName))
                    missing.Add("Key Alias Name is empty");

                if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(profile.androidKeystorePassEnv)))
                    missing.Add($"environment variable '{profile.androidKeystorePassEnv}' (keystore password) is not set");

                if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(profile.androidKeyaliasPassEnv)))
                    missing.Add($"environment variable '{profile.androidKeyaliasPassEnv}' (key alias password) is not set");

                if (missing.Count > 0)
                {
                    failure =
                        $"profile '{profile.name}' enables custom Android signing, but it cannot be applied:\n  - " +
                        string.Join("\n  - ", missing) +
                        "\nRefusing rather than falling back to the debug keystore, which would produce an " +
                        "unpublishable artifact that looks exactly like a signed one. Fix the signing " +
                        "configuration, or turn Use Custom Signing off for this profile.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>Resolves a profile keystore path against the project root when it is relative.</summary>
        private static string AbsoluteKeystorePath(string keystorePath) =>
            Path.IsPathRooted(keystorePath)
                ? keystorePath
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", keystorePath));

        /// <summary>
        /// True when <see cref="GetBuildPath"/> has an output rule for <paramref name="target"/>.
        /// </summary>
        /// <param name="target">The target to check.</param>
        /// <remarks>
        /// Deliberately a second switch rather than a try/catch around <see cref="GetBuildPath"/>, which
        /// creates directories as it resolves. A test asserts the two agree for every
        /// <see cref="BuildTarget"/> value, so the pair cannot drift into a target that validates and then
        /// throws mid-build.
        /// </remarks>
        internal static bool IsOutputTargetSupported(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneOSX:
                case BuildTarget.StandaloneLinux64:
                case BuildTarget.WebGL:
                case BuildTarget.Android:
                case BuildTarget.iOS:
                    return true;
                default:
                    return false;
            }
        }

        private static string UnsupportedTargetMessage(BuildTarget target) =>
            $"Molca's build system has no output-path rule for build target '{target}'. " +
            "Add one to BuildManager.GetBuildPath — an artifact whose location is guessed is " +
            "an artifact nobody can find or sign.";

        /// <summary>
        /// Resolves the <c>locationPathName</c> for a build: which folder it lands in, and what the
        /// artifact inside it is called.
        /// </summary>
        /// <param name="target">The target being built.</param>
        /// <param name="projectName">The project name, used for the executable/bundle name.</param>
        /// <param name="fullVersionString">Version and build number, for the folder/file name.</param>
        /// <param name="outputRoot">The profile's output path, absolute or relative to the project root.</param>
        /// <param name="profileName">The profile being built, for the folder/file name.</param>
        /// <param name="androidAppBundle">True when Android should produce an <c>.aab</c>.</param>
        /// <returns>The path to pass to <c>BuildPipeline.BuildPlayer</c>.</returns>
        /// <remarks>
        /// <b>Every supported target is named here, and the fallback throws.</b> Only Windows, Android and
        /// iOS used to be, and everything else fell through to a bare extensionless path — so a macOS
        /// build asked Unity for a location with no <c>.app</c> suffix, which is not a valid application
        /// bundle, and Linux and WebGL builds landed as loose siblings in <c>Builds/</c> rather than in a
        /// folder of their own. A silent default for a platform nobody tested is how "the framework
        /// supports it" and "the framework has a switch case for it" come apart.
        /// </remarks>
        internal static string GetBuildPath(BuildTarget target, string projectName, string fullVersionString, string outputRoot, string profileName, bool androidAppBundle)
        {
            string fileName = $"{projectName}_{profileName}_{fullVersionString}";
            string buildDir = ResolveOutputPath(outputRoot);

            // Create build directory if it doesn't exist
            Directory.CreateDirectory(buildDir);

            // One folder per (platform, profile, version) so successive builds of different profiles do
            // not overwrite each other's output, and a build can be zipped by folder.
            string PlatformDir(string platform)
            {
                var dir = Path.Combine(buildDir, $"{platform}_{profileName}_{fullVersionString}");
                Directory.CreateDirectory(dir);
                return dir;
            }

            switch (target)
            {
                case BuildTarget.StandaloneWindows64:
                    return Path.Combine(PlatformDir("Windows"), $"{projectName}.exe");
                case BuildTarget.StandaloneWindows:
                    return Path.Combine(PlatformDir("Windows32"), $"{projectName}.exe");
                case BuildTarget.StandaloneOSX:
                    // The .app extension is not decoration: Unity writes an application bundle, and a
                    // location without it produces something macOS will not launch.
                    return Path.Combine(PlatformDir("macOS"), $"{projectName}.app");
                case BuildTarget.StandaloneLinux64:
                    // Linux players are extensionless by convention.
                    return Path.Combine(PlatformDir("Linux"), projectName);
                case BuildTarget.WebGL:
                    // WebGL's location is the folder the site is written into, not a file.
                    return PlatformDir("WebGL");
                case BuildTarget.Android:
                    var androidDir = Path.Combine(buildDir, $"Android_{profileName}");
                    Directory.CreateDirectory(androidDir);
                    var androidExtension = androidAppBundle ? "aab" : "apk";
                    return Path.Combine(androidDir, $"{fileName}.{androidExtension}");
                case BuildTarget.iOS:
                    // An Xcode project directory, not a player.
                    var iosDir = Path.Combine(buildDir, $"iOS_{profileName}");
                    Directory.CreateDirectory(iosDir);
                    return iosDir;
                default:
                    // Unreachable via the Molca build path: TryValidateProfileForBuild refuses an
                    // unsupported target before anything is mutated. Kept as the backstop for a direct
                    // caller, and as the single place the message lives.
                    throw new System.NotSupportedException(UnsupportedTargetMessage(target));
            }
        }

        /// <summary>
        /// Writes a <c>build-info.json</c> manifest next to the build output for QA traceability:
        /// version, build number, git commit/branch, target, options, scenes, size, and timestamp.
        /// </summary>
        /// <remarks>Best-effort: a failure here is logged as a warning and never fails the build.</remarks>
        private static void WriteBuildManifest(
            BuildSettings.BuildProfile profile, BuildReport report, string buildPath,
            string[] scenes, System.Collections.Generic.List<string> options,
            string fullVersion, string buildNumber, VersionSettings versionSettings)
        {
            try
            {
                var dir = Directory.Exists(buildPath) ? buildPath : Path.GetDirectoryName(buildPath);
                if (string.IsNullOrEmpty(dir))
                    return;
                Directory.CreateDirectory(dir);

                GitLogReader.ReadProvenance(
                    Directory.GetParent(Application.dataPath)?.FullName, out var commit, out var branch);

                var manifest = new BuildManifest
                {
                    product = Molca.MolcaProjectSettings.Instance.ProjectName,
                    profile = profile.name,
                    target = profile.target.ToString(),
                    version = fullVersion,
                    semanticVersion = versionSettings.GetSemanticVersion(),
                    buildNumber = buildNumber,
                    commit = commit,
                    branch = branch,
                    timestampUtc = System.DateTime.UtcNow.ToString("o"),
                    projectId = Molca.MolcaProjectSettings.Instance.ProjectId,
                    projectCode = Molca.MolcaProjectSettings.Instance.ProjectCode,
                    output = buildPath,
                    totalSizeBytes = (long)report.summary.totalSize,
                    scenes = scenes,
                    options = options.ToArray(),
                };

                var manifestPath = Path.Combine(dir, "build-info.json");
                File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
                Debug.Log($"[BuildManager] Wrote build manifest to {manifestPath}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BuildManager] Failed to write build manifest: {e.Message}");
            }
        }

        /// <summary>Serializable build manifest written next to the output as build-info.json.</summary>
        [System.Serializable]
        private class BuildManifest
        {
            public string product;
            public string profile;
            public string target;
            public string version;
            public string semanticVersion;
            public string buildNumber;
            public string commit;
            public string branch;
            public string timestampUtc;
            public string projectId;
            public string projectCode;
            public string output;
            public long totalSizeBytes;
            public string[] scenes;
            public string[] options;
        }

        private static string ResolveOutputPath(string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                return Path.Combine(Application.dataPath, "../Builds");
            }

            if (Path.IsPathRooted(outputRoot))
            {
                return outputRoot;
            }

            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputRoot));
        }

        private static void TryResumePendingBuild()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            // A pending operation is only valid within the editor session that created it (see
            // PendingBuildSessionKey). A missing/mismatched token means the editor was closed
            // mid-switch — discard the request rather than launching an unexpected build on startup.
            bool hasPending = MolcaEditorPrefs.HasKey(PendingBuildProfileKey) || MolcaEditorPrefs.HasKey(PendingApplyProfileKey);
            if (hasPending && !IsPendingFromThisSession())
            {
                Debug.LogWarning("[BuildManager] Discarded a pending build/apply left over from a previous editor session.");
                ClearPendingBuild();
                MolcaEditorPrefs.DeleteKey(PendingApplyProfileKey);
                MolcaEditorPrefs.DeleteKey(PendingBuildSessionKey);
                return;
            }

            if (MolcaEditorPrefs.HasKey(PendingBuildProfileKey))
            {
                if (!EditorApplication.isCompiling && !EditorApplication.isUpdating && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    var profileName = MolcaEditorPrefs.GetString(PendingBuildProfileKey);
                    BuildTarget? restoreTarget = null;
                    if (MolcaEditorPrefs.HasKey(PendingBuildRestoreTargetKey))
                    {
                        restoreTarget = (BuildTarget)MolcaEditorPrefs.GetInt(PendingBuildRestoreTargetKey, (int)EditorUserBuildSettings.activeBuildTarget);
                    }
                    ClearPendingBuild();

                    // Gated, like the click that started it. A build deferred across a target switch
                    // used to resume through the ungated path, so asking to build a profile for a
                    // target that was not active silently skipped the pre-build Doctor gate — the
                    // opposite of what a target change should do to your confidence in the project.
                    ResumeGatedBuild(profileName, restoreTarget);
                }
                else
                {
                    EditorApplication.delayCall += TryResumePendingBuild;
                }
                return;
            }

            if (MolcaEditorPrefs.HasKey(PendingApplyProfileKey))
            {
                if (!EditorApplication.isCompiling && !EditorApplication.isUpdating && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    var profileName = MolcaEditorPrefs.GetString(PendingApplyProfileKey);
                    MolcaEditorPrefs.DeleteKey(PendingApplyProfileKey);
                    MolcaEditorPrefs.DeleteKey(PendingBuildSessionKey);
                    ApplyProfile(profileName);
                }
                else
                {
                    EditorApplication.delayCall += TryResumePendingBuild;
                }
                return;
            }
        }

        /// <summary>
        /// Runs the pre-build gate and then the deferred build, preserving its restore target.
        /// </summary>
        /// <param name="profileName">The profile whose build was deferred across the target switch.</param>
        /// <param name="restoreTarget">The target to restore afterwards, or null to stay put.</param>
        /// <remarks>
        /// <c>async void</c> is the Unity event-handler entry-point exception in the async contract —
        /// this resumes from <see cref="EditorApplication.delayCall"/> and has no caller to await it.
        /// The body is wrapped so nothing escapes into Unity's synchronization context.
        /// </remarks>
        private static async void ResumeGatedBuild(string profileName, BuildTarget? restoreTarget)
        {
            try
            {
                var gate = await MolcaBuildGate.RunAsync();
                if (!gate.Passed)
                {
                    Debug.LogError(gate.DescribeFailure());
                    return;
                }

                Build(profileName, restoreTarget);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BuildManager] Deferred build of '{profileName}' failed: {e}");
            }
        }

        private static void SetPendingBuild(string profileName, BuildTarget? restoreTarget)
        {
            MolcaEditorPrefs.SetString(PendingBuildProfileKey, profileName);
            MarkPendingSession();
            if (restoreTarget.HasValue)
            {
                MolcaEditorPrefs.SetInt(PendingBuildRestoreTargetKey, (int)restoreTarget.Value);
            }
            else if (MolcaEditorPrefs.HasKey(PendingBuildRestoreTargetKey))
            {
                MolcaEditorPrefs.DeleteKey(PendingBuildRestoreTargetKey);
            }
        }

        private static void ClearPendingBuild()
        {
            if (MolcaEditorPrefs.HasKey(PendingBuildProfileKey))
            {
                MolcaEditorPrefs.DeleteKey(PendingBuildProfileKey);
            }

            if (MolcaEditorPrefs.HasKey(PendingBuildRestoreTargetKey))
            {
                MolcaEditorPrefs.DeleteKey(PendingBuildRestoreTargetKey);
            }

            if (MolcaEditorPrefs.HasKey(PendingBuildSessionKey))
            {
                MolcaEditorPrefs.DeleteKey(PendingBuildSessionKey);
            }
        }

        /// <summary>Stamps the pending operation with a token valid only for the current editor session.</summary>
        private static void MarkPendingSession()
        {
            var token = System.Guid.NewGuid().ToString("N");
            MolcaEditorPrefs.SetString(PendingBuildSessionKey, token);
            SessionState.SetString(PendingBuildSessionKey, token);
        }

        /// <summary>True when the persisted pending token matches this session's SessionState token.</summary>
        private static bool IsPendingFromThisSession()
        {
            var persisted = MolcaEditorPrefs.GetString(PendingBuildSessionKey, string.Empty);
            var session = SessionState.GetString(PendingBuildSessionKey, string.Empty);
            return !string.IsNullOrEmpty(session) && session == persisted;
        }

        /// <summary>
        /// Captured PlayerSettings signing state, restored after a build so applied secrets and
        /// project-wide signing config do not leak into the editor session.
        /// </summary>
        private struct SigningRestore
        {
            public bool HasAndroid;
            public bool AndroidUseCustomKeystore;
            public string AndroidKeystoreName;
            public string AndroidKeystorePass;
            public string AndroidKeyaliasName;
            public string AndroidKeyaliasPass;

            public bool HasIos;
            public string IosTeamId;
            public bool IosAutomaticSigning;
        }

        /// <summary>
        /// Applies the profile's signing configuration for Android/iOS and returns the captured
        /// originals for restoration. Keystore/alias passwords are read from environment variables
        /// named by the profile — never stored in the asset.
        /// </summary>
        /// <param name="profile">The build profile whose signing configuration to apply.</param>
        /// <returns>The captured original signing state, for <see cref="RestoreSigning"/>.</returns>
        private static SigningRestore ApplySigning(BuildSettings.BuildProfile profile)
        {
            var restore = new SigningRestore();
            if (!profile.useCustomSigning)
                return restore;

            if (profile.target == BuildTarget.Android)
            {
                restore.HasAndroid = true;
                restore.AndroidUseCustomKeystore = PlayerSettings.Android.useCustomKeystore;
                restore.AndroidKeystoreName = PlayerSettings.Android.keystoreName;
                restore.AndroidKeystorePass = PlayerSettings.Android.keystorePass;
                restore.AndroidKeyaliasName = PlayerSettings.Android.keyaliasName;
                restore.AndroidKeyaliasPass = PlayerSettings.Android.keyaliasPass;

                var keystorePath = profile.androidKeystorePath;
                if (!string.IsNullOrWhiteSpace(keystorePath) && !Path.IsPathRooted(keystorePath))
                    keystorePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", keystorePath));

                var keystorePass = System.Environment.GetEnvironmentVariable(profile.androidKeystorePassEnv) ?? string.Empty;
                var keyaliasPass = System.Environment.GetEnvironmentVariable(profile.androidKeyaliasPassEnv) ?? string.Empty;

                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = keystorePath ?? string.Empty;
                PlayerSettings.Android.keystorePass = keystorePass;
                PlayerSettings.Android.keyaliasName = profile.androidKeyaliasName;
                PlayerSettings.Android.keyaliasPass = keyaliasPass;

                if (string.IsNullOrEmpty(keystorePass) || string.IsNullOrEmpty(keyaliasPass))
                {
                    // Unreachable from the Molca build path — TryValidateProfileForBuild refuses this
                    // profile before any mutation. Kept because this method is also reachable from a
                    // direct caller, and silently signing with the debug keystore is the outcome that
                    // must never happen quietly.
                    Debug.LogError(
                        "[BuildManager] Custom Android signing is enabled but the password environment " +
                        $"variables ('{profile.androidKeystorePassEnv}'/'{profile.androidKeyaliasPassEnv}') are empty. " +
                        "The resulting artifact will not be signed with the intended keystore.");
                }
            }
            else if (profile.target == BuildTarget.iOS)
            {
                restore.HasIos = true;
                restore.IosTeamId = PlayerSettings.iOS.appleDeveloperTeamID;
                restore.IosAutomaticSigning = PlayerSettings.iOS.appleEnableAutomaticSigning;

                if (!string.IsNullOrWhiteSpace(profile.iosTeamId))
                    PlayerSettings.iOS.appleDeveloperTeamID = profile.iosTeamId.Trim();
                PlayerSettings.iOS.appleEnableAutomaticSigning = profile.iosAutomaticSigning;
            }

            return restore;
        }

        /// <summary>Restores the signing state captured by <see cref="ApplySigning"/>, clearing any applied secrets.</summary>
        /// <param name="restore">The state previously returned by <see cref="ApplySigning"/>.</param>
        private static void RestoreSigning(SigningRestore restore)
        {
            if (restore.HasAndroid)
            {
                PlayerSettings.Android.useCustomKeystore = restore.AndroidUseCustomKeystore;
                PlayerSettings.Android.keystoreName = restore.AndroidKeystoreName;
                PlayerSettings.Android.keystorePass = restore.AndroidKeystorePass;
                PlayerSettings.Android.keyaliasName = restore.AndroidKeyaliasName;
                PlayerSettings.Android.keyaliasPass = restore.AndroidKeyaliasPass;
            }

            if (restore.HasIos)
            {
                PlayerSettings.iOS.appleDeveloperTeamID = restore.IosTeamId;
                PlayerSettings.iOS.appleEnableAutomaticSigning = restore.IosAutomaticSigning;
            }
        }
    }
}
