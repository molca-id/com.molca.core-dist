using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using Molca.Editor;
using System.Linq;
using System.Collections.Generic;

// Edit-mode inspector must work without a RuntimeManager; ColorModule.ResolveActive()
// is internal to the Molca runtime assembly, so the legacy static shims are used here.
#pragma warning disable CS0618

namespace Molca.ColorID.Editor
{
    [CustomEditor(typeof(ColorModule))]
    public class ColorModuleEditor : UnityEditor.Editor
    {
        private const string CopyPasteEditorPrefsKey = "Molca_ColorModule_CopiedSwatch";

        private ColorModule colorModule;
        private SerializedProperty colorSwatchesProperty;
        private SerializedProperty schemeNameProperty;
        private Dictionary<int, ReorderableList> swatchReorderableLists = new Dictionary<int, ReorderableList>();
        private Dictionary<string, bool> swatchFoldouts = new Dictionary<string, bool>();
        private string newSwatchName = "";

        private void OnEnable()
        {
            colorModule = (ColorModule)target;
            colorSwatchesProperty = serializedObject.FindProperty("_colorSwatches");
            schemeNameProperty = serializedObject.FindProperty("_schemeName");
            swatchReorderableLists.Clear();
            LoadFoldoutStates();
        }
        
        private void LoadFoldoutStates()
        {
            swatchFoldouts.Clear();
            
            // Load foldout states from EditorPrefs
            if (colorSwatchesProperty != null)
            {
                for (int i = 0; i < colorSwatchesProperty.arraySize; i++)
                {
                    var swatchElement = colorSwatchesProperty.GetArrayElementAtIndex(i);
                    var swatchNameProperty = swatchElement.FindPropertyRelative("swatchName");
                    string swatchName = swatchNameProperty.stringValue;
                    
                    string key = $"ColorModule_Foldout_{swatchName}";
                    swatchFoldouts[swatchName] = MolcaEditorPrefs.GetBool(key, true); // Default to expanded
                }
            }
        }
        
        private void SaveFoldoutState(string swatchName, bool isExpanded)
        {
            string key = $"ColorModule_Foldout_{swatchName}";
            MolcaEditorPrefs.SetBool(key, isExpanded);
            swatchFoldouts[swatchName] = isExpanded;
        }

        private void CopySwatch(int swatchIndex)
        {
            var swatch = colorModule.ColorSwatches[swatchIndex];
            string json = JsonUtility.ToJson(swatch);
            MolcaEditorPrefs.SetString(CopyPasteEditorPrefsKey, json);
            Debug.Log($"Copied swatch '{swatch.SwatchName}' ({swatch.ColorDefinitions.Count} colors).");
        }

        private bool HasCopiedSwatch()
        {
            return MolcaEditorPrefs.HasKey(CopyPasteEditorPrefsKey) &&
                   !string.IsNullOrEmpty(MolcaEditorPrefs.GetString(CopyPasteEditorPrefsKey, ""));
        }

        private void PasteSwatch()
        {
            if (!HasCopiedSwatch())
            {
                Debug.LogWarning("Nothing to paste. Copy a swatch first.");
                return;
            }

            string json = MolcaEditorPrefs.GetString(CopyPasteEditorPrefsKey, "");
            var copied = JsonUtility.FromJson<ColorModule.ColorSwatch>(json);
            if (copied == null || copied.ColorDefinitions == null)
            {
                Debug.LogWarning("Paste failed: invalid copied data.");
                return;
            }

            string baseName = copied.SwatchName;
            string newName = baseName;
            int copyIndex = 1;
            while (colorModule.ColorSwatches.Exists(s => s.SwatchName == newName))
            {
                newName = $"{baseName} ({copyIndex})";
                copyIndex++;
            }

            if (colorModule.AddSwatch(newName))
            {
                var swatch = colorModule.GetSwatch(newName);
                if (swatch != null)
                {
                    foreach (var def in copied.ColorDefinitions)
                    {
                        swatch.AddColor(def.ColorId, def.Color, def.Description ?? "");
                    }
                }
                EditorUtility.SetDirty(target);
                swatchReorderableLists.Clear();
                serializedObject.Update();
                LoadFoldoutStates();
                SaveFoldoutState(newName, true);
                Debug.Log($"Pasted swatch as '{newName}' ({copied.ColorDefinitions.Count} colors).");
            }
        }

        private ReorderableList GetOrCreateReorderableList(int swatchIndex)
        {
            if (swatchReorderableLists.ContainsKey(swatchIndex))
            {
                return swatchReorderableLists[swatchIndex];
            }

            var swatchElement = colorSwatchesProperty.GetArrayElementAtIndex(swatchIndex);
            var colorDefinitionsProperty = swatchElement.FindPropertyRelative("colorDefinitions");
            var swatchNameProperty = swatchElement.FindPropertyRelative("swatchName");
            var isDefaultProperty = swatchElement.FindPropertyRelative("isDefault");

            var reorderableList = new ReorderableList(serializedObject, colorDefinitionsProperty,
                true, false, true, true)
            {
                drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
                {
                    var element = colorDefinitionsProperty.GetArrayElementAtIndex(index);
                    rect.y += 2;
                    float singleLineHeight = EditorGUIUtility.singleLineHeight;

                    float buttonWidth = 30;
                    float space = 5;
                    float availableWidth = rect.width - buttonWidth - space;
                    
                    float colorWidth = 60;
                    float idWidth = (availableWidth - colorWidth - space) * 0.4f;
                    float descriptionWidth = (availableWidth - colorWidth - space) * 0.6f;

                    EditorGUI.PropertyField(
                        new Rect(rect.x, rect.y, colorWidth, singleLineHeight),
                        element.FindPropertyRelative("color"), GUIContent.none);
                    
                    EditorGUI.PropertyField(
                        new Rect(rect.x + colorWidth + space, rect.y, idWidth, singleLineHeight),
                        element.FindPropertyRelative("colorId"), GUIContent.none);

                    EditorGUI.PropertyField(
                        new Rect(rect.x + colorWidth + idWidth + space * 2, rect.y, descriptionWidth, singleLineHeight),
                        element.FindPropertyRelative("description"), GUIContent.none);
                    
                    if (GUI.Button(new Rect(rect.x + rect.width - buttonWidth, rect.y, buttonWidth, singleLineHeight), EditorGUIUtility.IconContent("Search On Icon")))
                    {
                        string swatchName = swatchNameProperty.stringValue;
                        string colorId = element.FindPropertyRelative("colorId").stringValue;
                        FindReferencesInScene(swatchName, colorId);
                    }
                }
            };

            swatchReorderableLists[swatchIndex] = reorderableList;
            return reorderableList;
        }

        public override void OnInspectorGUI()
        {
            if (target != colorModule)
            {
                colorModule = (ColorModule)target;
                if (colorModule == GlobalSettings.GetModule<ColorModule>())
                {
                    ColorModule.RefreshInstance();
                }
            }

            serializedObject.Update();

            // Draw Scheme Name
            EditorGUILayout.PropertyField(schemeNameProperty, new GUIContent("Scheme Name", "Display name for this color scheme (e.g., 'Light', 'Dark')"));
            EditorGUILayout.Space(10);

            // Draw "Add Swatch" section
            DrawAddSwatchSection();

            EditorGUILayout.Space(10);

            // Draw all swatches
            if (colorSwatchesProperty != null && colorSwatchesProperty.arraySize > 0)
            {
                for (int i = 0; i < colorSwatchesProperty.arraySize; i++)
                {
                    DrawSwatchSection(i);
                    EditorGUILayout.Space(5);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No swatches found. Click 'Initialize Default Colors' below.", MessageType.Warning);
            }

            EditorGUILayout.Space(10);
            
            DrawActionButtons();

            if (serializedObject.ApplyModifiedProperties())
            {
                // Clear cached reorderable lists when properties change
                swatchReorderableLists.Clear();
                // Reload foldout states in case swatches were added/removed
                LoadFoldoutStates();
            }
        }

        private void DrawAddSwatchSection()
        {
            EditorGUILayout.LabelField("Swatches", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            newSwatchName = EditorGUILayout.TextField("New Swatch Name", newSwatchName);
            
            EditorGUI.BeginDisabledGroup(!HasCopiedSwatch());
            if (GUILayout.Button("Paste", GUILayout.Width(50)))
            {
                PasteSwatch();
            }
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Create", GUILayout.Width(55)))
            {
                if (!string.IsNullOrEmpty(newSwatchName))
                {
                    if (colorModule.AddSwatch(newSwatchName))
                    {
                        EditorUtility.SetDirty(target);
                        
                        // Initialize new swatch as expanded
                        SaveFoldoutState(newSwatchName, true);
                        
                        newSwatchName = "";
                        swatchReorderableLists.Clear();
                        serializedObject.Update();
                        LoadFoldoutStates();
                    }
                }
                else
                {
                    Debug.LogWarning("Swatch name cannot be empty.");
                }
            }
            
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSwatchSection(int swatchIndex)
        {
            var swatchElement = colorSwatchesProperty.GetArrayElementAtIndex(swatchIndex);
            var swatchNameProperty = swatchElement.FindPropertyRelative("swatchName");
            var isDefaultProperty = swatchElement.FindPropertyRelative("isDefault");
            var colorDefinitionsProperty = swatchElement.FindPropertyRelative("colorDefinitions");
            
            bool isDefault = isDefaultProperty.boolValue;
            string swatchName = swatchNameProperty.stringValue;
            int colorCount = colorDefinitionsProperty != null ? colorDefinitionsProperty.arraySize : 0;

            // Ensure swatch has a foldout state
            if (!swatchFoldouts.ContainsKey(swatchName))
            {
                string key = $"ColorModule_Foldout_{swatchName}";
                swatchFoldouts[swatchName] = MolcaEditorPrefs.GetBool(key, true);
            }

            // Draw swatch header
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            EditorGUILayout.BeginHorizontal();
            
            // Foldout with color count (using GUILayout.Width via Rect)
            Rect foldoutRect = GUILayoutUtility.GetRect(50, EditorGUIUtility.singleLineHeight);
            bool newFoldoutState = EditorGUI.Foldout(foldoutRect, swatchFoldouts[swatchName], $"[{colorCount}]", true);
            
            if (newFoldoutState != swatchFoldouts[swatchName])
            {
                SaveFoldoutState(swatchName, newFoldoutState);
            }
            
            // Swatch name (editable if not default)
            if (isDefault)
            {
                EditorGUILayout.LabelField(swatchName, EditorStyles.boldLabel, GUILayout.Width(80));
            }
            else
            {
                swatchNameProperty.stringValue = EditorGUILayout.TextField(swatchNameProperty.stringValue, GUILayout.MinWidth(100), GUILayout.MaxWidth(200));
            }

            GUILayout.FlexibleSpace();

            // Copy button
            if (GUILayout.Button("Copy", GUILayout.Width(45)))
            {
                CopySwatch(swatchIndex);
            }

            // Delete button (disabled for default swatch)
            EditorGUI.BeginDisabledGroup(isDefault);
            if (GUILayout.Button("Delete", GUILayout.Width(55)))
            {
                if (EditorUtility.DisplayDialog("Delete Swatch", 
                    $"Are you sure you want to delete the '{swatchName}' swatch and all its colors?", 
                    "Delete", "Cancel"))
                {
                    if (colorModule.RemoveSwatch(swatchName))
                    {
                        EditorUtility.SetDirty(target);
                        swatchReorderableLists.Clear();
                        serializedObject.Update();
                        
                        // Clean up foldout state
                        string key = $"ColorModule_Foldout_{swatchName}";
                        MolcaEditorPrefs.DeleteKey(key);
                        swatchFoldouts.Remove(swatchName);
                        return;
                    }
                }
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            // Only draw the color list if the foldout is expanded
            if (swatchFoldouts[swatchName])
            {
                EditorGUILayout.Space(5);

                // Draw color definitions list
                var reorderableList = GetOrCreateReorderableList(swatchIndex);
                reorderableList.DoLayoutList();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawActionButtons()
        {
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Refresh Cache"))
            {
                colorModule.RefreshCache();
                EditorUtility.SetDirty(target);
                Debug.Log("Color cache refreshed.");
            }

            if (GUILayout.Button("Reset to Defaults"))
            {
                if (EditorUtility.DisplayDialog("Reset to Defaults", 
                    "This will clear all saved color settings, remove custom swatches, and restore default colors. Continue?", 
                    "Yes", "No"))
                {
                    colorModule.ResetToDefaults();
                    EditorUtility.SetDirty(target);
                    swatchReorderableLists.Clear();
                    serializedObject.Update();
                    LoadFoldoutStates();
                    AssetDatabase.SaveAssets();
                    Repaint();
                    Debug.Log("Color settings reset to defaults.");
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void FindReferencesInScene(string swatchName, string colorId)
        {
            if (string.IsNullOrEmpty(colorId))
            {
                Debug.LogWarning("Cannot search for an empty Color ID.");
                return;
            }

            var colorIdComponents = FindObjectsByType<ColorID>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var foundObjects = new System.Collections.Generic.List<GameObject>();

            foreach (var idComponent in colorIdComponents)
            {
                var so = new SerializedObject(idComponent);
                var swatchNameProp = so.FindProperty("_swatchName");
                var colorIdProp = so.FindProperty("_colorId");

                // A ColorID applies a single swatch + color ID to all of its targets;
                // ColorTarget carries no per-target color ID, so the component matches
                // when both its swatch name and color ID equal the requested pair.
                bool found = colorIdProp != null && colorIdProp.stringValue == colorId
                             && swatchNameProp != null && swatchNameProp.stringValue == swatchName;

                if (found && !foundObjects.Contains(idComponent.gameObject))
                {
                    foundObjects.Add(idComponent.gameObject);
                }
            }

            if (foundObjects.Count > 0)
            {
                Debug.Log($"Found {foundObjects.Count} GameObject(s) referencing Color ID '{swatchName}.{colorId}'. Selecting them in the Hierarchy.", target);
                Selection.objects = foundObjects.ToArray();
                EditorGUIUtility.PingObject(foundObjects.First());
            }
            else
            {
                Debug.Log($"No references found for Color ID '{swatchName}.{colorId}' in the current scene.", target);
            }
        }
    }
} 