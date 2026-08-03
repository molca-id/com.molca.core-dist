using UnityEngine;
using UnityEditor;

namespace Molca.App.UI.Editor
{
    [CustomEditor(typeof(ColorIDButtonGroup))]
    public class ColorIDButtonGroupDrawer : UnityEditor.Editor
    {
        private SerializedProperty allowMultipleSelectionProp;
        private SerializedProperty allowSwitchOffProp;
        private SerializedProperty requireSelectionProp;
        private SerializedProperty onButtonToggledProp;
        private SerializedProperty onSelectionChangedProp;

        private void OnEnable()
        {
            allowMultipleSelectionProp = serializedObject.FindProperty("allowMultipleSelection");
            allowSwitchOffProp = serializedObject.FindProperty("allowSwitchOff");
            requireSelectionProp = serializedObject.FindProperty("requireSelection");
            onButtonToggledProp = serializedObject.FindProperty("onButtonToggled");
            onSelectionChangedProp = serializedObject.FindProperty("onSelectionChanged");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            // Group Configuration
            EditorGUILayout.PropertyField(allowMultipleSelectionProp, new GUIContent("Allow Multiple Selection"));
            EditorGUILayout.PropertyField(allowSwitchOffProp, new GUIContent("Allow Switch Off"));
            EditorGUILayout.PropertyField(requireSelectionProp, new GUIContent("Require Selection"));

            EditorGUILayout.Space();

            // Events
            EditorGUILayout.LabelField("Group Events", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(onButtonToggledProp, new GUIContent("On Button Toggled"));
            EditorGUILayout.PropertyField(onSelectionChangedProp, new GUIContent("On Selection Changed"));

            EditorGUILayout.Space();

            // Runtime Information
            if (Application.isPlaying)
            {
                DrawRuntimeInfo();
            }

            // Help box
            DrawHelpBox();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawRuntimeInfo()
        {
            var group = target as ColorIDButtonGroup;
            if (group == null) return;

            EditorGUILayout.LabelField("Runtime Information", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Registered Buttons:", GUILayout.Width(120));
            EditorGUILayout.LabelField(group.Buttons.Count.ToString());
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Active Buttons:", GUILayout.Width(120));
            EditorGUILayout.LabelField(group.ActiveButtons.Count.ToString());
            EditorGUILayout.EndHorizontal();

            var firstActive = group.FirstActiveButton;
            if (firstActive != null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("First Active:", GUILayout.Width(120));
                EditorGUILayout.LabelField(firstActive.name);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space();

            // Action buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Group"))
            {
                group.RefreshButtonGroup();
            }
            if (GUILayout.Button("Set All Off"))
            {
                group.SetAllButtonsOff();
            }
            EditorGUILayout.EndHorizontal();

            if (group.AllowMultipleSelection)
            {
                if (GUILayout.Button("Set All On"))
                {
                    group.SetAllButtonsOn();
                }
            }
        }

        private void DrawHelpBox()
        {
            var group = target as ColorIDButtonGroup;
            if (group == null) return;

            string helpText = "";
            
            if (!group.AllowMultipleSelection)
            {
                helpText = "Radio Button Group: Only one button can be selected at a time. ";
                if (group.RequireSelection)
                {
                    helpText += "At least one button must be selected. ";
                }
                if (!group.AllowSwitchOff)
                {
                    helpText += "Buttons cannot be deselected.";
                }
            }
            else
            {
                helpText = "Multi-Select Group: Multiple buttons can be selected simultaneously. ";
                if (!group.AllowSwitchOff)
                {
                    helpText += "Buttons cannot be deselected.";
                }
            }

            helpText += "\n\nAdd ColorIDButton components as children and enable 'Is Toggle Button' on them.";

            EditorGUILayout.HelpBox(helpText, MessageType.Info);
        }
    }
} 