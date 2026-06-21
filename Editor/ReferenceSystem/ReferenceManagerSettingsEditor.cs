using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Molca.ReferenceSystem;

namespace Molca.Editor
{
    /// <summary>
    /// Drops Unity's noisy light-probe warning when additively loading many scenes during batch scans.
    /// </summary>
    internal sealed class SuppressProbeAppendWarningLogHandler : ILogHandler
    {
        private const string ProbeWarningSubstring = "Inconsistent counts in appended probe data";
        private readonly ILogHandler _inner;

        public SuppressProbeAppendWarningLogHandler(ILogHandler inner)
        {
            _inner = inner;
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            string msg;
            try
            {
                msg = string.Format(format ?? string.Empty, args ?? Array.Empty<object>());
            }
            catch (FormatException)
            {
                msg = format ?? string.Empty;
            }

            if (msg.IndexOf(ProbeWarningSubstring, StringComparison.Ordinal) >= 0)
                return;

            _inner.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            _inner.LogException(exception, context);
        }
    }

    /// <summary>
    /// Custom editor for ReferenceManagerSettings.
    /// Provides UI for scanning and managing references.
    /// Hooks pre-scene-save for optional RefId validation/fix in the scene being saved.
    /// </summary>
    [CustomEditor(typeof(ReferenceManagerSettings))]
    [InitializeOnLoad]
    public class ReferenceManagerSettingsEditor : UnityEditor.Editor
    {
        private sealed class ProjectScanStatistics
        {
            public int ScriptableObjectReferenceables;
            public int PrefabAssetsScanned;
            public int ReferenceablesInPrefabs;
            public int ScenesProcessed;
            public int ReferenceablesInScenes;
        }

        static ReferenceManagerSettingsEditor()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        /// <summary>
        /// Called just before a scene is saved. Runs RefId validation/fix for the scene being saved when enabled in settings.
        /// </summary>
        private static void OnSceneSaving(Scene scene, string path)
        {
            var settings = GetSettingsSilent();
            if (settings == null || !settings.ValidateRefIdsOnSceneSave)
                return;

            var editor = CreateEditor(settings) as ReferenceManagerSettingsEditor;
            if (editor != null)
            {
                editor.ValidateAndFixRefIdsInScene(scene, settings);
                DestroyImmediate(editor);
            }
        }

        /// <summary>
        /// Get settings without showing dialogs (for silent use from scene save hook).
        /// </summary>
        private static ReferenceManagerSettings GetSettingsSilent()
        {
            var settings = ReferenceManagerSettings.Instance;
            if (settings != null)
                return settings;
            var guids = AssetDatabase.FindAssets("t:ReferenceManagerSettings");
            if (guids.Length > 0)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<ReferenceManagerSettings>(assetPath);
            }
            return null;
        }

        #region Menu Items

        [MenuItem("Molca/Reference System/Scan Project for References", priority = 30)]
        public static void MenuScanProject()
        {
            var settings = GetOrCreateSettings();
            if (settings != null)
            {
                var editor = CreateEditor(settings) as ReferenceManagerSettingsEditor;
                editor.ScanProjectReferences(settings);
                DestroyImmediate(editor);
            }
        }

        [MenuItem("Molca/Reference System/Refresh Active Scenes", priority = 31)]
        public static void MenuRefreshActiveScenes()
        {
            var settings = GetOrCreateSettings();
            if (settings != null)
            {
                var editor = CreateEditor(settings) as ReferenceManagerSettingsEditor;
                editor.ScanActiveScenes(settings);
                DestroyImmediate(editor);
            }
        }

        [MenuItem("Molca/Reference System/Clear All References", priority = 32)]
        public static void MenuClearReferences()
        {
            var settings = GetOrCreateSettings();
            if (settings != null)
            {
                if (EditorUtility.DisplayDialog("Clear References",
                    "This will remove all scanned reference data. Are you sure?",
                    "Yes", "Cancel"))
                {
                    var editor = CreateEditor(settings) as ReferenceManagerSettingsEditor;
                    editor.ClearAllReferences(settings);
                    DestroyImmediate(editor);
                }
            }
        }

        private static ReferenceManagerSettings GetOrCreateSettings()
        {
            // Try to get from GlobalSettings first
            var settings = ReferenceManagerSettings.Instance;
            if (settings != null)
            {
                return settings;
            }

            // If not found, try to find the asset in the project
            var guids = AssetDatabase.FindAssets("t:ReferenceManagerSettings");
            if (guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<ReferenceManagerSettings>(path);
            }

            // If still not found, show a dialog to create one
            if (EditorUtility.DisplayDialog("Reference Manager Settings Not Found",
                "ReferenceManagerSettings asset not found. Would you like to create one?",
                "Create", "Cancel"))
            {
                var newSettings = ScriptableObject.CreateInstance<ReferenceManagerSettings>();
                var path = EditorUtility.SaveFilePanelInProject(
                    "Save Reference Manager Settings",
                    "Reference Manager Settings",
                    "asset",
                    "Please enter a file name to save the settings");

                if (!string.IsNullOrEmpty(path))
                {
                    AssetDatabase.CreateAsset(newSettings, path);
                    AssetDatabase.SaveAssets();
                    EditorUtility.FocusProjectWindow();
                    Selection.activeObject = newSettings;
                    return newSettings;
                }
            }

            Debug.LogWarning("[ReferenceManagerSettings] Settings asset not found. Please create one or add it to GlobalSettings.");
            return null;
        }

        #endregion

        public override void OnInspectorGUI()
        {
            var settings = (ReferenceManagerSettings)target;
            serializedObject.Update();

            // Left column: Reference Management. Right column: On Scene Save + Debug Settings
            var leftProps = new[] { "autoValidateOnScan", "showValidationResults", "comprehensiveSceneScanning", "prefabScanPaths" };
            var rightProps = new[] { "validateRefIdsOnSceneSave", "fixRefIdsOnSceneSave", "enableDebugLogging" };

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            foreach (string name in leftProps)
            {
                var prop = serializedObject.FindProperty(name);
                if (prop != null)
                    EditorGUILayout.PropertyField(prop, true);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            foreach (string name in rightProps)
            {
                var prop = serializedObject.FindProperty(name);
                if (prop != null)
                    EditorGUILayout.PropertyField(prop, true);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Reference Management", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan Project", EditorStyles.miniButton))
            {
                ScanProjectReferences(settings);
            }
            if (GUILayout.Button("Refresh Scenes", EditorStyles.miniButton))
            {
                ScanActiveScenes(settings);
            }
            if (GUILayout.Button("Clear All", EditorStyles.miniButton))
            {
                if (EditorUtility.DisplayDialog("Clear References",
                    "This will remove all scanned reference data. Are you sure?",
                    "Yes", "Cancel"))
                {
                    ClearAllReferences(settings);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "Scan Project clears and rebuilds asset + scene buckets: ScriptableObjects, then prefab assets whose paths match the Prefab Scan Paths list, then scene(s). " +
                "Refresh Scenes only updates buckets for loaded scenes and does not rescan prefab files—use Scan Project before merge or after changing prefab-only referenceables. " +
                "Add paths to 'Prefab Scan Paths' to enable prefab scanning; leave it empty to skip prefabs entirely.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Asset reference IDs (ScriptableObjects)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This asset is an editor-time validation database, not the runtime registry. " +
                "Asset (ScriptableObject) IDs below are data-identity only and are NOT resolvable at " +
                "runtime — only loaded scene MonoBehaviours resolve through ReferenceManager (SOs-out boundary).",
                MessageType.Info);
            var assetProp = serializedObject.FindProperty("assetKnownIds");
            if (assetProp != null)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.PropertyField(assetProp, new GUIContent("Types & IDs"), true);
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear asset bucket", GUILayout.Width(140)))
            {
                if (EditorUtility.DisplayDialog("Clear asset bucket",
                    "Remove all IDs collected from ScriptableObjects? Scene buckets are not affected.",
                    "Clear", "Cancel"))
                {
                    GetAssetKnownIdsField(settings).Clear();
                    EditorUtility.SetDirty(settings);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene reference collections", EditorStyles.boldLabel);
            var sceneProp = serializedObject.FindProperty("sceneKnownIds");
            if (sceneProp != null)
            {
                for (int i = 0; i < sceneProp.arraySize; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    var el = sceneProp.GetArrayElementAtIndex(i);
                    var pathProp = el.FindPropertyRelative("sceneAssetPath");
                    var displayName = FormatSceneCollectionDisplayName(pathProp != null ? pathProp.stringValue : null);
                    EditorGUILayout.PropertyField(el, new GUIContent(displayName), true);
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                    {
                        sceneProp.DeleteArrayElementAtIndex(i);
                        serializedObject.ApplyModifiedProperties();
                        EditorUtility.SetDirty(settings);
                        break;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                if (sceneProp.arraySize == 0)
                    EditorGUILayout.HelpBox("No scene buckets yet — run Scan Project or Refresh Scenes.", MessageType.None);
            }

            EditorGUILayout.Space();

            if (settings.ValidateRefIdsOnSceneSave)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("On Scene Save", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "RefId validation runs when you save a scene. It checks only the scene being saved (missing or duplicate RefIds within that scene).\n" +
                    (settings.FixRefIdsOnSceneSave
                        ? "✓ Auto-fix is ON: missing and duplicate RefIds will be fixed before save."
                        : "Validate only: issues are reported in the Console. Enable 'Fix Ref Ids On Scene Save' to auto-fix."),
                    settings.FixRefIdsOnSceneSave ? MessageType.Info : MessageType.None);
            }

            var comprehensiveScanning = GetComprehensiveSceneScanning(settings);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scanning Configuration", EditorStyles.boldLabel);

            if (comprehensiveScanning)
            {
                EditorGUILayout.HelpBox(
                    "🔍 Comprehensive Mode: Scanning ALL scene files in project.\n" +
                    "⚠️ This will load and unload each scene file, which may be slow for large projects.\n" +
                    "📦 Package scenes (read-only) will be automatically skipped.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "⚡ Fast Mode: Scanning only loaded scenes.\n" +
                    "ℹ️ Objects in unloaded scene files will be missed.\n" +
                    "Enable 'Comprehensive Scene Scanning' for complete coverage.\n" +
                    "📦 Note: Package scenes are always skipped (read-only).",
                    MessageType.Info);
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Scan the entire project for IReferenceable objects with progress display.
        /// Clears asset and all scene buckets, then repopulates.
        /// </summary>
        public void ScanProjectReferences(ReferenceManagerSettings settings)
        {
            if (!ConfirmProjectScanBeforeRun(settings))
                return;

            if (GetEnableDebugLogging(settings))
            {
                Debug.Log("[ReferenceManagerSettings] Starting project scan for IReferenceable objects...");
            }

            var stats = new ProjectScanStatistics();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            GetAssetKnownIdsField(settings).Clear();
            GetSceneKnownIdsField(settings).Clear();

            var globalSeen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var scannedObjects = new List<IReferenceable>();
            var validationIssues = new List<string>();
            var fixedDuplicates = new List<string>();
            var idRemappings = new List<(string refType, string oldId, string newId)>();

            try
            {
                var scriptableObjectGuids = AssetDatabase.FindAssets("t:ScriptableObject");
                int totalAssets = scriptableObjectGuids.Length;

                for (int i = 0; i < scriptableObjectGuids.Length; i++)
                {
                    string guid = scriptableObjectGuids[i];
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    ScriptableObject asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);

                    if (asset is IReferenceable referenceable)
                    {
                        stats.ScriptableObjectReferenceables++;
                        ProcessScannedReferenceable(settings, referenceable, globalSeen, scannedObjects, validationIssues, fixedDuplicates,
                            (t, id) => RegisterAssetReference(settings, t, id), idRemappings);
                    }

                    if (i % 50 == 0)
                    {
                        EditorUtility.DisplayProgressBar("Scanning Project",
                            $"Scanning ScriptableObjects... {i}/{totalAssets}",
                            (float)i / Math.Max(1, totalAssets) * 0.4f);
                    }
                }

                ScanPrefabAssets(settings, globalSeen, scannedObjects, validationIssues, fixedDuplicates, stats, idRemappings);

                if (ReadComprehensiveSceneScanning(settings))
                {
                    ScanAllSceneFiles(settings, globalSeen, scannedObjects, validationIssues, fixedDuplicates, stats, idRemappings);
                }
                else
                {
                    ScanLoadedScenes(settings, globalSeen, scannedObjects, validationIssues, fixedDuplicates, stats, idRemappings);
                }
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                sw.Stop();
                Debug.LogError($"[ReferenceManagerSettings] Error during project scan: {e.Message}\n{e.StackTrace}");
                EditorUtility.DisplayDialog(
                    "Scan Project Failed",
                    $"The project scan stopped with an error after {sw.Elapsed.TotalSeconds:F1} s.\n\n{e.Message}\n\nDetails are in the Console.",
                    "OK");
                return;
            }

            EditorUtility.ClearProgressBar();
            sw.Stop();

            EditorUtility.SetDirty(settings);

            if (GetEnableDebugLogging(settings))
            {
                Debug.Log($"[ReferenceManagerSettings] Scan complete: {scannedObjects.Count} objects found, {settings.GetReferenceTypes().Count} types registered");

                if (fixedDuplicates.Count > 0)
                {
                    Debug.Log($"[ReferenceManagerSettings] Fixed {fixedDuplicates.Count} duplicate IDs:");
                    foreach (var fix in fixedDuplicates)
                    {
                        Debug.Log($"  ✓ {fix}");
                    }
                }

                if (GetShowValidationResults(settings) && validationIssues.Count > 0)
                {
                    Debug.LogWarning($"[ReferenceManagerSettings] Found {validationIssues.Count} validation issues:");
                    foreach (var issue in validationIssues)
                    {
                        Debug.LogWarning($"  - {issue}");
                    }
                }
            }

            LogProjectScanFullReport(settings, stats, scannedObjects, fixedDuplicates, validationIssues, sw.Elapsed);
            ShowProjectScanCompleteDialog(settings, stats, scannedObjects, fixedDuplicates, validationIssues, sw.Elapsed);

            OfferAndApplySceneObjectReferenceRedirects(settings, idRemappings);
        }

        private static bool ConfirmProjectScanBeforeRun(ReferenceManagerSettings settings)
        {
            bool comprehensive = ReadComprehensiveSceneScanning(settings);
            string scenePhase = comprehensive
                ? "Every scene file in the project (slower; package / read-only scenes are skipped)."
                : "Only scenes that are currently open in the Editor.";

            string message =
                "This rebuilds all reference ID buckets in your Reference Manager Settings asset.\n\n" +
                "The scan will:\n" +
                "• Clear existing asset and scene ID lists, then repopulate them.\n" +
                "• Walk all ScriptableObjects, then prefab assets matching Prefab Scan Paths, then scenes.\n" +
                "• Scene phase: " + scenePhase + "\n" +
                "• Write new RefIds when they are missing or duplicated (ScriptableObjects, prefabs, and scene objects may be modified).\n\n" +
                "Large projects can take several minutes. Continue?";

            return EditorUtility.DisplayDialog("Scan Project for References", message, "Scan", "Cancel");
        }

        private static void ShowProjectScanCompleteDialog(
            ReferenceManagerSettings settings,
            ProjectScanStatistics stats,
            List<IReferenceable> scannedObjects,
            List<string> fixedDuplicates,
            List<string> validationIssues,
            TimeSpan elapsed)
        {
            var refTypes = settings.GetReferenceTypes();
            var refStats = settings.GetReferenceStats();
            int totalIdsRegistered = refStats.Values.Sum();
            int sceneBuckets = GetSceneKnownIdsFieldStatic(settings).Count;

            var dialog = new StringBuilder();
            dialog.AppendLine($"Completed in {elapsed.TotalSeconds:F1} s.");
            dialog.AppendLine();
            dialog.AppendLine($"Referenceables visited: {scannedObjects.Count}");
            dialog.AppendLine($"  • ScriptableObjects: {stats.ScriptableObjectReferenceables}");
            dialog.AppendLine($"  • Prefab assets: {stats.ReferenceablesInPrefabs} ({stats.PrefabAssetsScanned} prefab(s) loaded)");
            dialog.AppendLine($"  • Scenes: {stats.ReferenceablesInScenes} ({stats.ScenesProcessed} scene(s))");
            dialog.AppendLine();
            dialog.AppendLine($"Registered reference types: {refTypes.Count}");
            dialog.AppendLine($"Distinct RefIds in settings: {totalIdsRegistered}");
            dialog.AppendLine($"Scene buckets in settings: {sceneBuckets}");
            dialog.AppendLine();
            dialog.AppendLine($"RefIds auto-assigned (missing/duplicate): {fixedDuplicates.Count}");
            dialog.AppendLine($"Issues / warnings: {validationIssues.Count}");
            dialog.AppendLine();
            dialog.AppendLine("Full breakdown is in the Console.");

            const int maxLines = 8;
            if (fixedDuplicates.Count > 0)
            {
                dialog.AppendLine();
                dialog.AppendLine("Fixes (sample):");
                foreach (var line in fixedDuplicates.Take(maxLines))
                    dialog.AppendLine("  • " + TruncateForDialog(line, 120));
                if (fixedDuplicates.Count > maxLines)
                    dialog.AppendLine($"  … +{fixedDuplicates.Count - maxLines} more");
            }

            if (validationIssues.Count > 0)
            {
                dialog.AppendLine();
                dialog.AppendLine("Issues (sample):");
                foreach (var line in validationIssues.Take(maxLines))
                    dialog.AppendLine("  • " + TruncateForDialog(line, 120));
                if (validationIssues.Count > maxLines)
                    dialog.AppendLine($"  … +{validationIssues.Count - maxLines} more");
            }

            EditorUtility.DisplayDialog("Scan Project — Complete", dialog.ToString(), "OK");
        }

        private static string TruncateForDialog(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text;
            return text.Substring(0, maxChars - 1) + "…";
        }

        private static void LogProjectScanFullReport(
            ReferenceManagerSettings settings,
            ProjectScanStatistics stats,
            List<IReferenceable> scannedObjects,
            List<string> fixedDuplicates,
            List<string> validationIssues,
            TimeSpan elapsed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ReferenceManagerSettings] — Project scan report —");
            sb.AppendLine($"Duration: {elapsed}");
            sb.AppendLine($"Referenceables visited: {scannedObjects.Count} (SO: {stats.ScriptableObjectReferenceables}, prefabs: {stats.ReferenceablesInPrefabs}, scenes: {stats.ReferenceablesInScenes})");
            sb.AppendLine($"Prefab assets opened: {stats.PrefabAssetsScanned}; scenes processed: {stats.ScenesProcessed}");
            sb.AppendLine($"Registered types: {string.Join(", ", settings.GetReferenceTypes())}");

            var refStats = settings.GetReferenceStats();
            foreach (var kv in refStats.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.AppendLine($"  Type \"{kv.Key}\": {kv.Value} id(s)");

            if (fixedDuplicates.Count > 0)
            {
                sb.AppendLine("RefId changes:");
                foreach (var f in fixedDuplicates)
                    sb.AppendLine("  • " + f);
            }

            if (validationIssues.Count > 0)
            {
                sb.AppendLine("Issues:");
                foreach (var v in validationIssues)
                    sb.AppendLine("  • " + v);
            }

            Debug.Log(sb.ToString());
        }

        private static List<ReferenceManagerSettings.SceneKnownIdsCollection> GetSceneKnownIdsFieldStatic(ReferenceManagerSettings settings)
        {
            var field = settings.GetType().GetField("sceneKnownIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(settings) as List<ReferenceManagerSettings.SceneKnownIdsCollection>
                   ?? new List<ReferenceManagerSettings.SceneKnownIdsCollection>();
        }

        private static bool ReadComprehensiveSceneScanning(ReferenceManagerSettings settings)
        {
            if (settings == null)
                return false;
            var field = settings.GetType().GetField("comprehensiveSceneScanning", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(settings) as bool? ?? false;
        }

        /// <summary>
        /// Scan only the currently loaded scenes. Replaces buckets for those scenes only; asset bucket is unchanged.
        /// </summary>
        public void ScanActiveScenes(ReferenceManagerSettings settings)
        {
            if (GetEnableDebugLogging(settings))
            {
                Debug.Log("[ReferenceManagerSettings] Refreshing active scenes for IReferenceable objects...");
            }

            var loadedKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.IsValid())
                    loadedKeys.Add(GetScenePathKey(scene));
            }

            foreach (var key in loadedKeys)
                RemoveSceneBucket(settings, key);

            var globalSeen = BuildGlobalSeen(settings, loadedKeys);

            var scannedObjects = new List<IReferenceable>();
            var validationIssues = new List<string>();
            var fixedDuplicates = new List<string>();
            var idRemappings = new List<(string refType, string oldId, string newId)>();

            try
            {
                ScanLoadedScenes(settings, globalSeen, scannedObjects, validationIssues, fixedDuplicates, idRemappings: idRemappings);
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[ReferenceManagerSettings] Error during active scene refresh: {e.Message}");
                return;
            }

            EditorUtility.ClearProgressBar();
            EditorUtility.SetDirty(settings);

            OfferAndApplySceneObjectReferenceRedirects(settings, idRemappings);

            if (GetEnableDebugLogging(settings))
            {
                Debug.Log($"[ReferenceManagerSettings] Active scene refresh complete: {scannedObjects.Count} objects refreshed");

                if (fixedDuplicates.Count > 0)
                {
                    Debug.Log($"[ReferenceManagerSettings] Fixed {fixedDuplicates.Count} duplicate IDs during refresh:");
                    foreach (var fix in fixedDuplicates)
                    {
                        Debug.Log($"  ✓ {fix}");
                    }
                }

                if (GetShowValidationResults(settings) && validationIssues.Count > 0)
                {
                    Debug.LogWarning($"[ReferenceManagerSettings] Found {validationIssues.Count} validation issues during refresh:");
                    foreach (var issue in validationIssues)
                    {
                        Debug.LogWarning($"  - {issue}");
                    }
                }
            }
        }

        private void ProcessScannedReferenceable(
            ReferenceManagerSettings settings,
            IReferenceable referenceable,
            Dictionary<string, HashSet<string>> globalSeenIds,
            List<IReferenceable> scannedObjects,
            List<string> validationIssues,
            List<string> fixedDuplicates,
            Action<string, string> registerTypeAndId,
            List<(string refType, string oldId, string newId)> idRemappings = null)
        {
            scannedObjects.Add(referenceable);

            if (string.IsNullOrEmpty(referenceable.RefType))
            {
                validationIssues.Add($"{referenceable.DisplayName}: Missing RefType");
                return;
            }

            if (string.IsNullOrEmpty(referenceable.RefId))
            {
                var newId = GenerateUniqueIdNotIn(referenceable.RefType, globalSeenIds);
                UpdateObjectId(referenceable, newId, fixedDuplicates, "Missing ID - generated new ID");
                registerTypeAndId(referenceable.RefType, newId);
                return;
            }

            if (GlobalSeenContains(globalSeenIds, referenceable.RefType, referenceable.RefId))
            {
                var oldId = referenceable.RefId;
                var newId = GenerateUniqueIdNotIn(referenceable.RefType, globalSeenIds);
                UpdateObjectId(referenceable, newId, fixedDuplicates, $"Duplicate ID '{oldId}' - assigned new ID");
                registerTypeAndId(referenceable.RefType, newId);
                idRemappings?.Add((referenceable.RefType, oldId, newId));
            }
            else
            {
                registerTypeAndId(referenceable.RefType, referenceable.RefId);
                AddToGlobalSeen(globalSeenIds, referenceable.RefType, referenceable.RefId);
            }
        }

        private void UpdateObjectId(IReferenceable referenceable, string newId, List<string> fixedDuplicates, string reason)
        {
            try
            {
                var obj = referenceable as UnityEngine.Object;
                if (obj != null)
                {
                    referenceable.RefId = newId;
                    EditorUtility.SetDirty(obj);
                    fixedDuplicates.Add($"{referenceable.DisplayName}: {reason} '{newId}'");
                }
                else
                {
                    fixedDuplicates.Add($"{referenceable.DisplayName}: {reason} '{newId}' (Not a Unity Object - manual update required)");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReferenceManagerSettings] Failed to update ID for {referenceable.DisplayName}: {e.Message}");
                fixedDuplicates.Add($"{referenceable.DisplayName}: {reason} '{newId}' (Update failed - manual fix required)");
            }
        }

        private void RegisterAssetReference(ReferenceManagerSettings settings, string refType, string refId)
        {
            if (string.IsNullOrEmpty(refType) || string.IsNullOrEmpty(refId))
                return;

            var knownIds = GetAssetKnownIdsField(settings);
            var typeData = knownIds.Find(t => t.type == refType);
            if (typeData == null)
            {
                typeData = new ReferenceManagerSettings.ReferenceTypeData { type = refType };
                knownIds.Add(typeData);
            }

            if (!typeData.ids.Contains(refId))
                typeData.ids.Add(refId);
        }

        private void RegisterSceneReference(ReferenceManagerSettings settings, string sceneKey, string refType, string refId)
        {
            if (string.IsNullOrEmpty(sceneKey) || string.IsNullOrEmpty(refType) || string.IsNullOrEmpty(refId))
                return;

            var col = EnsureSceneCollection(settings, sceneKey);
            var typeData = col.types.Find(t => t.type == refType);
            if (typeData == null)
            {
                typeData = new ReferenceManagerSettings.ReferenceTypeData { type = refType };
                col.types.Add(typeData);
            }

            if (!typeData.ids.Contains(refId))
                typeData.ids.Add(refId);
        }

        private ReferenceManagerSettings.SceneKnownIdsCollection EnsureSceneCollection(ReferenceManagerSettings settings, string sceneKey)
        {
            var list = GetSceneKnownIdsField(settings);
            var found = list.Find(c => c != null && c.sceneAssetPath == sceneKey);
            if (found != null)
                return found;

            found = new ReferenceManagerSettings.SceneKnownIdsCollection
            {
                sceneAssetPath = sceneKey,
                types = new List<ReferenceManagerSettings.ReferenceTypeData>()
            };
            list.Add(found);
            return found;
        }

        private void RemoveSceneBucket(ReferenceManagerSettings settings, string sceneKey)
        {
            var list = GetSceneKnownIdsField(settings);
            list.RemoveAll(c => c != null && c.sceneAssetPath == sceneKey);
        }

        public void ClearAllReferences(ReferenceManagerSettings settings)
        {
            GetAssetKnownIdsField(settings).Clear();
            GetSceneKnownIdsField(settings).Clear();

            EditorUtility.SetDirty(settings);

            if (GetEnableDebugLogging(settings))
            {
                Debug.Log("[ReferenceManagerSettings] Cleared all reference data");
            }
        }

        private void ValidateAndFixRefIdsInScene(Scene scene, ReferenceManagerSettings settings)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            var referenceables = new List<IReferenceable>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb is IReferenceable refable)
                        referenceables.Add(refable);
                }
            }

            if (referenceables.Count == 0)
                return;

            var idsInScene = new Dictionary<string, HashSet<string>>();
            var validationIssues = new List<string>();
            var fixedEntries = new List<string>();
            var doFix = settings.FixRefIdsOnSceneSave;
            var sceneKey = GetScenePathKey(scene);

            foreach (var referenceable in referenceables)
            {
                if (string.IsNullOrEmpty(referenceable.RefType))
                {
                    validationIssues.Add($"{referenceable.DisplayName}: Missing RefType");
                    continue;
                }

                if (!idsInScene.TryGetValue(referenceable.RefType, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    idsInScene[referenceable.RefType] = set;
                }

                if (string.IsNullOrEmpty(referenceable.RefId))
                {
                    if (doFix)
                    {
                        var newId = ReferenceGenerator.GenerateUniqueId(referenceable.RefType);
                        UpdateObjectId(referenceable, newId, fixedEntries, "Missing ID - generated new ID");
                        RegisterSceneReference(settings, sceneKey, referenceable.RefType, newId);
                        set.Add(newId);
                    }
                    else
                    {
                        validationIssues.Add($"{referenceable.DisplayName}: Missing RefId");
                    }
                    continue;
                }

                if (set.Contains(referenceable.RefId))
                {
                    if (doFix)
                    {
                        var newId = ReferenceGenerator.GenerateUniqueId(referenceable.RefType);
                        UpdateObjectId(referenceable, newId, fixedEntries, $"Duplicate ID '{referenceable.RefId}' within scene - assigned new ID");
                        RegisterSceneReference(settings, sceneKey, referenceable.RefType, newId);
                        set.Add(newId);
                    }
                    else
                    {
                        validationIssues.Add($"{referenceable.DisplayName}: Duplicate RefId '{referenceable.RefId}' within scene");
                    }
                    continue;
                }

                set.Add(referenceable.RefId);
            }

            if (fixedEntries.Count > 0)
                EditorUtility.SetDirty(settings);

            var sceneName = System.IO.Path.GetFileNameWithoutExtension(scene.path);
            if (fixedEntries.Count > 0 && GetEnableDebugLogging(settings))
            {
                Debug.Log($"[ReferenceManagerSettings] Scene '{sceneName}' (pre-save): Fixed {fixedEntries.Count} RefId(s):\n  " + string.Join("\n  ", fixedEntries));
            }
            if (validationIssues.Count > 0 && !doFix && GetShowValidationResults(settings))
            {
                Debug.LogWarning($"[ReferenceManagerSettings] Scene '{sceneName}' (pre-save): {validationIssues.Count} validation issue(s):\n  " + string.Join("\n  ", validationIssues));
            }
        }

        private List<ReferenceManagerSettings.ReferenceTypeData> GetAssetKnownIdsField(ReferenceManagerSettings settings)
        {
            var field = settings.GetType().GetField("assetKnownIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(settings) as List<ReferenceManagerSettings.ReferenceTypeData>
                   ?? new List<ReferenceManagerSettings.ReferenceTypeData>();
        }

        private List<ReferenceManagerSettings.SceneKnownIdsCollection> GetSceneKnownIdsField(ReferenceManagerSettings settings)
        {
            var field = settings.GetType().GetField("sceneKnownIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(settings) as List<ReferenceManagerSettings.SceneKnownIdsCollection>
                   ?? new List<ReferenceManagerSettings.SceneKnownIdsCollection>();
        }

        private bool GetEnableDebugLogging(ReferenceManagerSettings settings)
        {
            var field = settings.GetType().GetField("enableDebugLogging", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(settings) as bool? ?? true;
        }

        private bool GetShowValidationResults(ReferenceManagerSettings settings)
        {
            var field = settings.GetType().GetField("showValidationResults", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(settings) as bool? ?? true;
        }

        /// <summary>
        /// Scans prefab assets that match <see cref="ReferenceManagerSettings.PrefabScanPaths"/> for
        /// <see cref="IReferenceable"/> components, registers their IDs in the asset bucket, and fixes
        /// missing/duplicate IDs on the prefab asset itself. Prefabs are skipped when the opt-in list is empty.
        /// </summary>
        private void ScanPrefabAssets(ReferenceManagerSettings settings, Dictionary<string, HashSet<string>> globalSeenIds,
            List<IReferenceable> scannedObjects, List<string> validationIssues, List<string> fixedDuplicates,
            ProjectScanStatistics stats,
            List<(string refType, string oldId, string newId)> idRemappings = null)
        {
            var scanPaths = GetPrefabScanPaths(settings);
            if (scanPaths.Count == 0)
            {
                if (GetEnableDebugLogging(settings))
                    Debug.Log("[ReferenceManagerSettings] Prefab scanning skipped — no paths configured in Prefab Scan Paths.");
                return;
            }

            var guids = AssetDatabase.FindAssets("t:Prefab");
            int total = guids.Length;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (IsInPackageFolder(path) || !IsPrefabInScanList(path, scanPaths))
                    continue;

                EditorUtility.DisplayProgressBar("Scanning Project",
                    $"Scanning prefabs... {i + 1}/{total}",
                    0.4f + (float)(i + 1) / Math.Max(1, total) * 0.15f);

                GameObject root = null;
                try
                {
                    root = PrefabUtility.LoadPrefabContents(path);
                    stats.PrefabAssetsScanned++;
                }
                catch (Exception e)
                {
                    validationIssues.Add($"Prefab load failed '{path}': {e.Message}");
                    continue;
                }

                try
                {
                    bool needSave = false;
                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb is not IReferenceable referenceable)
                            continue;

                        stats.ReferenceablesInPrefabs++;
                        ProcessScannedReferenceable(settings, referenceable, globalSeenIds, scannedObjects, validationIssues, fixedDuplicates,
                            (t, id) => RegisterAssetReference(settings, t, id), idRemappings);

                        if (EditorUtility.IsDirty(mb))
                            needSave = true;
                    }

                    if (needSave)
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                catch (Exception e)
                {
                    validationIssues.Add($"Prefab scan failed '{path}': {e.Message}");
                }
                finally
                {
                    if (root != null)
                        PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        private void ScanLoadedScenes(ReferenceManagerSettings settings, Dictionary<string, HashSet<string>> globalSeenIds,
            List<IReferenceable> scannedObjects, List<string> validationIssues, List<string> fixedDuplicates,
            ProjectScanStatistics stats = null,
            List<(string refType, string oldId, string newId)> idRemappings = null)
        {
            int totalRefs = 0;
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var sc = SceneManager.GetSceneAt(s);
                if (!sc.isLoaded || !sc.IsValid())
                    continue;
                var key = GetScenePathKey(sc);
                foreach (var root in sc.GetRootGameObjects())
                {
                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb is IReferenceable r)
                            totalRefs++;
                    }
                }
            }

            int processed = 0;
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var sc = SceneManager.GetSceneAt(s);
                if (!sc.isLoaded || !sc.IsValid())
                    continue;

                if (stats != null)
                    stats.ScenesProcessed++;

                var sceneKey = GetScenePathKey(sc);
                foreach (var root in sc.GetRootGameObjects())
                {
                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb is not IReferenceable referenceable)
                            continue;

                        if (stats != null)
                            stats.ReferenceablesInScenes++;
                        ProcessScannedReferenceable(settings, referenceable, globalSeenIds, scannedObjects, validationIssues, fixedDuplicates,
                            (t, id) => RegisterSceneReference(settings, sceneKey, t, id), idRemappings);

                        processed++;
                        var denom = Math.Max(1, totalRefs);
                        if (processed % 20 == 0)
                        {
                            EditorUtility.DisplayProgressBar("Scanning Project",
                                $"Scanning Loaded Scene Objects... {processed}/{Math.Max(processed, totalRefs)}",
                                0.55f + (float)processed / denom * 0.45f);
                        }
                    }
                }
            }
        }

        private void ScanAllSceneFiles(ReferenceManagerSettings settings, Dictionary<string, HashSet<string>> globalSeenIds,
            List<IReferenceable> scannedObjects, List<string> validationIssues, List<string> fixedDuplicates,
            ProjectScanStatistics stats = null,
            List<(string refType, string oldId, string newId)> idRemappings = null)
        {
            var allSceneGuids = AssetDatabase.FindAssets("t:Scene");

            var sceneGuids = new List<string>();
            foreach (var guid in allSceneGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsInPackageFolder(path))
                {
                    sceneGuids.Add(guid);
                }
            }

            int processedScenes = 0;
            int totalScenes = sceneGuids.Count;
            int skippedScenes = allSceneGuids.Length - totalScenes;

            if (GetEnableDebugLogging(settings))
            {
                Debug.Log($"[ReferenceManagerSettings] Found {totalScenes} scene files to scan comprehensively ({skippedScenes} package scenes skipped)");
            }

            bool scanIsolated = TryBuildOpenScenesSnapshot(out var snapshotPaths, out var snapshotActivePath);
            if (scanIsolated)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    validationIssues.Add("Comprehensive scene scan was skipped because saving open scenes was cancelled.");
                    return;
                }
            }

            if (!scanIsolated && GetEnableDebugLogging(settings))
            {
                Debug.Log("[ReferenceManagerSettings] Comprehensive scan: an unsaved scene is open — using additive loads; " +
                          "light probe console warnings may still appear. Save all scenes to use isolated single-scene loading.");
            }

            // Avoid starting a lighting bake for every scene Unity opens (Editor setting).
            var previousBakeOnSceneLoad = Lightmapping.bakeOnSceneLoad;
            var previousLogHandler = Debug.unityLogger.logHandler;
            var logFilterActive = false;
            try
            {
                Lightmapping.bakeOnSceneLoad = Lightmapping.BakeOnSceneLoadMode.Never;

                if (!scanIsolated)
                {
                    Debug.unityLogger.logHandler = new SuppressProbeAppendWarningLogHandler(previousLogHandler);
                    logFilterActive = true;
                }

                try
                {
                    foreach (var sceneGuid in sceneGuids)
                    {
                        var scenePath = AssetDatabase.GUIDToAssetPath(sceneGuid);
                        var sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);

                        EditorUtility.DisplayProgressBar("Scanning Project",
                            $"Loading scene: {sceneName}",
                            totalScenes > 0 ? 0.55f + (float)processedScenes / totalScenes * 0.45f : 1f);

                        try
                        {
                            if (!System.IO.File.Exists(scenePath))
                            {
                                throw new System.IO.FileNotFoundException($"Scene file not found: {scenePath}");
                            }

                            var openMode = scanIsolated ? OpenSceneMode.Single : OpenSceneMode.Additive;
                            var scene = EditorSceneManager.OpenScene(scenePath, openMode);
                            if (stats != null)
                                stats.ScenesProcessed++;

                            var sceneKey = GetScenePathKey(scene);

                            var rootObjects = scene.GetRootGameObjects();
                            int sceneObjectCount = 0;

                            foreach (var rootObject in rootObjects)
                            {
                                sceneObjectCount += rootObject.GetComponentsInChildren<MonoBehaviour>()
                                    .Count(obj => obj is IReferenceable);
                            }

                            int processedObjects = 0;

                            foreach (var rootObject in rootObjects)
                            {
                                var referenceables = rootObject.GetComponentsInChildren<MonoBehaviour>()
                                    .Where(obj => obj is IReferenceable)
                                    .Cast<IReferenceable>();

                                foreach (var referenceable in referenceables)
                                {
                                    if (stats != null)
                                        stats.ReferenceablesInScenes++;
                                    ProcessScannedReferenceable(settings, referenceable, globalSeenIds, scannedObjects, validationIssues, fixedDuplicates,
                                        (t, id) => RegisterSceneReference(settings, sceneKey, t, id), idRemappings);

                                    processedObjects++;
                                    if (processedObjects % 10 == 0)
                                    {
                                        EditorUtility.DisplayProgressBar("Scanning Project",
                                            $"Scene '{sceneName}': {processedObjects}/{sceneObjectCount} objects",
                                            totalScenes > 0
                                                ? 0.55f + (float)(processedScenes + (float)processedObjects / Math.Max(1, sceneObjectCount)) / totalScenes * 0.45f
                                                : 1f);
                                    }
                                }
                            }

                            if (scene.isDirty)
                            {
                                if (!EditorSceneManager.SaveScene(scene))
                                {
                                    validationIssues.Add($"Failed to save scene after RefId updates: {sceneName}");
                                    Debug.LogWarning($"[ReferenceManagerSettings] Could not save scene '{sceneName}' after RefId updates.");
                                }
                            }

                            if (!scanIsolated)
                                EditorSceneManager.CloseScene(scene, true);
                        }
                        catch (Exception e)
                        {
                            string errorDetails = $"[ReferenceManagerSettings] Failed to scan scene '{scenePath}': {e.Message}";
                            if (e.Message.Contains("managedReferences"))
                            {
                                errorDetails += "\nThis appears to be a corrupted managed reference in the scene file. " +
                                               "Consider removing and re-adding the problematic component, or restoring from backup.";
                            }
                            else if (e.Message.Contains("serialization") || e.Message.Contains("deserialization"))
                            {
                                errorDetails += "\nThis appears to be a scene serialization error. " +
                                               "The scene file may be corrupted - try restoring from version control.";
                            }

                            Debug.LogWarning(errorDetails);
                            validationIssues.Add($"Failed to scan scene '{sceneName}': {e.Message}");
                        }

                        processedScenes++;
                    }
                }
                finally
                {
                    if (scanIsolated)
                        RestoreEditorOpenScenes(snapshotPaths, snapshotActivePath);
                }

                if (GetEnableDebugLogging(settings))
                {
                    Debug.Log($"[ReferenceManagerSettings] Comprehensive scene scanning complete: {totalScenes} scenes processed, {skippedScenes} package scenes skipped");
                }
            }
            finally
            {
                if (logFilterActive)
                    Debug.unityLogger.logHandler = previousLogHandler;
                Lightmapping.bakeOnSceneLoad = previousBakeOnSceneLoad;
            }
        }

        /// <summary>
        /// True when every loaded editor scene has a saved asset path (snapshot can be restored after single-scene scans).
        /// </summary>
        private static bool TryBuildOpenScenesSnapshot(out List<string> pathsInOrder, out string activeScenePath)
        {
            pathsInOrder = new List<string>();
            activeScenePath = null;

            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var sc = EditorSceneManager.GetSceneAt(i);
                if (!sc.isLoaded || !sc.IsValid())
                    continue;
                if (string.IsNullOrEmpty(sc.path))
                    return false;
                pathsInOrder.Add(sc.path);
            }

            if (pathsInOrder.Count == 0)
                return false;

            var active = EditorSceneManager.GetActiveScene();
            if (active.IsValid() && !string.IsNullOrEmpty(active.path))
                activeScenePath = active.path;

            return true;
        }

        private static void RestoreEditorOpenScenes(List<string> pathsInOrder, string activeScenePath)
        {
            if (pathsInOrder == null || pathsInOrder.Count == 0)
            {
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                return;
            }

            try
            {
                EditorSceneManager.OpenScene(pathsInOrder[0], OpenSceneMode.Single);
                for (int i = 1; i < pathsInOrder.Count; i++)
                {
                    if (!string.IsNullOrEmpty(pathsInOrder[i]))
                        EditorSceneManager.OpenScene(pathsInOrder[i], OpenSceneMode.Additive);
                }

                if (!string.IsNullOrEmpty(activeScenePath))
                {
                    var active = EditorSceneManager.GetSceneByPath(activeScenePath);
                    if (active.isLoaded)
                        EditorSceneManager.SetActiveScene(active);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ReferenceManagerSettings] Could not fully restore scenes after comprehensive scan: {e.Message}");
            }
        }

        private static string GetScenePathKey(Scene scene)
        {
            if (!scene.IsValid())
                return "__InvalidScene__";

            if (!string.IsNullOrEmpty(scene.path))
                return scene.path;

            return "__UntitledOrRuntime__/" + scene.name;
        }

        private static string FormatSceneCollectionDisplayName(string sceneAssetPath)
        {
            if (string.IsNullOrEmpty(sceneAssetPath))
                return "Scene (no path)";

            if (sceneAssetPath == "__InvalidScene__")
                return "Invalid scene";

            const string untitledPrefix = "__UntitledOrRuntime__/";
            if (sceneAssetPath.StartsWith(untitledPrefix, StringComparison.Ordinal))
                return sceneAssetPath.Substring(untitledPrefix.Length);

            var fileName = System.IO.Path.GetFileNameWithoutExtension(sceneAssetPath);
            return string.IsNullOrEmpty(fileName) ? sceneAssetPath : fileName;
        }

        private Dictionary<string, HashSet<string>> BuildGlobalSeen(ReferenceManagerSettings settings, HashSet<string> excludeSceneKeys)
        {
            var dict = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            void AddList(List<ReferenceManagerSettings.ReferenceTypeData> source)
            {
                if (source == null)
                    return;
                foreach (var td in source)
                {
                    if (td == null || string.IsNullOrEmpty(td.type) || td.ids == null)
                        continue;
                    if (!dict.TryGetValue(td.type, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        dict[td.type] = set;
                    }
                    foreach (var id in td.ids)
                    {
                        if (!string.IsNullOrEmpty(id))
                            set.Add(id);
                    }
                }
            }

            AddList(GetAssetKnownIdsField(settings));
            foreach (var col in GetSceneKnownIdsField(settings))
            {
                if (col == null || excludeSceneKeys.Contains(col.sceneAssetPath))
                    continue;
                AddList(col.types);
            }

            return dict;
        }

        private static string GenerateUniqueIdNotIn(string refType, Dictionary<string, HashSet<string>> globalSeen)
        {
            for (int a = 0; a < 64; a++)
            {
                var id = ReferenceGenerator.GenerateUniqueId(refType);
                if (!globalSeen.TryGetValue(refType, out var set) || !set.Contains(id))
                {
                    AddToGlobalSeen(globalSeen, refType, id);
                    return id;
                }
            }

            var fallback = ReferenceGenerator.GenerateUniqueId(refType);
            AddToGlobalSeen(globalSeen, refType, fallback);
            return fallback;
        }

        private static void AddToGlobalSeen(Dictionary<string, HashSet<string>> globalSeen, string refType, string id)
        {
            if (!globalSeen.TryGetValue(refType, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                globalSeen[refType] = set;
            }
            set.Add(id);
        }

        private static bool GlobalSeenContains(Dictionary<string, HashSet<string>> globalSeen, string refType, string id)
        {
            return globalSeen.TryGetValue(refType, out var set) && set.Contains(id);
        }

        /// <summary>
        /// Returns the prefab scan paths configured in settings via reflection.
        /// </summary>
        private static List<string> GetPrefabScanPaths(ReferenceManagerSettings settings)
        {
            var field = settings.GetType().GetField("prefabScanPaths", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(settings) as List<string> ?? new List<string>();
        }

        /// <summary>
        /// Returns true when <paramref name="assetPath"/> starts with at least one entry in <paramref name="scanPaths"/>,
        /// supporting both exact prefab paths and folder prefixes.
        /// </summary>
        private static bool IsPrefabInScanList(string assetPath, List<string> scanPaths)
        {
            assetPath = assetPath.Replace('\\', '/');
            foreach (var entry in scanPaths)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;
                var norm = entry.Replace('\\', '/').TrimEnd('/');
                // Folder match: path starts with "folder/" or exact match
                if (assetPath.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(assetPath, norm, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// After a scan that reassigned RefIds, offers the user the option to redirect
        /// <see cref="SceneObjectReference"/> fields (anywhere a serialized <c>refId</c> string matches
        /// an old ID) to the new IDs in loaded scenes and in prefab assets matching the scan list.
        /// </summary>
        private void OfferAndApplySceneObjectReferenceRedirects(
            ReferenceManagerSettings settings,
            List<(string refType, string oldId, string newId)> remappings)
        {
            if (remappings == null || remappings.Count == 0)
                return;

            var oldToNew = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (_, oldId, newId) in remappings)
            {
                if (!oldToNew.ContainsKey(oldId))
                    oldToNew[oldId] = newId;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{remappings.Count} RefId(s) were reassigned as duplicates during this scan:");
            sb.AppendLine();
            foreach (var r in remappings.Take(8))
                sb.AppendLine($"  {r.refType} | {r.oldId}  →  {r.newId}");
            if (remappings.Count > 8)
                sb.AppendLine($"  … +{remappings.Count - 8} more");
            sb.AppendLine();
            sb.AppendLine("Do you want to update SceneObjectReference fields in loaded scenes and scanned prefabs that point to these old IDs?");
            sb.AppendLine();
            sb.AppendLine("Redirect  — fields are updated to the new IDs (they will resolve the reassigned objects).");
            sb.AppendLine("Keep      — fields keep the old IDs (they resolve the original, non-duplicate objects).");

            bool redirect = EditorUtility.DisplayDialog(
                "Update SceneObjectReferences?",
                sb.ToString(),
                "Redirect to New IDs",
                "Keep Old IDs");

            if (!redirect)
                return;

            var log = new List<string>();
            int redirectedObjects = 0;

            // Loaded scenes
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var sc = SceneManager.GetSceneAt(s);
                if (!sc.isLoaded || !sc.IsValid())
                    continue;

                foreach (var root in sc.GetRootGameObjects())
                {
                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (RedirectSceneObjectRefsInObject(mb, oldToNew, log))
                            redirectedObjects++;
                    }
                }
            }

            // Prefabs in scan list
            var scanPaths = GetPrefabScanPaths(settings);
            if (scanPaths.Count > 0)
            {
                var guids = AssetDatabase.FindAssets("t:Prefab");
                foreach (var guid in guids)
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (IsInPackageFolder(assetPath) || !IsPrefabInScanList(assetPath, scanPaths))
                        continue;

                    GameObject root = null;
                    bool needSave = false;
                    try
                    {
                        root = PrefabUtility.LoadPrefabContents(assetPath);
                        foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (RedirectSceneObjectRefsInObject(mb, oldToNew, log))
                            {
                                redirectedObjects++;
                                needSave = true;
                            }
                        }
                        if (needSave)
                            PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ReferenceManagerSettings] Could not redirect refs in prefab '{assetPath}': {e.Message}");
                    }
                    finally
                    {
                        if (root != null)
                            PrefabUtility.UnloadPrefabContents(root);
                    }
                }
            }

            if (redirectedObjects > 0)
                Debug.Log($"[ReferenceManagerSettings] Redirected SceneObjectReference fields on {redirectedObjects} object(s):\n" + string.Join("\n", log));
            else
                Debug.Log("[ReferenceManagerSettings] No SceneObjectReference fields found matching the reassigned IDs.");
        }

        private static bool RedirectSceneObjectRefsInObject(
            MonoBehaviour mb,
            Dictionary<string, string> oldToNew,
            List<string> log)
        {
            return RefIdEditorUtility.RedirectSceneObjectRefsInObject(mb, oldToNew, log);
        }

        private bool IsInPackageFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            assetPath = assetPath.Replace('\\', '/');

            if (assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return true;

            var directory = System.IO.Path.GetDirectoryName(assetPath);
            while (!string.IsNullOrEmpty(directory))
            {
                var packageJsonPath = System.IO.Path.Combine(directory, "package.json");
                if (System.IO.File.Exists(packageJsonPath))
                {
                    return true;
                }

                var parentDirectory = System.IO.Path.GetDirectoryName(directory);
                if (parentDirectory == directory)
                    break;
                directory = parentDirectory;
            }

            return false;
        }

        private bool GetComprehensiveSceneScanning(ReferenceManagerSettings settings) => ReadComprehensiveSceneScanning(settings);
    }
}
