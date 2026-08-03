using UnityEngine;
using UnityEditor;

namespace Molca.App.UI.Editor
{
    [CustomEditor(typeof(ColorIDButton))]
    [CanEditMultipleObjects]
    public class ColorIDButtonDrawer : UnityEditor.Editor
    {
        private SerializedProperty normalColorProp;
        private SerializedProperty highlightedColorProp;
        private SerializedProperty pressedColorProp;
        private SerializedProperty selectedColorProp;
        private SerializedProperty disabledColorProp;
        private SerializedProperty onClickProp;
        private SerializedProperty interactableProp;

        // Toggle properties
        private SerializedProperty isToggleButtonProp;
        private SerializedProperty isOnProp;
        private SerializedProperty excludeFromGroupProp;
        private SerializedProperty onToggleChangedProp;
        private SerializedProperty onToggleOnProp;
        private SerializedProperty onToggleOffProp;

        // Event properties
        private SerializedProperty onPointerEnterProp;
        private SerializedProperty onPointerExitProp;

        private bool hasMultipleTargets;
        private bool showEvents;

        protected virtual void OnEnable()
        {
            hasMultipleTargets = targets.Length > 1;
            
            normalColorProp = serializedObject.FindProperty("normalColor");
            highlightedColorProp = serializedObject.FindProperty("highlightedColor");
            pressedColorProp = serializedObject.FindProperty("pressedColor");
            selectedColorProp = serializedObject.FindProperty("selectedColor");
            disabledColorProp = serializedObject.FindProperty("disabledColor");
            onClickProp = serializedObject.FindProperty("m_OnClick");
            interactableProp = serializedObject.FindProperty("m_Interactable");

            // Toggle properties
            isToggleButtonProp = serializedObject.FindProperty("isToggleButton");
            isOnProp = serializedObject.FindProperty("isOn");
            excludeFromGroupProp = serializedObject.FindProperty("excludeFromGroup");
            onToggleChangedProp = serializedObject.FindProperty("onToggleChanged");
            onToggleOnProp = serializedObject.FindProperty("onToggleOn");
            onToggleOffProp = serializedObject.FindProperty("onToggleOff");

            // Event properties
            onPointerEnterProp = serializedObject.FindProperty("onPointerEnter");
            onPointerExitProp = serializedObject.FindProperty("onPointerExit");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            // Show multi-edit indicator
            if (hasMultipleTargets)
            {
                EditorGUILayout.HelpBox($"Editing {targets.Length} ColorIDButton components", MessageType.Info);
                EditorGUILayout.Space(5);
            }
            
            // Interactable property (always visible at the top)
            bool hasMixedInteractableValues = HasMixedInteractableValues();
            string interactableLabel = hasMixedInteractableValues ? "Interactable (Mixed Values)" : "Interactable";
            EditorGUILayout.PropertyField(interactableProp, new GUIContent(interactableLabel));
            
            // Handle mixed values for color properties
            DrawColorPropertyWithMixedValue(normalColorProp, "Normal Color");
            DrawColorPropertyWithMixedValue(highlightedColorProp, "Highlighted Color");
            DrawColorPropertyWithMixedValue(pressedColorProp, "Pressed Color");
            DrawColorPropertyWithMixedValue(selectedColorProp, "Selected Color");
            DrawColorPropertyWithMixedValue(disabledColorProp, "Disabled Color");
            
            bool hasMixedToggleValues = HasMixedToggleButtonValues();
            string toggleLabel = hasMixedToggleValues ? "Is Toggle Button (Mixed Values)" : "Is Toggle Button";
            EditorGUILayout.PropertyField(isToggleButtonProp, new GUIContent(toggleLabel));
            
            if (isToggleButtonProp.boolValue && !hasMixedToggleValues)
            {
                EditorGUI.indentLevel++;
                
                bool hasMixedOnValues = HasMixedIsOnValues();
                string onLabel = hasMixedOnValues ? "Is On (Mixed Values)" : "Is On";
                EditorGUILayout.PropertyField(isOnProp, new GUIContent(onLabel));
                
                bool hasMixedExcludeValues = HasMixedExcludeFromGroupValues();
                string excludeLabel = hasMixedExcludeValues ? "Exclude From Group (Mixed Values)" : "Exclude From Group";
                EditorGUILayout.PropertyField(excludeFromGroupProp, new GUIContent(excludeLabel));
                
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();

            // Events
            showEvents = EditorGUILayout.Foldout(showEvents, "Events");
            if (showEvents)
            {
                EditorGUI.indentLevel++;
                
                if (isToggleButtonProp.boolValue && !hasMixedToggleValues)
                {
                    EditorGUILayout.PropertyField(onToggleChangedProp, new GUIContent("On Toggle Changed"));
                    EditorGUILayout.PropertyField(onToggleOnProp, new GUIContent("On Toggle On"));
                    EditorGUILayout.PropertyField(onToggleOffProp, new GUIContent("On Toggle Off"));
                    EditorGUILayout.Space();
                }
                
                EditorGUILayout.PropertyField(onClickProp, new GUIContent("On Click"));
                EditorGUILayout.PropertyField(onPointerEnterProp, new GUIContent("On Pointer Enter"));
                EditorGUILayout.PropertyField(onPointerExitProp, new GUIContent("On Pointer Exit"));
                
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();

            // Help box for toggle behavior
            if (isToggleButtonProp.boolValue && !hasMixedToggleValues)
            {
                EditorGUILayout.HelpBox(
                    "Toggle Button: Uses selected color when ON, normal color when OFF. " +
                    "Add to a ColorIDButtonGroup for radio/multi-toggle behavior.", 
                    MessageType.Info);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawColorPropertyWithMixedValue(SerializedProperty property, string label)
        {
            if (hasMultipleTargets)
            {
                bool hasMixedValues = HasMixedColorValues(property.name);
                string displayLabel = hasMixedValues ? $"{label} (Mixed Values)" : label;
                EditorGUILayout.PropertyField(property, new GUIContent(displayLabel));
            }
            else
            {
                EditorGUILayout.PropertyField(property, new GUIContent(label));
            }
        }

        private bool HasMixedColorValues(string propertyName)
        {
            if (!hasMultipleTargets) return false;
            
            string firstColorId = null;
            foreach (var target in targets)
            {
                var serializedTarget = new SerializedObject(target);
                var colorProperty = serializedTarget.FindProperty(propertyName);
                
                // ColorTokenReference serializes a single "_tokenId". The V1 shape compared here was the
                // "_swatchName"/"_colorId" pair; a canonical token is one string, so there is nothing to
                // compose. An unassigned token is the empty string, which compares equal across targets —
                // correct, since two unassigned buttons genuinely do not have mixed values.
                if (colorProperty != null)
                {
                    var tokenProperty = colorProperty.FindPropertyRelative("_tokenId");
                    string currentToken = tokenProperty != null ? tokenProperty.stringValue : string.Empty;

                    if (firstColorId == null)
                    {
                        firstColorId = currentToken;
                    }
                    else if (firstColorId != currentToken)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool HasMixedToggleButtonValues()
        {
            if (!hasMultipleTargets) return false;
            
            bool? firstValue = null;
            foreach (var target in targets)
            {
                var serializedTarget = new SerializedObject(target);
                var toggleProperty = serializedTarget.FindProperty("isToggleButton");
                bool currentValue = toggleProperty != null ? toggleProperty.boolValue : false;
                
                if (firstValue == null)
                {
                    firstValue = currentValue;
                }
                else if (firstValue != currentValue)
                {
                    return true;
                }
            }
            return false;
        }

        private bool HasMixedIsOnValues()
        {
            if (!hasMultipleTargets) return false;
            
            bool? firstValue = null;
            foreach (var target in targets)
            {
                var serializedTarget = new SerializedObject(target);
                var isOnProperty = serializedTarget.FindProperty("isOn");
                bool currentValue = isOnProperty != null ? isOnProperty.boolValue : false;
                
                if (firstValue == null)
                {
                    firstValue = currentValue;
                }
                else if (firstValue != currentValue)
                {
                    return true;
                }
            }
            return false;
        }

        private bool HasMixedExcludeFromGroupValues()
        {
            if (!hasMultipleTargets) return false;
            
            bool? firstValue = null;
            foreach (var target in targets)
            {
                var serializedTarget = new SerializedObject(target);
                var excludeProperty = serializedTarget.FindProperty("excludeFromGroup");
                bool currentValue = excludeProperty != null ? excludeProperty.boolValue : false;
                
                if (firstValue == null)
                {
                    firstValue = currentValue;
                }
                else if (firstValue != currentValue)
                {
                    return true;
                }
            }
            return false;
        }

        private bool HasMixedInteractableValues()
        {
            if (!hasMultipleTargets) return false;
            
            bool? firstValue = null;
            foreach (var target in targets)
            {
                var serializedTarget = new SerializedObject(target);
                var interactableProperty = serializedTarget.FindProperty("m_Interactable");
                bool currentValue = interactableProperty != null ? interactableProperty.boolValue : true; // Default to true if not found
                
                if (firstValue == null)
                {
                    firstValue = currentValue;
                }
                else if (firstValue != currentValue)
                {
                    return true;
                }
            }
            return false;
        }
    }
} 