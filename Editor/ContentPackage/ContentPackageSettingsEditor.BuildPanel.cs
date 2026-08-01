using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using Molca.ContentPackage;
using Molca.ContentPackage.Utilities;
using Molca.Editor.UI;

namespace Molca.Editor.ContentPackage
{
    /// <summary>
    /// Legacy build and verify panel for the Content Package Manager inspector.
    /// </summary>
    /// <remarks>
    /// The deploy half is gone. It shelled out to an external AWS/GCloud CLI through a storage
    /// provider asset holding bucket configuration, which the release protocol replaced: publishing
    /// now goes through the Hub's Content workspace, which uploads to short-lived presigned URLs and
    /// never puts a storage credential in the project.
    ///
    /// Leaving it as a second path was the defect, not the fix -- both existed, and the one with a
    /// button was the superseded one.
    ///
    /// Build and verify remain for the legacy schema-v1 delivery path, which is retained through the
    /// compatibility window named in the implementation plan (Phase 10 retires it).
    /// </remarks>
    public partial class ContentPackageSettingsEditor
    {
        // ── State ────────────────────────────────────────────────────────────

        private static readonly string BuildConfigPrefKey = "Molca.ContentPackage.BuildConfigGuid";

        private bool   _buildFoldout;
        private ContentPackageBuildConfig _buildConfig;
        private bool   _buildConfigLoaded;

        // Verify results: packageId → (bundleCount, totalBytes, error)
        private readonly Dictionary<string, (int bundles, long bytes, string error)> _verifyResults
            = new Dictionary<string, (int, long, string)>();


        // ── Entry point (called from OnInspectorGUI) ──────────────────────────

        private void DrawBuildPanel()
        {
            _buildFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_buildFoldout, "Build & Deploy");
            if (_buildFoldout)
            {
                EnsureBuildConfigLoaded();
                EditorGUILayout.Space(4);
                DrawBuildConfigPicker();
                if (_buildConfig != null)
                {
                    EditorGUILayout.Space(6);
                    DrawBuildSection();
                    EditorGUILayout.Space(6);
                    DrawVerifySection();
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Build config picker ───────────────────────────────────────────────

        private void EnsureBuildConfigLoaded()
        {
            if (_buildConfigLoaded) return;
            _buildConfigLoaded = true;

            var guid = MolcaEditorPrefs.GetString(BuildConfigPrefKey, "");
            if (!string.IsNullOrEmpty(guid))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    _buildConfig = AssetDatabase.LoadAssetAtPath<ContentPackageBuildConfig>(path);
            }
        }

        private void DrawBuildConfigPicker()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Build Config", GUILayout.Width(80));
            var prev = _buildConfig;
            _buildConfig = (ContentPackageBuildConfig)EditorGUILayout.ObjectField(
                _buildConfig, typeof(ContentPackageBuildConfig), false);

            if (_buildConfig != prev)
            {
                var path = _buildConfig != null ? AssetDatabase.GetAssetPath(_buildConfig) : "";
                var guid = string.IsNullOrEmpty(path) ? "" : AssetDatabase.AssetPathToGUID(path);
                MolcaEditorPrefs.SetString(BuildConfigPrefKey, guid);
            }

            if (_buildConfig == null && GUILayout.Button("Create", GUILayout.Width(54)))
            {
                var savePath = EditorUtility.SaveFilePanelInProject(
                    "Create Build Config", "ContentPackageBuildConfig", "asset",
                    "Choose where to save the build config.");
                if (!string.IsNullOrEmpty(savePath))
                {
                    var asset = ScriptableObject.CreateInstance<ContentPackageBuildConfig>();
                    AssetDatabase.CreateAsset(asset, savePath);
                    AssetDatabase.SaveAssets();
                    _buildConfig = asset;
                    MolcaEditorPrefs.SetString(BuildConfigPrefKey, AssetDatabase.AssetPathToGUID(savePath));
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── Build section ─────────────────────────────────────────────────────

        private void DrawBuildSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Build", EditorStyles.boldLabel);

            // Show/edit key paths inline
            var so = new SerializedObject(_buildConfig);
            so.Update();
            EditorGUILayout.PropertyField(so.FindProperty("localBuildPath"),  new GUIContent("Local Output"));
            EditorGUILayout.PropertyField(so.FindProperty("remoteLoadURL"),   new GUIContent("Remote Load URL"));
            so.ApplyModifiedProperties();

            var target     = EditorUserBuildSettings.activeBuildTarget.ToString();
            var resolvedPath = _buildConfig.ResolvedLocalBuildPath(target);
            EditorGUILayout.LabelField($"→ {resolvedPath}", _mutedStyle);

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Build Player Content"))
                RunBuild(fullBuild: true);

            GUI.enabled = File.Exists(ContentUpdatePath());
            if (GUILayout.Button("Build Content Update"))
                RunBuild(fullBuild: false);
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(
                "Build Player Content : full rebuild (first time or after structural changes).\n" +
                "Build Content Update : incremental rebuild of changed groups only.",
                MessageType.None);

            EditorGUILayout.EndVertical();
        }

        private void RunBuild(bool fullBuild)
        {
            var addrSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addrSettings == null)
            {
                EditorUtility.DisplayDialog("Build", "Addressables is not configured in this project.", "OK");
                return;
            }

            // Report a profile mismatch; never rewrite the shared profile asset on someone's behalf.
            string mismatch = DescribeProfileMismatch(addrSettings, _buildConfig);
            if (mismatch != null &&
                !EditorUtility.DisplayDialog("Addressables profile does not match the build config",
                    mismatch + "\n\nFix these in the Addressables Profiles window. Build anyway?",
                    "Build anyway", "Cancel"))
                return;

            _verifyResults.Clear();

            var options = new AddressablesBuildUtility.BuildOptions
            {
                ProfileName  = AddressablesBuildUtility.GetActiveProfileName(),
                CleanBuild   = false,
            };

            AddressablesBuildUtility.BuildResult result;

            if (fullBuild)
            {
                result = AddressablesBuildUtility.BuildAllContent(options);
            }
            else
            {
                var binPath = ContentUpdatePath();
                if (!File.Exists(binPath))
                {
                    EditorUtility.DisplayDialog("Build Content Update",
                        $"Previous build state file not found:\n{binPath}\n\nRun a full build first.", "OK");
                    return;
                }

                result = AddressablesBuildUtility.BuildContentUpdate(binPath, options);
            }

            if (result.Success && !string.IsNullOrEmpty(result.BuildPath))
            {
                AddressablesBuildUtility.WritePackageManifest(
                    result.BuildPath,
                    target as ContentPackageSettings,
                    addrSettings,
                    _buildConfig);
            }
            else if (!result.Success)
            {
                Debug.LogError($"[ContentPackage] Build failed: {result.ErrorMessage}");
            }

            Repaint();
        }

        /// <summary>
        /// Reports where the Addressables profile disagrees with the build config, without changing it.
        /// </summary>
        /// <remarks>
        /// This used to write <c>RemoteBuildPath</c>, <c>RemoteLoadPath</c>, <c>BuildRemoteCatalog</c>,
        /// and both catalog path variables into the shared <see cref="AddressableAssetSettings"/>
        /// asset on every build, then mark it dirty. That is version-controlled configuration shared
        /// by the whole team: a local build silently rewrote it to whoever built last, and the diff
        /// showed up in someone else's commit. Phase 4 removed the same class of write-back from
        /// <c>AddressablesBuildUtility</c>; this instance was missed.
        ///
        /// Reporting instead of writing means a mismatch is visible and the author decides. The
        /// Addressables Profiles window is where profile values belong.
        /// </remarks>
        private static string DescribeProfileMismatch(
            AddressableAssetSettings addrSettings, ContentPackageBuildConfig buildConfig)
        {
            if (addrSettings == null || buildConfig == null) return null;

            var profileId = addrSettings.activeProfileId;
            var problems = new List<string>();

            void Compare(string key, string expected)
            {
                string actual = addrSettings.profileSettings.GetValueByName(profileId, key);
                if (actual == null)
                    problems.Add($"Profile variable '{key}' does not exist.");
                else if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    problems.Add($"'{key}' is '{actual}', build config expects '{expected}'.");
            }

            Compare("RemoteBuildPath", buildConfig.localBuildPath);
            Compare("RemoteLoadPath", buildConfig.remoteLoadURL);

            if (!addrSettings.BuildRemoteCatalog)
                problems.Add("Build Remote Catalog is off, so no catalog will be produced to publish.");

            return problems.Count == 0 ? null : string.Join("\n", problems);
        }

        private static string ContentUpdatePath()
        {
            var addrSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addrSettings == null) return "";
            return UnityEditor.AddressableAssets.Build.ContentUpdateScript.GetContentStateDataPath(false, addrSettings);
        }

        // ── Verify section ────────────────────────────────────────────────────

        private void DrawVerifySection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Verify", EditorStyles.boldLabel);

            var target      = EditorUserBuildSettings.activeBuildTarget.ToString();
            var buildPath   = _buildConfig.ResolvedLocalBuildPath(target);
            bool buildExists = Directory.Exists(buildPath);

            if (!buildExists)
            {
                EditorGUILayout.HelpBox($"No build found at: {buildPath}", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            if (GUILayout.Button("Run Verification"))
                RunVerify(buildPath);

            if (_verifyResults.Count > 0)
            {
                EditorGUILayout.Space(2);
                foreach (var kvp in _verifyResults.OrderBy(k => k.Key))
                {
                    var (bundles, bytes, error) = kvp.Value;
                    bool ok = string.IsNullOrEmpty(error) && bundles > 0;

                    EditorGUILayout.BeginHorizontal();
                    var prevColor = GUI.color;
                    GUI.color = ok ? MolcaEditorColors.StatusOk : MolcaEditorColors.StatusError;
                    GUILayout.Label("●", GUILayout.Width(14));
                    GUI.color = prevColor;

                    EditorGUILayout.LabelField(kvp.Key, GUILayout.ExpandWidth(true));
                    if (ok)
                    {
                        EditorGUILayout.LabelField(
                            $"{bundles} bundle{(bundles == 1 ? "" : "s")}  ·  {SizeFormatter.Format(bytes)}",
                            _mutedStyle, GUILayout.Width(160));
                    }
                    else
                    {
                        EditorGUILayout.LabelField(
                            string.IsNullOrEmpty(error) ? "no bundles found" : error,
                            _errorStyle, GUILayout.Width(160));
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void RunVerify(string buildPath)
        {
            _verifyResults.Clear();
            var settings = this.target as ContentPackageSettings;
            if (settings == null) return;

            var addrSettings = AddressableAssetSettingsDefaultObject.Settings;

            foreach (var cfg in settings.packageConfigs)
            {
                // Hidden packages are verified too. Visibility affects presentation, not
                // correctness — a hidden *required* package is still built, uploaded, and installed,
                // and skipping it here meant the one package a player cannot run without was the one
                // package verification never looked at.
                if (string.IsNullOrEmpty(cfg.packageId))
                    continue;

                if (cfg.addressableLabels == null || cfg.addressableLabels.Length == 0)
                {
                    _verifyResults[cfg.packageId] = (0, 0, "no labels configured");
                    continue;
                }

                if (addrSettings == null)
                {
                    _verifyResults[cfg.packageId] = (0, 0, "Addressables not configured");
                    continue;
                }

                var (count, bytes) = AddressablesBuildUtility.GetPackageBundleInfo(cfg, addrSettings, buildPath);

                _verifyResults[cfg.packageId] = count > 0
                    ? (count, bytes, null)
                    : (0, 0, "no bundles found");
            }

            Repaint();
        }

    }
}
