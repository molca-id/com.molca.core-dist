using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using Molca.Settings;

namespace Molca.Editor
{
    [CustomEditor(typeof(BuildSettings))]
    public class BuildSettingsEditor : UnityEditor.Editor
    {
        private SerializedProperty _profiles;
        private ReorderableList _profilesList;

        private void OnEnable()
        {
            _profiles = serializedObject.FindProperty("profiles");

            _profilesList = new ReorderableList(serializedObject, _profiles, true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Build Profiles"),
                drawElementCallback = DrawProfileListElement,
                onAddCallback = list =>
                {
                    int index = _profiles.arraySize;
                    _profiles.InsertArrayElementAtIndex(index);
                    var element = _profiles.GetArrayElementAtIndex(index);
                    element.FindPropertyRelative("name").stringValue = "New Profile";
                    element.FindPropertyRelative("target").enumValueIndex = (int)BuildTarget.StandaloneWindows64;
                    element.FindPropertyRelative("outputPath").stringValue = "Builds";
                }
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawSummaryHeader();

            _profilesList.DoLayoutList();

            if (_profilesList.index >= 0 && _profilesList.index < _profiles.arraySize)
            {
                EditorGUILayout.Space(6);
                DrawProfileDetails(_profiles.GetArrayElementAtIndex(_profilesList.index));
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawSummaryHeader()
        {
            var versionSettings = MolcaEditorSettings.Instance?.VersionSettings;
            var activeTarget = EditorUserBuildSettings.activeBuildTarget;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                "Version",
                versionSettings != null
                    ? $"{versionSettings.GetFullVersionString()}   (active target: {activeTarget})"
                    : $"(no VersionSettings assigned)   active target: {activeTarget}");

            if (GUILayout.Button($"Build All ({activeTarget})"))
            {
                BuildAllForActiveTarget(activeTarget);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        private void BuildAllForActiveTarget(BuildTarget activeTarget)
        {
            var buildSettings = (BuildSettings)target;
            var matching = new System.Collections.Generic.List<string>();
            var skipped = new System.Collections.Generic.List<string>();
            foreach (var profile in buildSettings.Profiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.name))
                    continue;
                if (profile.target == activeTarget)
                    matching.Add(profile.name);
                else
                    skipped.Add($"{profile.name} ({profile.target})");
            }

            if (matching.Count == 0)
            {
                EditorUtility.DisplayDialog("Build All",
                    $"No profiles target the active build target ({activeTarget}).", "OK");
                return;
            }

            var message = $"Build {matching.Count} profile(s) for {activeTarget}?";
            if (skipped.Count > 0)
            {
                message += $"\n\n{skipped.Count} profile(s) targeting other platforms will be skipped — the " +
                    "editor builds one target at a time; use CI for multi-target builds:\n  " + string.Join("\n  ", skipped);
            }

            if (!EditorUtility.DisplayDialog("Build All", message, "Build All", "Cancel"))
                return;

            // Same-target profiles build synchronously (no deferred target switch), so a simple
            // sequential loop is safe. Deferred via delayCall so the GUI event finishes first.
            var names = matching.ToArray();
            EditorApplication.delayCall += () => BuildAllGated(names);
        }

        // async void is the Unity event-handler entry-point exception in the async contract; the body
        // is wrapped so exceptions cannot escape into Unity's synchronization context.
        private static async void BuildProfileGated(string profileName)
        {
            try { await BuildManager.BuildAsync(profileName); }
            catch (System.Exception e) { Debug.LogError($"[BuildManager] Build failed: {e}"); }
        }

        private static async void BuildAllGated(string[] profileNames)
        {
            try
            {
                for (int i = 0; i < profileNames.Length; i++)
                {
                    // Gate once for the batch; same-target profiles never trigger a deferred switch.
                    var report = await BuildManager.BuildAsync(profileNames[i], runPreBuildChecks: i == 0);
                    if (i == 0 && report == null)
                    {
                        Debug.LogWarning("[BuildManager] Build All aborted (pre-build checks failed or the first build did not run).");
                        return;
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BuildManager] Build All failed: {e}");
            }
        }

        private void DrawProfileListElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            var element = _profiles.GetArrayElementAtIndex(index);
            var name = element.FindPropertyRelative("name");
            var target = element.FindPropertyRelative("target");

            rect.y += 2;
            var nameRect = new Rect(rect.x, rect.y, rect.width * 0.6f, EditorGUIUtility.singleLineHeight);
            var targetRect = new Rect(rect.x + rect.width * 0.62f, rect.y, rect.width * 0.38f, EditorGUIUtility.singleLineHeight);

            name.stringValue = EditorGUI.TextField(nameRect, name.stringValue);
            EditorGUI.PropertyField(targetRect, target, GUIContent.none);
        }

        private void DrawProfileDetails(SerializedProperty profile)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(profile.FindPropertyRelative("name").stringValue, EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("target"), new GUIContent("Target"));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("outputPath"), new GUIContent("Output Path"));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("runtimeManager"), new GUIContent("Runtime Manager"));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("globalSettings"), new GUIContent("Global Settings"));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("applicationIdentifierOverride"), new GUIContent("Package Name Override", "Android package name / iOS bundle ID. Only used for Android and iOS builds."));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Build Options", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(GUILayout.MinWidth(200));
            EditorGUILayout.LabelField("Development", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("developmentBuild"), new GUIContent("Development Build"));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("allowDebugging"), new GUIContent("Allow Debugging"));
            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("Performance", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("il2cpp"), new GUIContent("IL2CPP"));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("compress"), new GUIContent("Compress"));
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(GUILayout.MinWidth(200));
            EditorGUILayout.LabelField("Build Behavior", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("autoRunPlayer"), new GUIContent("Auto Run Player"));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("showBuiltPlayer"), new GUIContent("Show Built Player"));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("cleanBuildCache"), new GUIContent("Clean Build Cache"));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("restoreOriginalTarget"), new GUIContent("Restore Original Target"));
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(GUILayout.MinWidth(200));
            EditorGUILayout.LabelField("Debugging", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("connectWithProfiler"), new GUIContent("Connect With Profiler"));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("deepProfiling"), new GUIContent("Deep Profiling"));
            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("Advanced", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("strictMode"), new GUIContent("Strict Mode"));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("detailedBuildReport"), new GUIContent("Detailed Build Report"));
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Content", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("buildAddressablesFirst"), new GUIContent("Build Addressables First", "Build Addressables content before the player; aborts the build if the content build fails."));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Android", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("buildAppBundle"), new GUIContent("Build App Bundle (AAB)", "Output an .aab instead of .apk. Required for Google Play uploads."));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("androidArchitectures"), new GUIContent("Architectures"));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Signing", EditorStyles.boldLabel);
            var useCustomSigning = profile.FindPropertyRelative("useCustomSigning");
            EditorGUILayout.PropertyField(useCustomSigning, new GUIContent("Use Custom Signing"));
            if (useCustomSigning.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("Android", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(profile.FindPropertyRelative("androidKeystorePath"), new GUIContent("Keystore Path"));
                EditorGUILayout.PropertyField(profile.FindPropertyRelative("androidKeyaliasName"), new GUIContent("Key Alias Name"));
                EditorGUILayout.PropertyField(profile.FindPropertyRelative("androidKeystorePassEnv"), new GUIContent("Keystore Pass Env Var"));
                EditorGUILayout.PropertyField(profile.FindPropertyRelative("androidKeyaliasPassEnv"), new GUIContent("Key Alias Pass Env Var"));
                EditorGUILayout.LabelField("iOS", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(profile.FindPropertyRelative("iosTeamId"), new GUIContent("Apple Team ID"));
                EditorGUILayout.PropertyField(profile.FindPropertyRelative("iosAutomaticSigning"), new GUIContent("Automatic Signing"));
                EditorGUILayout.HelpBox("Passwords are read from the named environment variables at build time and are never stored in this asset.", MessageType.Info);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative("defineSymbols"), new GUIContent("Define Symbols"));

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply Profile", GUILayout.Height(28)))
            {
                var profileName = profile.FindPropertyRelative("name").stringValue;
                serializedObject.ApplyModifiedProperties();
                EditorApplication.delayCall += () => BuildManager.ApplyProfile(profileName);
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Build This Profile", GUILayout.Height(28)))
            {
                var profileName = profile.FindPropertyRelative("name").stringValue;
                serializedObject.ApplyModifiedProperties();
                EditorApplication.delayCall += () => BuildProfileGated(profileName);
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Duplicate", GUILayout.Height(28), GUILayout.Width(90)))
            {
                int index = _profilesList.index;
                // InsertArrayElementAtIndex copies the element at the index, giving an exact duplicate.
                _profiles.InsertArrayElementAtIndex(index);
                var duplicate = _profiles.GetArrayElementAtIndex(index + 1);
                var nameProp = duplicate.FindPropertyRelative("name");
                nameProp.stringValue = $"{nameProp.stringValue} Copy";
                serializedObject.ApplyModifiedProperties();
                _profilesList.index = index + 1;
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }
    }
}
