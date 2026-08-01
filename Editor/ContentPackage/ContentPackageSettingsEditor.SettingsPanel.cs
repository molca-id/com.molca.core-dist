using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ContentPackage;
using Molca.ContentPackage.Core;
using Molca.Editor.UI;

namespace Molca.Editor.ContentPackage
{
    /// <summary>
    /// Collapsible settings panel (bottom of the inspector) and helper operations.
    /// </summary>
    public partial class ContentPackageSettingsEditor
    {
        private bool _settingsFoldout;

        // ── Settings panel ───────────────────────────────────────────────────

        private void DrawSettingsPanel()
        {
            _settingsFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_settingsFoldout, "System Settings");
            if (_settingsFoldout)
            {
                EditorGUILayout.Space(2);
                DrawReleaseProtocolSection();
                EditorGUILayout.Space(4);
                DrawRemoteSection();
                EditorGUILayout.Space(4);

                DrawDownloadSection();
                EditorGUILayout.Space(6);
                DrawToolsSection();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        /// <summary>
        /// The <c>contentRelease</c> protocol settings.
        /// </summary>
        /// <remarks>
        /// Drawn above the legacy remote section because it supersedes it. The two are alternatives
        /// at runtime, so the panel says which one is in force rather than showing both as if they
        /// combined.
        /// </remarks>
        private void DrawReleaseProtocolSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Release Protocol", EditorStyles.boldLabel);

            var enabled = Prop("_enableReleaseProtocol");
            EditorGUILayout.PropertyField(enabled, new GUIContent(
                "Use Release Protocol",
                "Resolve content through Molca's signed release protocol instead of a directly " +
                "configured catalog URL. Content is verified before it can change local state."));

            if (!enabled.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "Off: this project uses the legacy catalog URL below.", MessageType.None);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.PropertyField(Prop("_contentServiceId"), new GUIContent(
                "Content Service",
                "Network catalog service id for the Molca content host. The origin is resolved from " +
                "the catalog, so the build token can only reach a host this project authorized."));
            EditorGUILayout.PropertyField(Prop("_contentPathPrefix"), new GUIContent("API Path Prefix"));
            EditorGUILayout.PropertyField(Prop("_trustedReleaseKeys"), new GUIContent(
                "Trusted Signing Keys",
                "Public keys permitted to sign a release for this project."), true);

            var keys = Prop("_trustedReleaseKeys");
            if (keys.arraySize == 0)
            {
                // Not a warning about aesthetics: with no key, verification refuses every release,
                // and the failure at runtime reads like tampering rather than like missing setup.
                EditorGUILayout.HelpBox(
                    "No trusted signing keys. Every release will be refused as untrusted at runtime.",
                    MessageType.Error);
            }

            EditorGUILayout.HelpBox(
                "The legacy catalog URL below is ignored while this is on.", MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private void DrawRemoteSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Remote Content (legacy)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(Prop("_checkForCatalogUpdates"), new GUIContent("Check for Updates"));
            EditorGUILayout.Space(2);

            // Editable, and no longer claimed to be automatic. These used to be disabled with a
            // note saying a build populates them -- a build-time write-back that was removed when
            // the build stopped mutating this shared asset. The note outlived the behaviour, so an
            // author saw an empty field, an explanation for it that was false, and no way to fix it.
            EditorGUILayout.PropertyField(Prop("_remoteCatalogUrl"),          new GUIContent("Catalog URL"));
            EditorGUILayout.PropertyField(Prop("_remotePackagesManifestUrl"), new GUIContent("Packages Manifest URL"));
            EditorGUILayout.HelpBox(
                "Set these for the legacy delivery path. Builds no longer write them back — a build " +
                "would otherwise change version-controlled settings to whoever built last.",
                MessageType.None);
            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(Prop("_enableContentVersioning"), new GUIContent(
                "Enable Content Versioning",
                "When enabled, packages.json is expected to be a ContentVersionIndex (schema v2) " +
                "and multi-version content switching is available. " +
                "When disabled, packages.json is treated as a flat manifest (schema v1) — identical to pre-versioning behaviour."));

            if (Prop("_enableContentVersioning").boolValue)
            {
                EditorGUILayout.PropertyField(Prop("_appVersion"), new GUIContent(
                    "App Version",
                    "Used to filter content versions from the CDN version index (schema v2) by their minAppVersion/maxAppVersion range. " +
                    "Leave empty to use Application.version at runtime. " +
                    "Note: content versions should only set maxAppVersion when they are known to break on a newer app version — leaving it empty means the content works on all future app versions."));
                if (string.IsNullOrEmpty(Prop("_appVersion").stringValue))
                {
                    EditorGUILayout.HelpBox(
                        $"Using Application.version (\"{PlayerSettings.bundleVersion}\") for content compatibility checks. Set this field to override.",
                        MessageType.None);
                }
            }

            if (_cloudStatus != null)
            {
                EditorGUILayout.Space(4);
                DrawCloudStatusDetail();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawCloudStatusDetail()
        {
            var state = _cloudStatus.State;

            // State row
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Cloud Status");
            var (dot, label, color) = state switch
            {
                CloudConnectionState.Connected     => ("●", "Connected",    MolcaEditorColors.StatusOk),
                CloudConnectionState.Unreachable   => ("●", "Unreachable",  MolcaEditorColors.StatusError),
                CloudConnectionState.NotConfigured => ("●", "Not Configured", MolcaEditorColors.StatusWarn),
                _                                  => ("●", "Unknown",      MolcaEditorColors.StatusIdle),
            };
            var style = new GUIStyle(EditorStyles.miniLabel);
            style.normal.textColor = color;
            EditorGUILayout.LabelField($"{dot}  {label}", style);
            EditorGUILayout.EndHorizontal();

            // Last sync
            using (new EditorGUI.DisabledScope(true))
            {
                var syncText = _cloudStatus.LastSyncTime.HasValue
                    ? _cloudStatus.LastSyncTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : "—";
                EditorGUILayout.LabelField("Last Sync", syncText);

                if (!string.IsNullOrEmpty(_cloudStatus.ManifestGeneratedAt))
                    EditorGUILayout.LabelField("Manifest Built", _cloudStatus.ManifestGeneratedAt);

                if (_cloudStatus.RemotePackageCount > 0)
                    EditorGUILayout.LabelField("Remote Packages", _cloudStatus.RemotePackageCount.ToString());
            }

            // Error message
            if (state == CloudConnectionState.Unreachable && !string.IsNullOrEmpty(_cloudStatus.ErrorMessage))
                EditorGUILayout.HelpBox(_cloudStatus.ErrorMessage, MessageType.Warning);
        }

        private void DrawDownloadSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Download & Retry", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(Prop("_maxRetryAttempts"), new GUIContent("Max Retry Attempts"));
            EditorGUILayout.PropertyField(Prop("_enableVerboseLogging"),        new GUIContent("Verbose Logging"));
            EditorGUILayout.EndVertical();
        }

        private void DrawToolsSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Import Manifest JSON")) ImportManifest();
            if (GUILayout.Button("Validate Configs"))     ValidateAllPackages();
            if (GUILayout.Button("Export JSON"))          ExportSettingsAsJson();
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Reset Settings to Defaults"))
            {
                if (EditorUtility.DisplayDialog("Reset Settings",
                    "Reset all system settings to defaults?\nPackage configs are not affected.", "Reset", "Cancel"))
                {
                    var s    = target as ContentPackageSettings;
                    var saved = new List<ContentPackageSettings.PackageConfig>(s.packageConfigs);
                    s.ResetToDefaults();
                    s.packageConfigs = saved;
                    EditorUtility.SetDirty(target);
                }
            }

            EditorGUILayout.EndVertical();
        }

        // ── Import / Export ──────────────────────────────────────────────────

        [Serializable]
        private class ImportableManifest
        {
            public string   packageId;
            public string   displayName;
            public string   version        = "1.0.0";
            public string   description;
            public string   author;
            public bool     isRequired     = false;
            public string[] addressableLabels;
            public string[] dependencies;
        }

        private void ImportManifest()
        {
            var path = EditorUtility.OpenFilePanel("Import Package Manifest", "", "json");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var json     = System.IO.File.ReadAllText(path);
                var manifest = JsonUtility.FromJson<ImportableManifest>(json);

                if (manifest == null || string.IsNullOrEmpty(manifest.packageId))
                {
                    EditorUtility.DisplayDialog("Import Failed", "Missing or empty packageId in JSON.", "OK");
                    return;
                }

                var settings = target as ContentPackageSettings;

                if (settings.GetPackageConfig(manifest.packageId) != null)
                {
                    if (!EditorUtility.DisplayDialog("Package Exists",
                        $"'{manifest.packageId}' already exists. Overwrite?", "Overwrite", "Cancel"))
                        return;
                    settings.packageConfigs.RemoveAll(p => p.packageId == manifest.packageId);
                }

                var deps = (manifest.dependencies ?? Array.Empty<string>())
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Select(d => new ContentPackageSettings.PackageDependency { packageId = d })
                    .ToArray();

                settings.packageConfigs.Add(new ContentPackageSettings.PackageConfig
                {
                    packageId        = manifest.packageId,
                    displayName      = manifest.displayName,
                    isVisible        = true,
                    isRequired       = manifest.isRequired,
                    metadata = new ContentPackageSettings.PackageMetadata
                    {
                        version     = manifest.version,
                        description = manifest.description,
                        author      = manifest.author,
                    },
                    dependencies     = deps,
                    addressableLabels = manifest.addressableLabels ?? Array.Empty<string>()
                });

                _selectedPackageId = manifest.packageId;
                EditorUtility.SetDirty(target);
                Repaint();

                EditorUtility.DisplayDialog("Import Successful",
                    $"Imported: {manifest.displayName ?? manifest.packageId}\n" +
                    $"Keys: {(manifest.addressableLabels?.Length ?? 0)}  Deps: {deps.Length}", "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Import Failed", ex.Message, "OK");
                Debug.LogError($"[ContentPackage] Manifest import failed: {ex}");
            }
        }

        private void ValidateAllPackages()
        {
            // Runs the same engine as the Doctor, automation, and the publish flow. It used to run
            // ContentPackageSettings.ValidateConfigurations, which checked a different and smaller
            // set, so this button could say "all valid" about a configuration the Doctor rejected.
            var settings = (ContentPackageSettings)target;
            var report = Molca.ContentPackage.Editor.ContentValidation.ValidateSettings(settings.packageConfigs);

            // Said plainly rather than implied: this button does not build, so it cannot know
            // whether a package actually ships anything.
            const string scope = "\n\nThis checks the configuration only. Content is verified when you " +
                                 "build a release.";

            if (report.Issues.Count == 0)
            {
                EditorUtility.DisplayDialog("Validation", "All configurations are valid." + scope, "OK");
                return;
            }

            var lines = report.Issues.Select(issue =>
            {
                string marker = issue.Severity == Molca.ContentPackage.Editor.ContentIssueSeverity.Error ? "✕"
                    : issue.Severity == Molca.ContentPackage.Editor.ContentIssueSeverity.Warning ? "!" : "·";
                return string.IsNullOrEmpty(issue.PackageId)
                    ? $"{marker} {issue.Message}"
                    : $"{marker} [{issue.PackageId}] {issue.Message}";
            });

            string title = report.CanPublish ? "Validation — no blocking problems" : "Validation Errors";
            string summary = $"{report.ErrorCount} error(s), {report.WarningCount} warning(s).\n\n";
            EditorUtility.DisplayDialog(title, summary + string.Join("\n\n", lines) + scope, "OK");
        }

        private void ExportSettingsAsJson()
        {
            var path = EditorUtility.SaveFilePanel("Export Settings", "", "ContentPackageSettings.json", "json");
            if (string.IsNullOrEmpty(path)) return;

            System.IO.File.WriteAllText(path, JsonUtility.ToJson(target, true));
            EditorUtility.DisplayDialog("Export Complete", $"Saved to:\n{path}", "OK");
        }

        // ── Utility ──────────────────────────────────────────────────────────

        private SerializedProperty Prop(string name) => serializedObject.FindProperty(name);


    }
}
