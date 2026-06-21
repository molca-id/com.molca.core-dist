using UnityEngine;
using UnityEditor;
using Molca.Attributes;

namespace Molca.Editor
{
    [CustomPropertyDrawer(typeof(ExpandableAttribute))]
    public class ExpandableDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                EditorGUI.PropertyField(position, property, label);
                EditorGUI.EndProperty();
                return;
            }

            var scriptableObject = property.objectReferenceValue as ScriptableObject;
            var propertyRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

            if (scriptableObject == null)
            {
                EditorGUI.PropertyField(propertyRect, property, label);
            }
            else
            {
                // Draw a foldout for the label
                var foldoutRect = new Rect(propertyRect.x, propertyRect.y, EditorGUIUtility.labelWidth, propertyRect.height);
                property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);

                // Draw the object field
                var objectFieldRect = new Rect(propertyRect.x + EditorGUIUtility.labelWidth, propertyRect.y, propertyRect.width - EditorGUIUtility.labelWidth, propertyRect.height);
                EditorGUI.ObjectField(objectFieldRect, property, GUIContent.none);

                if (property.isExpanded)
                {
                    EditorGUI.indentLevel++;
                    var serializedObject = new SerializedObject(scriptableObject);
                    var iterator = serializedObject.GetIterator();
                    var y = position.y + EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

                    iterator.NextVisible(true); // Skip script field
                    while (iterator.NextVisible(false))
                    {
                        var height = EditorGUI.GetPropertyHeight(iterator, true);
                        var rect = new Rect(position.x, y, position.width, height);
                        EditorGUI.PropertyField(rect, iterator, true);
                        y += height + EditorGUIUtility.standardVerticalSpacing;
                    }

                    if (GUI.changed)
                    {
                        serializedObject.ApplyModifiedProperties();
                    }
                    EditorGUI.indentLevel--;
                }
            }
            
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            
            if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null && property.isExpanded)
            {
                var scriptableObject = property.objectReferenceValue as ScriptableObject;
                if (scriptableObject != null)
                {
                    var serializedObject = new SerializedObject(scriptableObject);
                    var iterator = serializedObject.GetIterator();
                    
                    iterator.NextVisible(true); // Skip script
                    while (iterator.NextVisible(false))
                    {
                        height += EditorGUI.GetPropertyHeight(iterator, true) + EditorGUIUtility.standardVerticalSpacing;
                    }
                }
            }
            
            return height;
        }
    }
} 