using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Molca.Settings;

namespace Molca.Editor
{
    /// <summary>
    /// Pre-build half of the build version lifecycle: appends the changelog entry and syncs the
    /// version string and platform version codes into <see cref="PlayerSettings"/> before the
    /// player is written.
    /// </summary>
    /// <remarks>
    /// This runs for every <c>BuildPipeline.BuildPlayer</c> invocation (Build Manager,
    /// <c>File &gt; Build</c>, and CI) because Unity discovers <see cref="IPreprocessBuildWithReport"/>
    /// by type, not by instance. It deliberately replaces the version-lifecycle calls that used to
    /// live in <c>BuildNotificationProvider</c>, so build-number increment and changelog append no
    /// longer require a notification provider asset to exist.
    /// <para>
    /// <see cref="callbackOrder"/> is the minimum value so the synced version is visible to every
    /// other build callback (e.g. notifications) that reads it during pre-process.
    /// </para>
    /// </remarks>
    public sealed class BuildVersionPreprocessor : IPreprocessBuildWithReport
    {
        /// <summary>Runs before every other build callback so the synced version is visible to them.</summary>
        public int callbackOrder => int.MinValue;

        /// <summary>Appends the changelog entry and syncs version data into PlayerSettings for this build.</summary>
        /// <param name="report">The Unity build report for the build about to run.</param>
        public void OnPreprocessBuild(BuildReport report)
        {
            var versionSettings = MolcaEditorSettings.Instance?.VersionSettings;
            if (versionSettings == null)
                return;

            var target = report.summary.platform;
            bool isDevelopment = (report.summary.options & BuildOptions.Development) != 0;
            var notes = $"Build started for {target} (Development: {isDevelopment})";

            // No-op unless AutoAppendChangelogOnBuild is enabled on the asset.
            versionSettings.PrepareForBuild(notes);

            versionSettings.SyncToUnityPlayerSettings(force: true);
            versionSettings.SyncPlatformVersionCode(target);

            EditorUtility.SetDirty(versionSettings);

            // Embed git provenance into the player so it is readable at runtime via Molca.BuildInfo.
            BuildInfoAsset.Write(versionSettings);
        }
    }

    /// <summary>
    /// Post-build half of the build version lifecycle: increments the build number after a
    /// successful build. See <see cref="BuildVersionPreprocessor"/> for the overall design.
    /// </summary>
    /// <remarks>
    /// <see cref="callbackOrder"/> is the maximum value so the increment happens after every other
    /// post-process callback — a "build completed" reader (e.g. a notification) therefore reports the
    /// version that was actually built, not the next build's number.
    /// </remarks>
    public sealed class BuildVersionPostprocessor : IPostprocessBuildWithReport
    {
        /// <summary>Runs after every other build callback so readers see the built version, not the next one.</summary>
        public int callbackOrder => int.MaxValue;

        /// <summary>Advances the build number, but only when the build actually succeeded.</summary>
        /// <param name="report">The Unity build report for the completed build.</param>
        public void OnPostprocessBuild(BuildReport report)
        {
            // Always remove the generated build-info asset, regardless of build outcome — the
            // preprocessor wrote it for every build, including ones that fail or are cancelled.
            BuildInfoAsset.Cleanup();

            if (report.summary.result != BuildResult.Succeeded)
                return;

            var versionSettings = MolcaEditorSettings.Instance?.VersionSettings;
            if (versionSettings == null)
                return;

            // No-op unless AutoIncrementBuildNumberOnBuild is enabled on the asset.
            versionSettings.NotifyBuildComplete();
            EditorUtility.SetDirty(versionSettings);
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

                var data = new MolcaBuildInfoData
                {
                    version = versionSettings.GetVersionString(),
                    buildNumber = versionSettings.GetBuildNumberString(),
                    commit = commit,
                    branch = branch,
                    timestampUtc = System.DateTime.UtcNow.ToString("o"),
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
