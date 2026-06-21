#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using Molca.Settings;
using Molca.Settings.Notification;
using Molca.Editor.UI;

namespace Molca.Editor
{
    [CustomEditor(typeof(NotificationSettings))]
    public class NotificationSettingsEditor : UnityEditor.Editor
    {
        private GUIStyle _boxStyle;

        public override void OnInspectorGUI()
        {
            if (_boxStyle == null)
            {
                _boxStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(15, 15, 15, 15),
                    margin = new RectOffset(5, 5, 5, 5)
                };
            }

            serializedObject.Update();

            EditorGUILayout.BeginVertical(_boxStyle);

            // Providers array
            var providersProperty = serializedObject.FindProperty("providers");
            EditorGUILayout.PropertyField(providersProperty, new GUIContent("Notification Providers"), true);

            EditorGUILayout.Space(10);

            // Show status of each provider
            if (providersProperty.arraySize > 0)
            {
                EditorGUILayout.LabelField("Provider Status", EditorStyles.miniBoldLabel);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                for (int i = 0; i < providersProperty.arraySize; i++)
                {
                    var providerProperty = providersProperty.GetArrayElementAtIndex(i);
                    var provider = providerProperty.objectReferenceValue as NotificationProvider;

                    if (provider != null)
                    {
                        EditorGUILayout.BeginHorizontal();
                        
                        // Status indicator - Option 1: Unicode circle with color
                        var statusColor = provider.IsEnabled ? MolcaEditorColors.StatusOk : MolcaEditorColors.StatusIdle;
                        var statusIcon = provider.IsEnabled ? "●" : "○";
                        
                        var iconStyle = new GUIStyle(EditorStyles.label)
                        {
                            fontSize = 16,
                            normal = { textColor = statusColor },
                            alignment = TextAnchor.MiddleCenter
                        };
                        
                        GUILayout.Label(statusIcon, iconStyle, GUILayout.Width(20));

                        // Provider info
                        EditorGUILayout.LabelField(provider.DisplayName, EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.LabelField(provider.GetStatusMessage(), EditorStyles.miniLabel, GUILayout.Width(200));
                        
                        EditorGUILayout.EndHorizontal();
                    }
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndVertical();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif

