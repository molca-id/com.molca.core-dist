using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEditor.Build;
using UnityEditor;
using UnityEditor.Build.Reporting;
using Molca.Settings;
using Molca.Editor.Doctor;

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
        /// Builds the given profile.
        /// </summary>
        /// <returns>
        /// The <see cref="BuildReport"/> from BuildPipeline, or <c>null</c> when the build
        /// did not run (missing settings/profile, target-switch failure, or deferred
        /// editor build pending a target switch). CI callers must treat <c>null</c> or a
        /// non-Succeeded result as failure.
        /// </returns>
        public static BuildReport Build(string profileName)
        {
            return Build(profileName, null);
        }

        // Build-correctness Doctor checks run as the pre-build gate. The code-convention checks
        // (static singletons, runtime SO writes, etc.) are intentionally excluded — they scan all
        // scripts and belong to the full Doctor run, not a per-build gate.
        private static readonly HashSet<string> PreBuildCheckIds = new HashSet<string>
        {
            "build-scenes-valid",
            "version-settings-valid",
            "build-profile-valid",
            "unresolvable-scene-reference",
            "content-package-valid",
        };

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
                var issues = await MolcaDoctor.RunAllAsync(PreBuildCheckIds, cancellationToken: cancellationToken);
                var errors = issues.Where(i => i.Severity == DoctorSeverity.Error).ToList();
                if (errors.Count > 0)
                {
                    Debug.LogError(
                        $"[BuildManager] Build aborted: {errors.Count} pre-build Doctor error(s):\n  " +
                        string.Join("\n  ", errors.Select(e => e.ToString())));
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

            versionSettings.SyncToUnityPlayerSettings(force: true);
            versionSettings.SyncPlatformVersionCode(profile.target);
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
                return null;
            }

            // Get build profile
            var profile = buildSettings.GetProfile(profileName);
            if (profile == null)
            {
                Debug.LogError($"Build profile '{profileName}' not found!");
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
                    }
                    else
                    {
                        Debug.Log($"Switched active build target to {profile.target}. Build will resume automatically.");
                    }
                    return null;
                }
            }

            // Version name, platform version codes, and changelog append are applied by
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

            // Scene reference gate (R3.5): missing/duplicate/unresolvable scene-MonoBehaviour Ref Ids,
            // validated against a fresh scan of the build scenes (not the cached snapshot).
            var referenceErrors = SceneReferenceBuildValidator.Validate();
            if (referenceErrors.Count > 0)
            {
                Debug.LogError(
                    $"[BuildManager] Build aborted: {referenceErrors.Count} scene reference problem(s):\n  " +
                    string.Join("\n  ", referenceErrors));
                return null;
            }

            // Addressables content gate (11.4): build the content bundles the player ships before the
            // player itself, so the two never go out of sync. Opt-in per profile.
            if (profile.buildAddressablesFirst)
            {
                Debug.Log("[BuildManager] Building Addressables content before the player...");
                var contentResult = ContentPackage.AddressablesBuildUtility.BuildAllContent(
                    new ContentPackage.AddressablesBuildUtility.BuildOptions { CleanBuild = profile.cleanBuildCache });
                if (contentResult == null || !contentResult.Success)
                {
                    Debug.LogError("[BuildManager] Build aborted: Addressables content build failed. " +
                        (contentResult?.ErrorMessage ?? contentResult?.Message ?? "Unknown error."));
                    return null;
                }
                Debug.Log($"[BuildManager] Addressables content build succeeded ({contentResult.BuiltGroups.Count} group(s)).");
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
            string buildPath = GetBuildPath(buildTarget, projectName, fullVersionString, profile.outputPath, profile.name, profile.buildAppBundle);

            Debug.Log($"Starting {profileName} build...");
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

            // Get all enabled scenes from build settings
            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

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
                WriteBuildManifest(profile, report, buildPath, scenes, activeOptions, fullVersionString, versionSettings);
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

        private static string GetBuildPath(BuildTarget target, string projectName, string fullVersionString, string outputRoot, string profileName, bool androidAppBundle)
        {
            string fileName = $"{projectName}_{profileName}_{fullVersionString}";
            string buildDir = ResolveOutputPath(outputRoot);

            // Create build directory if it doesn't exist
            Directory.CreateDirectory(buildDir);

            switch (target)
            {
                case BuildTarget.StandaloneWindows64:
                    var winDir = Path.Combine(buildDir, $"Windows_{profileName}_{fullVersionString}");
                    Directory.CreateDirectory(winDir);
                    return Path.Combine(winDir, $"{projectName}.exe");
                case BuildTarget.Android:
                    var androidDir = Path.Combine(buildDir, $"Android_{profileName}");
                    Directory.CreateDirectory(androidDir);
                    var androidExtension = androidAppBundle ? "aab" : "apk";
                    return Path.Combine(androidDir, $"{fileName}.{androidExtension}");
                case BuildTarget.iOS:
                    var iosDir = Path.Combine(buildDir, $"iOS_{profileName}");
                    Directory.CreateDirectory(iosDir);
                    return iosDir;
                default:
                    return Path.Combine(buildDir, fileName);
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
            string fullVersion, VersionSettings versionSettings)
        {
            try
            {
                var dir = Directory.Exists(buildPath) ? buildPath : Path.GetDirectoryName(buildPath);
                if (string.IsNullOrEmpty(dir))
                    return;
                Directory.CreateDirectory(dir);

                string commit = string.Empty, branch = string.Empty;
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    if (GitLogReader.TryRunGit(projectRoot, "rev-parse --short HEAD", out var c))
                        commit = c.Trim();
                    if (GitLogReader.TryRunGit(projectRoot, "rev-parse --abbrev-ref HEAD", out var b))
                        branch = b.Trim();
                }

                var manifest = new BuildManifest
                {
                    product = Molca.MolcaProjectSettings.Instance.ProjectName,
                    profile = profile.name,
                    target = profile.target.ToString(),
                    version = fullVersion,
                    semanticVersion = versionSettings.GetSemanticVersion(),
                    buildNumber = versionSettings.GetBuildNumberString(),
                    commit = commit,
                    branch = branch,
                    timestampUtc = System.DateTime.UtcNow.ToString("o"),
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
                    Build(profileName, restoreTarget);
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
                    Debug.LogWarning(
                        "[BuildManager] Custom Android signing is enabled but the password environment " +
                        $"variables ('{profile.androidKeystorePassEnv}'/'{profile.androidKeyaliasPassEnv}') are empty. " +
                        "The build may fail to sign or fall back to the debug keystore.");
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