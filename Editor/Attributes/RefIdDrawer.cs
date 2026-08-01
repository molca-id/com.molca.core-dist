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

        /// <summary>
        /// Assigns a fresh Ref Id to the host provider, after showing what it will break.
        /// </summary>
        /// <remarks>
        /// Regenerating an id that has inbound references is a destructive change, so it is confirmed
        /// first and the affected sites are recorded afterwards. It used to offer to rewrite every
        /// matching <c>refId</c> string in the loaded scenes instead, which pointed references at the
        /// wrong object whenever the id was shared — the exact situation that motivates regenerating one.
        /// </remarks>
        private void TryRegenerateId(SerializedProperty property)
        {
            var refType = GetRefType(property);
            if (string.IsNullOrEmpty(refType))
            {
                Debug.LogWarning("[RefId] Host object does not implement IReferenceable — cannot determine RefType.");
                return;
            }

            var oldId = property.stringValue;
            var displayName = (property.serializedObject.targetObject as IReferenceable)?.DisplayName
                ?? property.serializedObject.targetObject.name;

            // Resolve the inbound sites before the change, while the old id is still the stored value.
            var inbound = RefIdEditorUtility.FindInboundSites(oldId);

            if (!RefIdEditorUtility.ConfirmIdChange(inbound, oldId, displayName))
                return;

            var newId = ReferenceGenerator.GenerateUniqueId(refType);
            property.stringValue = newId;
            property.serializedObject.ApplyModifiedProperties();

            RefIdEditorUtility.ReportBrokenInboundReferences(inbound, oldId, newId, displayName);
        }

        private static string GetRefType(SerializedProperty property)
        {
            return (property.serializedObject.targetObject as IReferenceable)?.RefType;
        }
    }
}
