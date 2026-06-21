using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Molca.Editor.Utils
{
    /// <summary>
    /// Utility to fix invalid auxiliary types by directly editing the YAML
    /// </summary>
    public static class AuxiliaryTypeFixerUtility
    {
        /// <summary>
        /// Fixes an invalid auxiliary by updating its type reference in the YAML file
        /// </summary>
        /// <param name="step">The step containing the invalid auxiliary</param>
        /// <param name="auxiliaryIndex">Index of the auxiliary</param>
        /// <param name="newType">The correct type to assign</param>
        /// <returns>True if successful</returns>
        public static bool FixAuxiliaryTypeInYaml(Molca.Sequence.Step step, int auxiliaryIndex, Type newType)
        {
            try
            {
                // Get the scene file path
                string scenePath = step.gameObject.scene.path;
                if (string.IsNullOrEmpty(scenePath))
                {
                    Debug.LogError("[AuxiliaryTypeFixer] Could not determine scene path");
                    return false;
                }

                // Get stepId for finding the correct MonoBehaviour block.
                // stepId is an int field; reading it as stringValue throws.
                SerializedObject serializedStep = new SerializedObject(step);
                var stepIdProperty = serializedStep.FindProperty("stepId");
                if (stepIdProperty == null)
                {
                    Debug.LogError("[AuxiliaryTypeFixer] Could not find stepId");
                    return false;
                }
                string stepId = stepIdProperty.intValue.ToString();

                // Read the scene YAML
                string yamlContent = File.ReadAllText(scenePath);
                
                // Find the MonoBehaviour block for this step
                string monoBehaviourPattern = @"(--- !u!114 &\d+\r?\nMonoBehaviour:[\s\S]*?stepId: " + Regex.Escape(stepId) + @"[\s\S]*?)(?=\r?\n--- !u!|$)";
                Match monoBehaviourMatch = Regex.Match(yamlContent, monoBehaviourPattern);
                
                if (!monoBehaviourMatch.Success)
                {
                    Debug.LogError($"[AuxiliaryTypeFixer] Could not find MonoBehaviour block with stepId: {stepId}");
                    return false;
                }

                string monoBehaviourBlock = monoBehaviourMatch.Groups[1].Value;
                
                // Extract the reference ID for the auxiliary at the given index
                string refId = ExtractAuxiliaryReferenceId(monoBehaviourBlock, auxiliaryIndex);
                if (string.IsNullOrEmpty(refId))
                {
                    Debug.LogError($"[AuxiliaryTypeFixer] Could not find reference ID for auxiliary at index {auxiliaryIndex}");
                    return false;
                }

                // Get the type information for the new type
                string className = newType.Name;
                string namespaceName = newType.Namespace ?? "";
                string assemblyName = newType.Assembly.GetName().Name;

                // Find and replace the type information in the references section
                // Pattern to match the specific reference entry
                string refPattern = $@"(\s+- rid: {refId}\r?\n\s+type: \{{)class: [^,]*, ns: [^,]*, asm: [^}}]*(\}})";
                string replacement = $"$1class: {className}, ns: {namespaceName}, asm: {assemblyName}$2";
                
                string updatedYaml = Regex.Replace(yamlContent, refPattern, replacement);
                
                if (updatedYaml == yamlContent)
                {
                    Debug.LogWarning($"[AuxiliaryTypeFixer] No changes made - could not find type reference for rid {refId}");
                    return false;
                }

                // Write the updated YAML back to the file
                File.WriteAllText(scenePath, updatedYaml);
                
                Debug.Log($"<color=green>[AuxiliaryTypeFixer]</color> Successfully updated type reference in YAML for rid {refId} to {className}");
                Debug.Log($"[AuxiliaryTypeFixer] Scene file updated: {scenePath}");
                Debug.Log($"[AuxiliaryTypeFixer] You need to reload the scene to see the changes.");
                
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[AuxiliaryTypeFixer] Error fixing auxiliary type: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        private static string ExtractAuxiliaryReferenceId(string monoBehaviourBlock, int auxiliaryIndex)
        {
            // Find the auxiliaries array
            string pattern = @"auxiliaries:\s*[\r\n]+((?:\s*- rid: \d+\s*[\r\n]+)+)";
            Match match = Regex.Match(monoBehaviourBlock, pattern);

            if (match.Success)
            {
                string auxiliariesBlock = match.Groups[1].Value;
                var rids = Regex.Matches(auxiliariesBlock, @"rid: (\d+)");

                if (auxiliaryIndex < rids.Count)
                {
                    return rids[auxiliaryIndex].Groups[1].Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Prompts the user to reload the scene after YAML changes
        /// </summary>
        public static void PromptSceneReload(string scenePath)
        {
            if (EditorUtility.DisplayDialog(
                "Scene Modified",
                "The scene file has been updated directly. You need to reload the scene to see the changes.\n\nReload now?",
                "Reload Scene",
                "Later"))
            {
                // Save current scene first if it has unsaved changes
                if (EditorSceneManager.GetActiveScene().isDirty)
                {
                    if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    {
                        return; // User cancelled
                    }
                }

                // Reload the scene
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            }
        }
    }
}

