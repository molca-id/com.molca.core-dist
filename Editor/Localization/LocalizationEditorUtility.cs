using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Molca.Localization;

namespace Molca.Editor
{
    public class LocalizationEditorUtility
    {
        [MenuItem("Molca/Diagnostics/Refresh Text Style", priority = 71)]
        public static void RefreshTextStyle()
        {
            var localizedTexts = Object.FindObjectsByType<LocalizedText>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int count = 0;
            
            foreach (var lt in localizedTexts)
            {
                lt.ApplyStyle();
                EditorUtility.SetDirty(lt);
                count++;
            }
            
            if (count > 0)
            {
                EditorSceneManager.MarkAllScenesDirty();
                EditorUtility.DisplayDialog(
                    "Refresh Text Style", 
                    $"Successfully refreshed {count} LocalizedText component{(count != 1 ? "s" : "")}.", 
                    "OK"
                );
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Refresh Text Style", 
                    "No LocalizedText components found in the current scene(s).", 
                    "OK"
                );
            }
        }
    }
}