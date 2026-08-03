using UnityEditor;
using Molca.App.UI;

namespace Molca.App.UI.Editor
{
    [CustomEditor(typeof(ButtonState))]
    public class ButtonStateEditor : UnityEditor.Editor
    {
        bool showColorState = false;
        bool showSpriteState = false;
        bool showStateEvents = false;

        public override void OnInspectorGUI()
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exludeFromGroup"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_isOn"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("labelText"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("backgroundImage"));

            showColorState = EditorGUILayout.Foldout(showColorState, "Color State");
            if (showColorState)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("interpolateColor"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("onColor"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("offColor"));
            }

            showSpriteState = EditorGUILayout.Foldout(showSpriteState, "Sprite State");
            if (showSpriteState)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("toggleBackground"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("onSprite"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("offSprite"));
            }

            showStateEvents = EditorGUILayout.Foldout(showStateEvents, "State Events");
            if (showStateEvents)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("onStateChanged"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("onStateOn"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("onStateOff"));
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
