using UnityEngine;
using UnityEditor;

namespace Molca.Editor
{
    [CustomPropertyDrawer(typeof(Molca.Audio.AudioCollection.AudioEntry))]
    public class AudioEntryDrawer : PropertyDrawer
    {
        private static bool foldout = true;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var idProp = property.FindPropertyRelative("id");
            var clipReferenceProp = property.FindPropertyRelative("clipReference");
            var descriptionProp = property.FindPropertyRelative("description");

            float y = position.y;
            float lineHeight = EditorGUIUtility.singleLineHeight + 2;
            float labelWidth = 80f;
            float fieldWidth = position.width - labelWidth - 10f;

            // Foldout with ID as label
            foldout = EditorGUI.Foldout(new Rect(position.x, y, position.width, lineHeight), foldout, string.IsNullOrEmpty(idProp.stringValue) ? "Audio Entry" : idProp.stringValue, true);
            y += lineHeight;

            if (foldout)
            {
                // ID
                EditorGUI.LabelField(new Rect(position.x, y, labelWidth, lineHeight), "Id");
                idProp.stringValue = EditorGUI.TextField(new Rect(position.x + labelWidth, y, fieldWidth, lineHeight), idProp.stringValue);
                y += lineHeight;

                // AssetReference
                EditorGUI.LabelField(new Rect(position.x, y, labelWidth, lineHeight), "Clip");
                EditorGUI.PropertyField(new Rect(position.x + labelWidth, y, fieldWidth, lineHeight), clipReferenceProp, GUIContent.none);
                y += lineHeight;

                // Description
                EditorGUI.LabelField(new Rect(position.x, y, labelWidth, lineHeight), "Description");
                descriptionProp.stringValue = EditorGUI.TextField(new Rect(position.x + labelWidth, y, fieldWidth, lineHeight), descriptionProp.stringValue);
                y += lineHeight;
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (!foldout)
                return EditorGUIUtility.singleLineHeight + 2;
            return (EditorGUIUtility.singleLineHeight + 2) * 4 + 4;
        }
    }
} 