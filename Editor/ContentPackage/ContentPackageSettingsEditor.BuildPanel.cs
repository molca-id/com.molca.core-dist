using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using Molca.ContentPackage;
using Molca.ContentPackage.Utilities;
using Molca.Editor.UI;
using Debug = UnityEngine.Debug;

namespace Molca.Editor.ContentPackage
{
    /// <summary>
    /// Build, verify, and deploy panel for the Content Package Manager inspector.
    /// </summary>
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

        // Running deploy process
        private Process        _deployProcess;
        private StringBuilder  _deployLog    = new StringBuilder();
        private bool           _deploying;
        private string         _deployStatus = "";
        private bool           _deployOk;

        // Process stdout/stderr callbacks fire on a thread-pool thread. They must not touch
        // EditorGUI state or call Repaint() directly, so they append to this buffer under a
        // lock and PollDeployProcess (running on the main thread via EditorApplication.update)
        // drains it into _deployLog and repaints.
        private readonly object        _deployLock    = new object();
        private readonly StringBuilder _deployPending = new StringBuilder();

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
                    EditorGUILayout.Space(6);
                    DrawDeploySection();
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

            // Sync Addressables profile paths from build config before delegating to utility
            SyncAddressablesPaths(addrSettings);

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

        private void SyncAddressablesPaths(AddressableAssetSettings addrSettings)
        {
            var profileId = addrSettings.activeProfileId;
            var target    = EditorUserBuildSettings.activeBuildTarget.ToString();

            void SetVar(string key, string value)
            {
                if (addrSettings.profileSettings.GetValueByName(profileId, key) != null)
                    addrSettings.profileSettings.SetValue(profileId, key, value);
                else
                    Debug.LogWarning($"[ContentPackage] Addressables profile variable '{key}' not found. Create it in the Addressables Profiles window.");
            }

            SetVar("RemoteBuildPath", _buildConfig.localBuildPath);
            SetVar("RemoteLoadPath",  _buildConfig.remoteLoadURL);

            // Ensure the remote catalog is written to the bundle output folder so it lands
            // alongside the bundles on CDN and can be discovered by WritePackageManifest.
            addrSettings.BuildRemoteCatalog = true;
            addrSettings.RemoteCatalogBuildPath.SetVariableByName(addrSettings, "RemoteBuildPath");
            addrSettings.RemoteCatalogLoadPath.SetVariableByName(addrSettings, "RemoteLoadPath");

            EditorUtility.SetDirty(addrSettings);
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
                if (!cfg.isVisible || string.IsNullOrEmpty(cfg.packageId))
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

        // ── Deploy section ────────────────────────────────────────────────────

        private void DrawDeploySection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var provider = _buildConfig.storageProvider;

            EditorGUILayout.LabelField(
                provider != null ? $"Deploy ({provider.DisplayName})" : "Deploy",
                EditorStyles.boldLabel);

            // Provider picker
            var cfgSo = new SerializedObject(_buildConfig);
            cfgSo.Update();
            EditorGUILayout.PropertyField(cfgSo.FindProperty("storageProvider"), new GUIContent("Storage Provider"));
            cfgSo.ApplyModifiedProperties();

            if (provider == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a Storage Provider asset (e.g. AWSS3StorageProvider) to enable deployment.\n" +
                    "Create one via Assets > Create > Molca > Content Package > Storage > …",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            // Draw provider-specific fields via its serialized object
            EditorGUILayout.Space(2);
            var providerSo = new SerializedObject(provider);
            providerSo.Update();
            var prop = providerSo.GetIterator();
            prop.NextVisible(true); // skip m_Script
            while (prop.NextVisible(false))
                EditorGUILayout.PropertyField(prop, true);
            providerSo.ApplyModifiedProperties();

            EditorGUILayout.Space(4);

            // Show resolved command
            var target    = EditorUserBuildSettings.activeBuildTarget.ToString();
            var localPath = _buildConfig.ResolvedLocalBuildPath(target);
            var cmd       = provider.BuildDeployCommand(localPath, target);
            EditorGUILayout.LabelField("Destination:", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(provider.GetDestinationDescription(target), _mutedStyle);
            EditorGUILayout.LabelField("Command:", EditorStyles.miniLabel);
            EditorGUILayout.SelectableLabel(cmd, EditorStyles.helpBox, GUILayout.Height(32));

            EditorGUILayout.Space(4);

            bool available = provider.CheckAvailability(out var availError);
            if (!available)
                EditorGUILayout.HelpBox(availError, MessageType.Warning);

            GUI.enabled = available && !_deploying;
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(_deploying ? "Deploying…" : $"Deploy ({provider.DisplayName})"))
                StartDeploy(provider, localPath, target);

            if (_deploying && GUILayout.Button("Abort", GUILayout.Width(54)))
                AbortDeploy();

            EditorGUILayout.EndHorizontal();
            GUI.enabled = true;

            // Status line
            if (!string.IsNullOrEmpty(_deployStatus))
            {
                var prevColor = GUI.color;
                GUI.color = _deploying ? MolcaEditorColors.StatusWarn
                          : _deployOk  ? MolcaEditorColors.StatusOk
                          :              MolcaEditorColors.StatusError;
                EditorGUILayout.LabelField(_deployStatus, EditorStyles.boldLabel);
                GUI.color = prevColor;
            }

            // Scrollable log
            if (_deployLog.Length > 0)
            {
                EditorGUILayout.LabelField("Log:", EditorStyles.miniLabel);
                EditorGUILayout.SelectableLabel(
                    _deployLog.ToString(),
                    EditorStyles.helpBox,
                    GUILayout.Height(120),
                    GUILayout.ExpandWidth(true));
            }

            EditorGUILayout.EndVertical();
        }

        // ── Deploy process management ─────────────────────────────────────────

        private void StartDeploy(ContentPackageStorageProvider provider, string localPath, string buildTarget)
        {
            if (!Directory.Exists(localPath))
            {
                EditorUtility.DisplayDialog("Deploy",
                    $"Build output folder not found:\n{localPath}\n\nRun a build first.", "OK");
                return;
            }

            _deployLog.Clear();
            _deployStatus = "Starting…";
            _deployOk     = false;
            _deploying    = true;

            var cmd = provider.BuildDeployCommand(localPath, buildTarget);
            _deployLog.AppendLine($"$ {cmd}");
            _deployLog.AppendLine();

            Debug.Log($"[ContentPackage] Deploy: {cmd}");

            _deployProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = provider.ExecutableName,
                    Arguments              = provider.BuildDeployArguments(localPath, buildTarget),
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                },
                EnableRaisingEvents = true
            };

            // These run off the main thread — buffer only; the poll loop drains and repaints.
            _deployProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_deployLock) _deployPending.AppendLine(e.Data);
            };
            _deployProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_deployLock) _deployPending.AppendLine("[err] " + e.Data);
            };

            _deployProcess.Start();
            _deployProcess.BeginOutputReadLine();
            _deployProcess.BeginErrorReadLine();

            _deployStatus = "Deploying…";
            EditorApplication.update += PollDeployProcess;
        }

        /// <summary>
        /// Runs on the main thread (EditorApplication.update). Drains any buffered process
        /// output into the visible log and, once the process exits, finalizes the deploy
        /// status. All EditorGUI/Repaint interaction happens here, never on the process
        /// callback thread.
        /// </summary>
        private void PollDeployProcess()
        {
            bool dirty = false;

            lock (_deployLock)
            {
                if (_deployPending.Length > 0)
                {
                    _deployLog.Append(_deployPending);
                    _deployPending.Clear();
                    dirty = true;
                }
            }

            if (_deployProcess == null || _deployProcess.HasExited)
            {
                EditorApplication.update -= PollDeployProcess;

                if (_deployProcess != null && _deploying)
                {
                    _deployOk     = _deployProcess.ExitCode == 0;
                    _deployStatus = _deployOk
                        ? "Deploy complete."
                        : $"Deploy failed (exit {_deployProcess.ExitCode}).";
                    Debug.Log($"[ContentPackage] {_deployStatus}");
                }

                _deploying = false;
                dirty = true;
            }

            if (dirty) Repaint();
        }

        private void AbortDeploy()
        {
            try { _deployProcess?.Kill(); }
            catch (System.Exception ex) { Debug.LogWarning($"[ContentPackage] Abort deploy failed: {ex.Message}"); }
            _deploying    = false;
            _deployStatus = "Aborted.";
            EditorApplication.update -= PollDeployProcess;
            Repaint();
        }
    }
}
