using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;
using Molca.Attributes;

namespace Molca.Editor
{
    /// <summary>
    /// Property drawer for ShowIf and HideIf attributes.
    /// Supports both fields and properties (including computed properties).
    /// </summary>
    [CustomPropertyDrawer(typeof(ShowIfAttribute))]
    [CustomPropertyDrawer(typeof(HideIfAttribute))]
    public class ConditionalDrawer : PropertyDrawer
    {
        private bool ShouldShow(SerializedProperty property)
        {
            var showIfAttribute = attribute as ShowIfAttribute;
            var hideIfAttribute = attribute as HideIfAttribute;
            string boolFieldName = showIfAttribute?.boolFieldName ?? hideIfAttribute?.boolFieldName;

            if (string.IsNullOrEmpty(boolFieldName))
                return true;

            // Get the parent object that contains this property
            object targetObject = GetParentObjectOfProperty(property);
            if (targetObject == null)
            {
                Debug.LogWarning($"Could not get parent object for property {property.propertyPath}");
                return true;
            }

            System.Type targetType = targetObject.GetType();

            // Try to find the field in the target object
            FieldInfo field = targetType.GetField(boolFieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            PropertyInfo propertyInfo = null;
            
            if (field == null)
            {
                // Try to find a property instead (supports computed properties)
                propertyInfo = targetType.GetProperty(boolFieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (propertyInfo == null)
                {
                    Debug.LogWarning($"Could not find boolean field or property '{boolFieldName}' in {targetType.Name}");
                    return true;
                }
            }

            // Get the value of the boolean field or property
            bool value = field != null 
                ? (bool)field.GetValue(targetObject)
                : (bool)propertyInfo.GetValue(targetObject);

            // Return true if we should show the property
            if (showIfAttribute != null)
                return value;
            else
                return !value;
        }

        private object GetParentObjectOfProperty(SerializedProperty property)
        {
            var path = property.propertyPath.Replace(".Array.data[", "[");
            object obj = property.serializedObject.targetObject;
            var elements = path.Split('.');
            
            // Remove the last element (the property itself) to get the parent object
            var parentElements = new string[elements.Length - 1];
            Array.Copy(elements, parentElements, elements.Length - 1);
            
            foreach (var element in parentElements)
            {
                if (element.Contains("["))
                {
                    var elementName = element.Substring(0, element.IndexOf("["));
                    var index = Convert.ToInt32(element.Substring(element.IndexOf("[")).Replace("[", "").Replace("]", ""));
                    obj = GetValue_Imp(obj, elementName, index);
                }
                else
                {
                    obj = GetValue_Imp(obj, element);
                }
                if (obj == null) { return null; }
            }
            return obj;
        }

        private object GetTargetObjectOfProperty(SerializedProperty property)
        {
            var path = property.propertyPath.Replace(".Array.data[", "[");
            object obj = property.serializedObject.targetObject;
            var elements = path.Split('.');
            foreach (var element in elements)
            {
                if (element.Contains("["))
                {
                    var elementName = element.Substring(0, element.IndexOf("["));
                    var index = Convert.ToInt32(element.Substring(element.IndexOf("[")).Replace("[", "").Replace("]", ""));
                    obj = GetValue_Imp(obj, elementName, index);
                }
                else
                {
                    obj = GetValue_Imp(obj, element);
                }
                if (obj == null) { return null; }
            }
            return obj;
        }

        private object GetValue_Imp(object source, string name)
        {
            if (source == null)
                return null;
            var type = source.GetType();

            while (type != null)
            {
                var f = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (f != null)
                    return f.GetValue(source);

                var p = type.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p != null)
                    return p.GetValue(source, null);

                type = type.BaseType;
            }
            return null;
        }

        private object GetValue_Imp(object source, string name, int index)
        {
            var enumerable = GetValue_Imp(source, name) as System.Collections.IEnumerable;
            if (enumerable == null) return null;
            var enm = enumerable.GetEnumerator();
            //while (index-- >= 0)
            //    enm.MoveNext();//.Current;
            for (int i = 0; i <= index; i++)
            {
                if (!enm.MoveNext()) return null;
            }
            return enm.Current;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (ShouldShow(property))
            {
                EditorGUI.PropertyField(position, property, label, true);
            }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (ShouldShow(property))
            {
                return EditorGUI.GetPropertyHeight(property, label);
            }
            return -EditorGUIUtility.standardVerticalSpacing;
        }
    }
} 