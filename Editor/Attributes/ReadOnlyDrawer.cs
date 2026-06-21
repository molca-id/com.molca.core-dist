using UnityEngine;
using UnityEditor;
using Molca.Attributes;

namespace Molca.Editor
{
    [CustomPropertyDrawer(typeof(ReadOnlyAttribute))]
    public class ReadOnlyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Save the current GUI enabled state
            bool previousGUIState = GUI.enabled;
            
            // Disable the GUI
            GUI.enabled = false;
            
            // Draw the property
            EditorGUI.PropertyField(position, property, label, true);
            
            // Restore the previous GUI state
            GUI.enabled = previousGUIState;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUI.GetPropertyHeight(property, label);
        }
    }
} 