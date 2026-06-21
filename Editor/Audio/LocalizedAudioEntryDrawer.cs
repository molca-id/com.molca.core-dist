using UnityEngine;
using UnityEditor;
using Molca.Audio;
using Molca.Settings;
using Molca.Localization;

namespace Molca.Editor
{
    [CustomPropertyDrawer(typeof(LocalizedAudioEntry))]
    public class LocalizedAudioEntryDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var idProp = property.FindPropertyRelative("id");
            var descriptionProp = property.FindPropertyRelative("description");
            var languageClipsProp = property.FindPropertyRelative("_languageClips");

            // Main foldout for the entire entry
            var mainFoldoutKey = property.propertyPath + "_main_foldout";
            var isMainExpanded = MolcaEditorPrefs.GetBool(mainFoldoutKey, true);
            
            Rect mainFoldoutRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            isMainExpanded = EditorGUI.Foldout(mainFoldoutRect, isMainExpanded, label, true);
            MolcaEditorPrefs.SetBool(mainFoldoutKey, isMainExpanded);

            if (!isMainExpanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            float y = position.y + EditorGUIUtility.singleLineHeight + 2;
            float lineHeight = EditorGUIUtility.singleLineHeight;
            float spacing = EditorGUIUtility.standardVerticalSpacing;
            float labelWidth = 80f;
            float fieldWidth = position.width - labelWidth - 10f;

            // ID field
            EditorGUI.LabelField(new Rect(position.x, y, labelWidth, lineHeight), "ID");
            idProp.stringValue = EditorGUI.TextField(new Rect(position.x + labelWidth, y, fieldWidth, lineHeight), idProp.stringValue);
            y += lineHeight + spacing;

            // Description field
            EditorGUI.LabelField(new Rect(position.x, y, labelWidth, lineHeight), "Description");
            descriptionProp.stringValue = EditorGUI.TextField(new Rect(position.x + labelWidth, y, fieldWidth, lineHeight), descriptionProp.stringValue);
            y += lineHeight + spacing;

            // Localized Audio Clips section with foldout
            var foldoutKey = property.propertyPath + "_foldout";
            var isExpanded = MolcaEditorPrefs.GetBool(foldoutKey, true);
            
            Rect foldoutRect = new Rect(position.x, y, position.width, lineHeight);
            isExpanded = EditorGUI.Foldout(foldoutRect, isExpanded, "Localized Audio Clips", true);
            MolcaEditorPrefs.SetBool(foldoutKey, isExpanded);
            y += lineHeight + spacing;

            if (isExpanded)
            {
                var localizationModule = GlobalSettings.GetModule<LocalizationModule>();
                if (localizationModule != null && languageClipsProp != null)
                {
                    var languageCodes = localizationModule.LanguageCode;
                    languageClipsProp.arraySize = languageCodes.Length;
                    
                    float boxHeight = (lineHeight + spacing) * languageCodes.Length + spacing;
                    Rect boxRect = new Rect(position.x, y, position.width, boxHeight);
                    GUI.Box(boxRect, GUIContent.none);
                    float innerY = y + spacing;
                
                for (int i = 0; i < languageCodes.Length; i++)
                {
                    var entry = languageClipsProp.GetArrayElementAtIndex(i);
                    var langCodeProp = entry.FindPropertyRelative("languageCode");
                    var clipReferenceProp = entry.FindPropertyRelative("clipReference");
                    
                    langCodeProp.stringValue = languageCodes[i];
                    
                    var labelLangRect = new Rect(position.x + 5, innerY, 60, lineHeight);
                    var clipRect = new Rect(position.x + 70, innerY, position.width - 75, lineHeight);
                    
                    EditorGUI.LabelField(labelLangRect, languageCodes[i]);
                    
                    // Make clip reference field read-only in play mode
                    bool wasEnabled = GUI.enabled;
                    GUI.enabled = !Application.isPlaying;
                    EditorGUI.PropertyField(clipRect, clipReferenceProp, GUIContent.none);
                    GUI.enabled = wasEnabled;
                    
                    innerY += lineHeight + spacing;
                }
            }
            else
            {
                EditorGUI.HelpBox(new Rect(position.x, y, position.width, lineHeight), "LocalizationModule not found in GlobalSettings!", MessageType.Error);
            }
        }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            // Check if main foldout is expanded
            var mainFoldoutKey = property.propertyPath + "_main_foldout";
            var isMainExpanded = MolcaEditorPrefs.GetBool(mainFoldoutKey, true);
            
            if (!isMainExpanded)
            {
                return EditorGUIUtility.singleLineHeight; // Only the foldout header
            }

            var languageClipsProp = property.FindPropertyRelative("_languageClips");
            float height = EditorGUIUtility.singleLineHeight * 4 + EditorGUIUtility.standardVerticalSpacing * 3; // ID, Description, Header, and spacing
            
            // Check if localized audio clips foldout is expanded
            var foldoutKey = property.propertyPath + "_foldout";
            var isExpanded = MolcaEditorPrefs.GetBool(foldoutKey, true);
            
            if (isExpanded)
            {
                var localizationModule = GlobalSettings.GetModule<LocalizationModule>();
                if (localizationModule != null)
                {
                    var languageCodes = localizationModule.LanguageCode;
                    var lineHeight = EditorGUIUtility.singleLineHeight;
                    var spacing = EditorGUIUtility.standardVerticalSpacing;
                    // Each language now has 1 line: audio clip
                    height += (lineHeight + spacing) * languageCodes.Length + spacing;
                }
                else
                {
                    height += EditorGUIUtility.singleLineHeight; // Error message height
                }
            }
            
            return height;
        }
    }
} 