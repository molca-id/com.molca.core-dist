using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor.Utils
{
    /// <summary>
    /// Editor window for bulk migrating auxiliary types to a new assembly
    /// </summary>
    public class AuxiliaryAssemblyMigrationWindow : EditorWindow
    {
        // Spacing/sizing constants mirror the editor design language rhythm (Sprint 27.5). This window
        // stays IMGUI — the doc permits IMGUI reuse for mature tools — so the language is applied through
        // consistent section spacing and helpBox "cards" rather than a UI Toolkit port.
        private const float SectionSpacing = 10f;
        private const float ScanButtonHeight = 30f;
        private const float ApplyButtonHeight = 40f;
        private const float ResultsMaxHeight = 200f;

        private string sourceAssembly = "";
        private string targetAssembly = "";
        private string namespaceFilter = "";
        private Vector2 scrollPosition;
        private List<AuxiliaryReference> foundReferences = new List<AuxiliaryReference>();
        private bool hasScanned = false;
        private Scene currentScene;

        [System.Serializable]
        private class AuxiliaryReference
        {
            public string rid;
            public string className;
            public string namespaceName;
            public string assemblyName;
            public int lineNumber;
            public string context; // A few lines of context for preview
        }

        [MenuItem("Molca/Diagnostics/Migrate Auxiliary Assemblies", priority = 72)]
        public static void ShowWindow()
        {
            var window = GetWindow<AuxiliaryAssemblyMigrationWindow>("Auxiliary Assembly Migration");
            window.titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Auxiliary Assembly Migration", "utils");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Auxiliary Assembly Migration", "utils");
            currentScene = SceneManager.GetActiveScene();
            
            // Set default values if empty
            if (string.IsNullOrEmpty(sourceAssembly))
            {
                sourceAssembly = "Assembly-CSharp";
            }
            if (string.IsNullOrEmpty(targetAssembly))
            {
                targetAssembly = "MolcaSDK.VR";
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(SectionSpacing);
            EditorGUILayout.LabelField("Bulk Auxiliary Assembly Migration", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This tool will scan the current scene and update all auxiliary type references from one assembly to another. " +
                "Useful when you've moved types to a new assembly definition.",
                MessageType.Info);

            EditorGUILayout.Space(SectionSpacing);

            // Scene info
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Current Scene:", EditorStyles.boldLabel);
            if (currentScene.IsValid())
            {
                EditorGUILayout.LabelField("Name:", currentScene.name);
                EditorGUILayout.LabelField("Path:", currentScene.path);
            }
            else
            {
                EditorGUILayout.HelpBox("No scene loaded!", MessageType.Warning);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(SectionSpacing);

            // Migration settings
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Migration Settings:", EditorStyles.boldLabel);
            
            sourceAssembly = EditorGUILayout.TextField("Source Assembly:", sourceAssembly);
            targetAssembly = EditorGUILayout.TextField("Target Assembly:", targetAssembly);
            namespaceFilter = EditorGUILayout.TextField("Namespace Filter (optional):", namespaceFilter);
            
            EditorGUILayout.HelpBox(
                "Leave namespace filter empty to migrate all auxiliaries from the source assembly. " +
                "Or specify a namespace (e.g., 'MolcaSDK.VR') to only migrate types from that namespace.",
                MessageType.Info);
            
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(SectionSpacing);

            // Scan button
            EditorGUI.BeginDisabledGroup(!currentScene.IsValid() || string.IsNullOrEmpty(sourceAssembly) || string.IsNullOrEmpty(targetAssembly));
            if (GUILayout.Button("Scan Scene", GUILayout.Height(ScanButtonHeight)))
            {
                ScanScene();
            }
            EditorGUI.EndDisabledGroup();

            // Results
            if (hasScanned)
            {
                EditorGUILayout.Space(SectionSpacing);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                
                if (foundReferences.Count > 0)
                {
                    EditorGUILayout.LabelField($"Found {foundReferences.Count} auxiliary references to migrate:", EditorStyles.boldLabel);
                    
                    scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.MaxHeight(ResultsMaxHeight));
                    
                    foreach (var reference in foundReferences)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"• {reference.className}", GUILayout.Width(200));
                        EditorGUILayout.LabelField($"({reference.namespaceName})", GUILayout.Width(200));
                        EditorGUILayout.LabelField($"rid: {reference.rid}", GUILayout.Width(150));
                        EditorGUILayout.EndHorizontal();
                    }
                    
                    EditorGUILayout.EndScrollView();
                    
                    EditorGUILayout.Space(SectionSpacing);
                    
                    // Apply button
                    GUI.backgroundColor = Color.green;
                    if (GUILayout.Button("Apply Migration", GUILayout.Height(ApplyButtonHeight)))
                    {
                        if (EditorUtility.DisplayDialog(
                            "Confirm Migration",
                            $"This will update {foundReferences.Count} auxiliary type references in the scene YAML.\n\n" +
                            $"From assembly: {sourceAssembly}\n" +
                            $"To assembly: {targetAssembly}\n\n" +
                            "The scene will be reloaded after the operation. Continue?",
                            "Apply Migration",
                            "Cancel"))
                        {
                            ApplyMigration();
                        }
                    }
                    GUI.backgroundColor = Color.white;
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        $"No auxiliary references found with assembly '{sourceAssembly}'" +
                        (string.IsNullOrEmpty(namespaceFilter) ? "" : $" and namespace '{namespaceFilter}'"),
                        MessageType.Info);
                }
                
                EditorGUILayout.EndVertical();
            }
        }

        private void ScanScene()
        {
            foundReferences.Clear();
            hasScanned = false;

            if (!currentScene.IsValid())
            {
                EditorUtility.DisplayDialog("Error", "No valid scene loaded!", "OK");
                return;
            }

            string scenePath = currentScene.path;
            if (string.IsNullOrEmpty(scenePath))
            {
                EditorUtility.DisplayDialog("Error", "Scene must be saved first!", "OK");
                return;
            }

            try
            {
                string yamlContent = File.ReadAllText(scenePath);
                
                // Pattern to match auxiliary type references in the references section
                // Looking for: type: {class: ClassName, ns: Namespace, asm: AssemblyName}
                string pattern = @"- rid: (\d+)\s*\n\s*type: \{class: ([^,]+), ns: ([^,]*), asm: ([^}]+)\}";
                
                MatchCollection matches = Regex.Matches(yamlContent, pattern);
                
                string[] lines = yamlContent.Split('\n');
                
                foreach (Match match in matches)
                {
                    string rid = match.Groups[1].Value;
                    string className = match.Groups[2].Value.Trim();
                    string namespaceName = match.Groups[3].Value.Trim();
                    string assemblyName = match.Groups[4].Value.Trim();
                    
                    // Check if this matches our source assembly
                    if (assemblyName == sourceAssembly)
                    {
                        // Check namespace filter if specified
                        if (!string.IsNullOrEmpty(namespaceFilter) && !namespaceName.StartsWith(namespaceFilter))
                        {
                            continue;
                        }
                        
                        // Find line number for context
                        int lineNumber = 0;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Contains($"rid: {rid}"))
                            {
                                lineNumber = i + 1;
                                break;
                            }
                        }
                        
                        foundReferences.Add(new AuxiliaryReference
                        {
                            rid = rid,
                            className = className,
                            namespaceName = namespaceName,
                            assemblyName = assemblyName,
                            lineNumber = lineNumber
                        });
                    }
                }
                
                hasScanned = true;
                Debug.Log($"[AuxiliaryMigration] Scan complete. Found {foundReferences.Count} references to migrate.");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Error", $"Failed to scan scene: {e.Message}", "OK");
                Debug.LogError($"[AuxiliaryMigration] Scan error: {e.Message}\n{e.StackTrace}");
            }
        }

        private void ApplyMigration()
        {
            if (foundReferences.Count == 0)
            {
                EditorUtility.DisplayDialog("Error", "No references to migrate!", "OK");
                return;
            }

            string scenePath = currentScene.path;
            
            try
            {
                string yamlContent = File.ReadAllText(scenePath);
                string originalYaml = yamlContent;
                
                int successCount = 0;
                
                // Update each reference
                foreach (var reference in foundReferences)
                {
                    // Pattern to match this specific reference
                    string pattern = $@"(- rid: {reference.rid}\s*\n\s*type: \{{class: {Regex.Escape(reference.className)}, ns: {Regex.Escape(reference.namespaceName)}, asm: ){Regex.Escape(reference.assemblyName)}(\}})";
                    string replacement = $"$1{targetAssembly}$2";
                    
                    string newYaml = Regex.Replace(yamlContent, pattern, replacement);
                    
                    if (newYaml != yamlContent)
                    {
                        yamlContent = newYaml;
                        successCount++;
                    }
                }
                
                if (successCount > 0)
                {
                    // Write back to file
                    File.WriteAllText(scenePath, yamlContent);
                    
                    Debug.Log($"<color=green>[AuxiliaryMigration]</color> Successfully migrated {successCount} auxiliary references from '{sourceAssembly}' to '{targetAssembly}'");
                    
                    // Prompt for reload
                    if (EditorUtility.DisplayDialog(
                        "Migration Complete",
                        $"Successfully updated {successCount} auxiliary references.\n\nReload the scene now to apply changes?",
                        "Reload Scene",
                        "Later"))
                    {
                        // Save any other open scenes first
                        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                        {
                            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                            
                            // Clear the results
                            foundReferences.Clear();
                            hasScanned = false;
                            
                            EditorUtility.DisplayDialog(
                                "Success",
                                $"Scene reloaded successfully!\n\n{successCount} auxiliaries should now be valid.",
                                "OK");
                        }
                    }
                }
                else
                {
                    EditorUtility.DisplayDialog("Warning", "No changes were made. Pattern matching may have failed.", "OK");
                }
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Error", $"Failed to apply migration: {e.Message}", "OK");
                Debug.LogError($"[AuxiliaryMigration] Migration error: {e.Message}\n{e.StackTrace}");
            }
        }
    }
}

