using UnityEngine;
using UnityEditor;
using Molca.Localization;
using Molca.Settings;

namespace Molca.Editor.Localization
{
    [CustomEditor(typeof(LocalizationManager))]
    public class LocalizationManagerDrawer : UnityEditor.Editor
    {
        private LocalizationModule _localizationModule;

        private void OnEnable()
        {
            _localizationModule = GlobalSettings.GetModule<LocalizationModule>();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Draw default inspector for all properties
            DrawDefaultInspector();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Localization Configuration", EditorStyles.boldLabel);

            if (_localizationModule != null && _localizationModule.LanguageCode != null)
            {
                EditorGUILayout.HelpBox(
                    "This LocalizationManager now uses Unity's StringDatabase API to access the 'Dynamic' table collection.\n\n" +
                    "Make sure the 'Dynamic' table collection is properly configured in Unity's Localization system and included in Addressables.",
                    MessageType.Info
                );

                EditorGUILayout.LabelField("Supported Languages:", EditorStyles.boldLabel);
                for (int i = 0; i < _localizationModule.LanguageCode.Length; i++)
                {
                    var languageCode = _localizationModule.LanguageCode[i];
                    EditorGUILayout.LabelField($"• {languageCode}");
                }

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Required Configuration:", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("• Dynamic table collection exists in Unity's Localization system");
                EditorGUILayout.LabelField("• Dynamic table is included in Addressables groups");
                EditorGUILayout.LabelField("• Table collection name is 'Dynamic'");
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "LocalizationModule not found in GlobalSettings! Please configure the LocalizationModule first.",
                    MessageType.Error
                );
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
} 