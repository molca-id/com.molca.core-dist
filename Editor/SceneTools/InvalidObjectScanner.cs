using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor
{
    /// <summary>
    /// Editor window that scans all loaded scenes for GameObjects with invalid state —
    /// currently: missing (null) script components. Results are listed with hierarchy paths
    /// and can be selected or pinged directly from the window.
    /// </summary>
    public class InvalidObjectScanner : EditorWindow
    {
        private struct ScanResult
        {
            public GameObject GameObject;
            public string HierarchyPath;
            public string SceneName;
            public int MissingScriptCount;
        }

        private List<ScanResult> _results = new();
        private Vector2 _scroll;
        private bool _hasScanned;
        private bool _includeInactive = true;

        [MenuItem("Molca/Scene Tools/Invalid Object Scanner", priority = 40)]
        public static void Open()
        {
            var window = GetWindow<InvalidObjectScanner>("Invalid Object Scanner");
            window.titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Invalid Object Scanner", "utils");
            window.minSize = new Vector2(420, 300);
        }

        // Set in OnEnable (not just Open) so the icon survives domain reloads and layout restores.
        private void OnEnable() =>
            titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Invalid Object Scanner", "utils");

        private void OnGUI()
        {
            DrawToolbar();
            DrawResults();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _includeInactive = GUILayout.Toggle(_includeInactive, "Include Inactive", EditorStyles.toolbarButton, GUILayout.Width(120));
                GUILayout.FlexibleSpace();

                if (_results.Count > 0)
                {
                    if (GUILayout.Button($"Select All ({_results.Count})", EditorStyles.toolbarButton, GUILayout.Width(120)))
                        SelectAll();
                }

                if (GUILayout.Button("Scan", EditorStyles.toolbarButton, GUILayout.Width(60)))
                    RunScan();
            }

            if (_hasScanned)
            {
                var style = _results.Count > 0 ? EditorStyles.helpBox : EditorStyles.helpBox;
                var icon = _results.Count > 0 ? "console.warnicon.sml" : "console.infoicon.sml";
                var msg = _results.Count > 0
                    ? $"{_results.Count} GameObject(s) with missing scripts found."
                    : "No issues found.";

                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(4);
                    EditorGUILayout.LabelField(new GUIContent(msg, EditorGUIUtility.IconContent(icon).image), style);
                }
            }

            EditorGUILayout.Space(4);
        }

        private void DrawResults()
        {
            if (!_hasScanned)
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField("Press Scan to begin.", EditorStyles.centeredGreyMiniLabel);
                    GUILayout.FlexibleSpace();
                }
                GUILayout.FlexibleSpace();
                return;
            }

            if (_results.Count == 0)
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField("All clear!", EditorStyles.centeredGreyMiniLabel);
                    GUILayout.FlexibleSpace();
                }
                GUILayout.FlexibleSpace();
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            string lastScene = null;
            foreach (var result in _results)
            {
                if (result.SceneName != lastScene)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField($"Scene: {result.SceneName}", EditorStyles.boldLabel);
                    lastScene = result.SceneName;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(12);
                    var label = $"{result.HierarchyPath}  [{result.MissingScriptCount} missing]";

                    bool isSelected = Selection.activeGameObject == result.GameObject;
                    var style = isSelected ? EditorStyles.whiteLabel : EditorStyles.label;

                    if (GUILayout.Button(label, style))
                    {
                        Selection.activeGameObject = result.GameObject;
                        EditorGUIUtility.PingObject(result.GameObject);
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void RunScan()
        {
            _results.Clear();

            // Prefab Mode: the prefab stage has its own scene not visible to SceneManager.
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                string label = $"Prefab: {Path.GetFileName(prefabStage.assetPath)}";
                foreach (GameObject root in prefabStage.scene.GetRootGameObjects())
                    ScanTransform(root.transform, label);
            }
            else
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded)
                        continue;

                    foreach (GameObject root in scene.GetRootGameObjects())
                        ScanTransform(root.transform, scene.name);
                }
            }

            // Sort: scene name first, then hierarchy path.
            _results.Sort((a, b) =>
            {
                int sceneCmp = string.Compare(a.SceneName, b.SceneName, System.StringComparison.Ordinal);
                return sceneCmp != 0 ? sceneCmp : string.Compare(a.HierarchyPath, b.HierarchyPath, System.StringComparison.Ordinal);
            });

            _hasScanned = true;
            Repaint();
        }

        private void ScanTransform(Transform t, string sceneName)
        {
            if (t == null)
                return;

            GameObject go = t.gameObject;
            if (!_includeInactive && !go.activeInHierarchy)
                return;

            int missingCount = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
            if (missingCount > 0)
            {
                _results.Add(new ScanResult
                {
                    GameObject = go,
                    HierarchyPath = BuildPath(t),
                    SceneName = sceneName,
                    MissingScriptCount = missingCount,
                });
            }

            for (int i = 0; i < t.childCount; i++)
                ScanTransform(t.GetChild(i), sceneName);
        }

        private void SelectAll()
        {
            var objects = new Object[_results.Count];
            for (int i = 0; i < _results.Count; i++)
                objects[i] = _results[i].GameObject;
            Selection.objects = objects;
            if (objects.Length > 0)
                EditorGUIUtility.PingObject(objects[0]);
        }

        private static string BuildPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
