using UnityEngine;
using UnityEditor;
using Molca.Attributes;
using Molca.ReferenceSystem;

namespace Molca.Editor
{
    [CustomPropertyDrawer(typeof(RefIdAttribute))]
    public class RefIdDrawer : PropertyDrawer
    {
        private const float ButtonWidth = 20f;
        private const float Spacing = 2f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.String)
            {
                EditorGUI.LabelField(position, label.text, "[RefId] requires a string field");
                return;
            }

            var buttonRect = new Rect(position.xMax - ButtonWidth, position.y, ButtonWidth, position.height);
            var fieldRect = new Rect(position.x, position.y, position.width - ButtonWidth - Spacing, position.height);

            bool previousGUIState = GUI.enabled;
            GUI.enabled = false;
            EditorGUI.PropertyField(fieldRect, property, label);
            GUI.enabled = previousGUIState;

            var refreshIcon = EditorGUIUtility.IconContent("Refresh", "Regenerate ID");
            if (GUI.Button(buttonRect, refreshIcon, EditorStyles.iconButton))
                TryRegenerateId(property);

            HandleContextMenu(fieldRect, property);
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUI.GetPropertyHeight(property, label);
        }

        private void HandleContextMenu(Rect fieldRect, SerializedProperty property)
        {
            var current = Event.current;
            if (current.type != EventType.ContextClick || !fieldRect.Contains(current.mousePosition))
                return;

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Regenerate ID"), false, () => TryRegenerateId(property));
            menu.AddItem(new GUIContent("Copy ID"), false, () => GUIUtility.systemCopyBuffer = property.stringValue);
            menu.ShowAsContext();
            current.Use();
        }

        private void TryRegenerateId(SerializedProperty property)
        {
            var refType = GetRefType(property);
            if (string.IsNullOrEmpty(refType))
            {
                Debug.LogWarning("[RefId] Host object does not implement IReferenceable — cannot determine RefType.");
                return;
            }

            var oldId = property.stringValue;
            var newId = ReferenceGenerator.GenerateUniqueId(refType);

            property.stringValue = newId;
            property.serializedObject.ApplyModifiedProperties();

            if (!string.IsNullOrEmpty(oldId))
            {
                var displayName = (property.serializedObject.targetObject as IReferenceable)?.DisplayName
                    ?? property.serializedObject.targetObject.name;
                RefIdEditorUtility.OfferAndApplyRedirectInLoadedScenes(oldId, newId, displayName);
            }
        }

        private static string GetRefType(SerializedProperty property)
        {
            return (property.serializedObject.targetObject as IReferenceable)?.RefType;
        }
    }
}
