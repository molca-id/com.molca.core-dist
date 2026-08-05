using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Molca.Settings;

namespace Molca.Editor
{
    /// <summary>
    /// Pre-build half of the build version lifecycle: syncs the version string and platform version
    /// codes into <see cref="PlayerSettings"/> before the player is written.
    /// </summary>
    /// <remarks>
    /// This runs for every <c>BuildPipeline.BuildPlayer</c> invocation (Build Manager,
    /// <c>File &gt; Build</c>, and CI) because Unity discovers <see cref="IPreprocessBuildWithReport"/>
    /// by type, not by instance. It deliberately replaces the version-lifecycle calls that used to
    /// live in <c>BuildNotificationProvider</c>, so build-number increment and changelog append no
    /// longer require a notification provider asset to exist.
    /// <para>
    /// <see cref="callbackOrder"/> is the minimum value so the synced version is visible to every
    /// other build callback (e.g. notifications) that reads it during pre-process. Only idempotent
    /// work belongs at this order: it runs before the build gates get to abort, so anything here
    /// happens even for a build that never produces an artifact. Writing the version this asset
    /// already declares qualifies; recording history does not, which is why the changelog moved to
    /// <see cref="BuildVersionPostprocessor"/>.
    /// </para>
    /// </remarks>
    public sealed class BuildVersionPreprocessor : IPreprocessBuildWithReport
    {
        /// <summary>Runs before every other build callback so the synced version is visible to them.</summary>
        public int callbackOrder => MolcaBuildCallbackOrder.VersionSync;

        /// <summary>Syncs version data into PlayerSettings for this build.</summary>
        /// <param name="report">The Unity build report for the build about to run.</param>
        public void OnPreprocessBuild(BuildReport report)
        {
            var versionSettings = MolcaEditorSettings.Instance?.VersionSettings;
            if (versionSettings == null)
                return;

            versionSettings.SyncToUnityPlayerSettings();
            versionSettings.SyncPlatformVersionCode(report.summary.platform);

            EditorUtility.SetDirty(versionSettings);
        }
    }

    /// <summary>
    /// Writes the generated build-info asset that carries version and git provenance into the player.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split from <see cref="BuildVersionPreprocessor"/> so it can run <em>after</em> the build gates.
    /// This callback creates a real file under <c>Assets/</c> (and sometimes an <c>Assets/Resources</c>
    /// folder to hold it) which its postprocessor half deletes. A gate that aborts the build by
    /// throwing skips every postprocessor, so writing the asset at <c>int.MinValue</c> — before the
    /// gates run — meant an aborted build could leave generated files behind in the project.
    /// </para>
    /// <para>
    /// <b>The order must be above every gate, not merely above the early ones.</b> Core's gates were
    /// once spread either side of this callback — references at -1000 and localization at -900, but the
    /// network-catalog and colour-theme validators at <b>+100</b> — so a value in between looked like
    /// "after the gates" and was not. <see cref="MolcaBuildCallbackOrder"/> now names the bands and a
    /// test enforces them; this sits in <see cref="MolcaBuildCallbackOrder.GeneratedArtifacts"/>.
    /// </para>
    /// <para>
    /// A project's own gate ordered above this one could still abort after the write. That leaks one
    /// file, not a growing pile — the next build overwrites it — but it is the reason
    /// <see cref="BuildInfoAsset.Write"/> tolerates finding a stale asset already present.
    /// </para>
    /// </remarks>
    public sealed class BuildInfoAssetPreprocessor : IPreprocessBuildWithReport
    {
        /// <summary>Runs after every Molca build gate, so an aborted build leaves no generated file behind.</summary>
        public int callbackOrder => MolcaBuildCallbackOrder.GeneratedArtifacts;

        /// <summary>Embeds git provenance into the player for <c>Molca.BuildInfo</c> to read at runtime.</summary>
        /// <param name="report">The Unity build report for the build about to run.</param>
        public void OnPreprocessBuild(BuildReport report)
        {
            var versionSettings = MolcaEditorSettings.Instance?.VersionSettings;
            if (versionSettings == null)
                return;

            BuildInfoAsset.Write(versionSettings);
        }
    }

    /// <summary>
    /// Post-build half of the build version lifecycle: records the changelog entry and advances the
    /// build number, after a successful build. See <see cref="BuildVersionPreprocessor"/> for the
    /// overall design.
    /// </summary>
    /// <remarks>
    /// <see cref="callbackOrder"/> is the maximum value so this happens after every other post-process
    /// callback — a "build completed" reader (e.g. a notification) therefore reports the version that
    /// was actually built, not the next build's number.
    /// </remarks>
    public sealed class BuildVersionPostprocessor : IPostprocessBuildWithReport
    {
        /// <summary>Runs after every other build callback so readers see the built version, not the next one.</summary>
        public int callbackOrder => int.MaxValue;

        /// <summary>Records the build and advances the build number, but only when the build succeeded.</summary>
        /// <param name="report">The Unity build report for the completed build.</param>
        public void OnPostprocessBuild(BuildReport report)
        {
            // Always remove the generated build-info asset, regardless of build outcome — the
            // preprocessor wrote it for every build that got past the gates, including ones that then
            // fail or are cancelled.
            BuildInfoAsset.Cleanup();

            if (report.summary.result != BuildResult.Succeeded)
                return;

            var versionSettings = MolcaEditorSettings.Instance?.VersionSettings;
            if (versionSettings == null)
                return;

            bool isDevelopment = (report.summary.options & BuildOptions.Development) != 0;
            var notes = $"Built for {report.summary.platform} ({(isDevelopment ? "Development" : "Release")})";

            // Appends the changelog entry (naming the version just built) and then advances the build
            // number. Each half is a no-op unless enabled on the asset.
            versionSettings.NotifyBuildComplete(notes);

            EditorUtility.SetDirty(versionSettings);

            // Persisted here rather than left dirty. The build number is authored state that belongs in
            // version control, and an editor that reloads its domain (or is killed by a CI runner)
            // before Unity next flushes assets would drop the increment — a build number that silently
            // does not move is worse than one that is not tracked at all, because the store rejects the
            // second upload rather than the first.
            AssetDatabase.SaveAssets();
        }
    }

    /// <summary>
    /// Writes and removes the generated <c>Assets/Resources/MolcaBuildInfo.json</c> TextAsset that
    /// carries build provenance into the player for <see cref="Molca.BuildInfo"/> to read at runtime.
    /// </summary>
    /// <remarks>
    /// The asset is created during pre-process (so it is packaged into the player) and deleted during
    /// post-process. A <c>Resources</c> folder created solely for this asset is removed too. State is
    /// held statically; a build runs both callbacks within a single domain, so no reload intervenes.
    /// <para>
    /// <see cref="Write"/> is idempotent and tolerates a stale asset left by an earlier build that was
    /// aborted after the write — it overwrites rather than accumulating, so the worst case is one
    /// orphaned file rather than a growing set. See <see cref="BuildInfoAssetPreprocessor"/> for why
    /// that window exists at all.
    /// </para>
    /// </remarks>
    internal static class BuildInfoAsset
    {
        private const string ResourcesFolder = "Assets/Resources";
        private const string AssetPath = "Assets/Resources/MolcaBuildInfo.json";

        private static bool _createdResourcesFolder;

        /// <summary>Writes the build-info asset and imports it so the build includes it.</summary>
        public static void Write(Molca.Settings.VersionSettings versionSettings)
        {
            try
            {
                string commit = string.Empty, branch = string.Empty;
                var projectRoot = System.IO.Directory.GetParent(Application.dataPath)?.FullName;
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    if (GitLogReader.TryRunGit(projectRoot, "rev-parse --short HEAD", out var c))
                        commit = c.Trim();
                    if (GitLogReader.TryRunGit(projectRoot, "rev-parse --abbrev-ref HEAD", out var b))
                        branch = b.Trim();
                }

                MolcaProjectSettings project = MolcaProjectSettings.Instance;
                var data = new MolcaBuildInfoData
                {
                    version = versionSettings.GetVersionString(),
                    buildNumber = versionSettings.GetBuildNumberString(),
                    commit = commit,
                    branch = branch,
                    timestampUtc = System.DateTime.UtcNow.ToString("o"),
                    projectId = project?.ProjectId ?? string.Empty,
                    projectCode = project?.ProjectCode ?? string.Empty,
                };

                _createdResourcesFolder = !AssetDatabase.IsValidFolder(ResourcesFolder);
                if (_createdResourcesFolder)
                    AssetDatabase.CreateFolder("Assets", "Resources");

                System.IO.File.WriteAllText(AssetPath, JsonUtility.ToJson(data, true));
                AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BuildVersion] Failed to write runtime build info: {e.Message}");
            }
        }

        /// <summary>Deletes the generated asset (and the Resources folder if this writer created it and it is now empty).</summary>
        public static void Cleanup()
        {
            try
            {
                if (System.IO.File.Exists(AssetPath) || AssetDatabase.LoadAssetAtPath<TextAsset>(AssetPath) != null)
                    AssetDatabase.DeleteAsset(AssetPath);

                if (_createdResourcesFolder && AssetDatabase.IsValidFolder(ResourcesFolder))
                {
                    var remaining = AssetDatabase.FindAssets(string.Empty, new[] { ResourcesFolder });
                    if (remaining == null || remaining.Length == 0)
                        AssetDatabase.DeleteAsset(ResourcesFolder);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BuildVersion] Failed to clean up runtime build info: {e.Message}");
            }
            finally
            {
                _createdResourcesFolder = false;
            }
        }
    }
}
