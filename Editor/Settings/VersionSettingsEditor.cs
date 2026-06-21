using UnityEngine;
using UnityEditor;
using Molca.Settings;

namespace Molca.Editor
{
    [CustomEditor(typeof(VersionSettings))]
    public class VersionSettingsEditor : UnityEditor.Editor
    {
        private SerializedProperty _major;
        private SerializedProperty _minor;
        private SerializedProperty _patch;
        private SerializedProperty _buildNumber;
        private SerializedProperty _preReleaseIdentifier;
        private SerializedProperty _buildMetadata;
        private SerializedProperty _autoSync;
        private SerializedProperty _autoIncrementBuildNumberOnBuild;
        private SerializedProperty _autoAppendChangelogOnBuild;
        private SerializedProperty _changelogPath;
        private SerializedProperty _includeGitCommitsInChangelog;
        private bool _showAdvanced;
        private bool _showHistory;
        private bool _showRelease;
        private bool _createGitTag;
        private ReleaseTool.BumpSuggestion? _bumpSuggestion;

        private void OnEnable()
        {
            _major = serializedObject.FindProperty("major");
            _minor = serializedObject.FindProperty("minor");
            _patch = serializedObject.FindProperty("patch");
            _buildNumber = serializedObject.FindProperty("buildNumber");
            _preReleaseIdentifier = serializedObject.FindProperty("preReleaseIdentifier");
            _buildMetadata = serializedObject.FindProperty("buildMetadata");
            _autoSync = serializedObject.FindProperty("autoSync");
            _autoIncrementBuildNumberOnBuild = serializedObject.FindProperty("autoIncrementBuildNumberOnBuild");
            _autoAppendChangelogOnBuild = serializedObject.FindProperty("autoAppendChangelogOnBuild");
            _changelogPath = serializedObject.FindProperty("changelogPath");
            _includeGitCommitsInChangelog = serializedObject.FindProperty("includeGitCommitsInChangelog");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawSummary();
            EditorGUILayout.Space(8);
            DrawVersionFields();
            EditorGUILayout.Space(8);
            DrawQuickActions();
            EditorGUILayout.Space(8);
            DrawReleaseSection();
            EditorGUILayout.Space(8);
            DrawAdvancedSettings();
            EditorGUILayout.Space(8);
            DrawVersionHistory();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawSummary()
        {
            var versionSettings = (VersionSettings)target;
            EditorGUILayout.LabelField("Current Version", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Version", versionSettings.GetVersionString());
                EditorGUILayout.TextField("Build", versionSettings.GetBuildNumberString());
                EditorGUILayout.TextField("Full", versionSettings.GetFullVersionString());

                if (!string.IsNullOrEmpty(versionSettings.GetPreReleaseIdentifier()) ||
                    !string.IsNullOrEmpty(versionSettings.GetBuildMetadata()))
                {
                    EditorGUILayout.TextField("Semantic", versionSettings.GetSemanticVersion());
                }
            }

            if (!versionSettings.IsValidVersion())
            {
                EditorGUILayout.HelpBox("Version settings are invalid. Please check your version numbers.", MessageType.Error);
            }
        }

        private void DrawVersionFields()
        {
            EditorGUILayout.LabelField("Version Fields", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.PropertyField(_major, new GUIContent("Major"));
            EditorGUILayout.PropertyField(_minor, new GUIContent("Minor"));
            EditorGUILayout.PropertyField(_patch, new GUIContent("Patch"));
            EditorGUILayout.PropertyField(_buildNumber, new GUIContent("Build"));
            EditorGUILayout.EndVertical();
        }

        private void DrawQuickActions()
        {
            var versionSettings = (VersionSettings)target;
            EditorGUILayout.LabelField("Quick Actions", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Increment Patch"))
            {
                versionSettings.IncrementPatch();
                EditorUtility.SetDirty(versionSettings);
            }
            if (GUILayout.Button("Increment Minor"))
            {
                versionSettings.IncrementMinor();
                EditorUtility.SetDirty(versionSettings);
            }
            if (GUILayout.Button("Increment Major"))
            {
                versionSettings.IncrementMajor();
                EditorUtility.SetDirty(versionSettings);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox("Build number and changelog entries are only updated when a build runs (Build Manager).", MessageType.Info);
            EditorGUILayout.EndVertical();
        }

        private void DrawReleaseSection()
        {
            _showRelease = EditorGUILayout.Foldout(_showRelease, "Release", true);
            if (!_showRelease)
            {
                return;
            }

            var versionSettings = (VersionSettings)target;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (GUILayout.Button("Suggest Bump From Commits"))
            {
                _bumpSuggestion = ReleaseTool.SuggestBump();
            }

            if (_bumpSuggestion.HasValue)
            {
                var suggestion = _bumpSuggestion.Value;
                var since = string.IsNullOrEmpty(suggestion.SinceRef) ? "recent history" : suggestion.SinceRef;
                EditorGUILayout.LabelField(
                    $"Suggested: {suggestion.Bump} ({suggestion.Commits.Count} commits since {since})",
                    EditorStyles.miniLabel);

                using (new EditorGUI.DisabledScope(suggestion.Bump == VersionBump.None))
                {
                    if (GUILayout.Button($"Apply {suggestion.Bump} Bump"))
                    {
                        ReleaseTool.ApplyBump(versionSettings, suggestion.Bump);
                        _bumpSuggestion = null;
                    }
                }
            }

            EditorGUILayout.Space(4);
            _createGitTag = EditorGUILayout.ToggleLeft(
                $"Create git tag (v{versionSettings.GetVersionString()})", _createGitTag);

            if (GUILayout.Button($"Create Release v{versionSettings.GetVersionString()}", GUILayout.Height(26)))
            {
                var confirm = EditorUtility.DisplayDialog("Create Release",
                    $"Release v{versionSettings.GetVersionString()}? This syncs PlayerSettings and appends a changelog entry" +
                    (_createGitTag ? ", then creates a local git tag (not pushed)." : "."),
                    "Release", "Cancel");
                if (confirm)
                {
                    var result = ReleaseTool.CreateRelease(versionSettings, _createGitTag);
                    EditorUtility.DisplayDialog(result.Success ? "Release" : "Release Failed", result.Message, "OK");
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawAdvancedSettings()
        {
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
            if (!_showAdvanced)
            {
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.PropertyField(_autoSync, new GUIContent("Auto-Sync", "Automatically synchronize Unity PlayerSettings version when build starts"));
            EditorGUILayout.PropertyField(_autoIncrementBuildNumberOnBuild, new GUIContent("Auto Increment Build", "Automatically increment build number when a build starts"));
            EditorGUILayout.PropertyField(_autoAppendChangelogOnBuild, new GUIContent("Auto Changelog", "Append a changelog entry when a build starts"));
            if (_autoAppendChangelogOnBuild.boolValue)
            {
                EditorGUILayout.PropertyField(_changelogPath, new GUIContent("Changelog Path", "Path relative to the project root"));
                EditorGUILayout.PropertyField(_includeGitCommitsInChangelog, new GUIContent("Include Git Commits", "Add commit messages since last build to changelog entry notes"));
            }

            var versionSettings = (VersionSettings)target;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("PlayerSettings Version", PlayerSettings.bundleVersion);
            }

            if (GUILayout.Button("Sync Now"))
            {
                versionSettings.SyncToUnityPlayerSettings(force: true);
                EditorUtility.SetDirty(versionSettings);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.PropertyField(_preReleaseIdentifier, new GUIContent("Pre-release"));
            EditorGUILayout.PropertyField(_buildMetadata, new GUIContent("Build Metadata"));
            EditorGUILayout.EndVertical();
        }

        private void DrawVersionHistory()
        {
            _showHistory = EditorGUILayout.Foldout(_showHistory, "History", true);
            if (!_showHistory)
            {
                return;
            }

            var versionSettings = (VersionSettings)target;
            var history = versionSettings.GetVersionHistory();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Loaded from: " + versionSettings.ChangelogPath, EditorStyles.miniLabel);
            if (history.Length == 0)
            {
                EditorGUILayout.LabelField("No history entries.");
                EditorGUILayout.EndVertical();
                return;
            }

            int startIndex = Mathf.Max(0, history.Length - 5);
            for (int i = startIndex; i < history.Length; i++)
            {
                var entry = history[i];
                EditorGUILayout.LabelField($"v{entry.version} • {entry.timestamp} • {entry.changeType}");
                if (!string.IsNullOrEmpty(entry.notes))
                {
                    EditorGUILayout.LabelField(entry.notes, EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Clear History"))
            {
                if (EditorUtility.DisplayDialog("Clear Version History",
                    "Are you sure you want to clear the version history? This action cannot be undone.",
                    "Clear", "Cancel"))
                {
                    versionSettings.ClearVersionHistory();
                }
            }
            EditorGUILayout.EndVertical();
        }
    }
}
