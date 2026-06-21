using UnityEngine;
using UnityEditor;
using Molca.ColorID;

// Edit-mode drawers must work without a RuntimeManager; ColorModule.ResolveActive()
// is internal to the Molca runtime assembly, so the legacy static shims are used here.
#pragma warning disable CS0618

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Custom property drawer for ColorIDReference fields
    /// </summary>
    [CustomPropertyDrawer(typeof(ColorIDReference))]
    public class ColorIDReferenceDrawer : PropertyDrawer
    {
        private const float ColorPreviewWidth = 30f;
        private const float Spacing = 2f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            // Get the serialized properties
            SerializedProperty swatchNameProp = property.FindPropertyRelative("_swatchName");
            SerializedProperty colorIdProp = property.FindPropertyRelative("_colorId");

            // Calculate positions for horizontal layout
            float lineHeight = EditorGUIUtility.singleLineHeight;
            float labelWidth = EditorGUIUtility.labelWidth;
            float remainingWidth = position.width - labelWidth;
            
            // Label
            Rect labelRect = new Rect(position.x, position.y, labelWidth, lineHeight);
            EditorGUI.LabelField(labelRect, label);

            // Color ID dropdown (with swatch grouping)
            float dropdownWidth = remainingWidth - ColorPreviewWidth - Spacing;
            Rect colorIdRect = new Rect(position.x + labelWidth, position.y, dropdownWidth, lineHeight);
            DrawColorIdDropdown(colorIdRect, swatchNameProp, colorIdProp);

            // Color preview
            Rect previewRect = new Rect(position.x + labelWidth + dropdownWidth + Spacing, position.y, ColorPreviewWidth, lineHeight);
            DrawColorPreview(previewRect, swatchNameProp.stringValue, colorIdProp.stringValue);

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }

        private void DrawColorIdDropdown(Rect position, SerializedProperty swatchNameProp, SerializedProperty colorIdProp)
        {
            string[] swatchNames = ColorModule.GetSwatchNames();
            
            if (swatchNames.Length == 0)
            {
                EditorGUI.LabelField(position, "No swatches available");
                return;
            }

            // Build display options with swatch grouping
            var displayOptions = new System.Collections.Generic.List<string>();
            var swatchColorMap = new System.Collections.Generic.List<(string swatchName, string colorId)>();
            
            foreach (string swatchName in swatchNames)
            {
                string[] colorIds = ColorModule.GetColorIdsInSwatch(swatchName);
                foreach (string colorId in colorIds)
                {
                    displayOptions.Add($"{swatchName}/{colorId}");
                    swatchColorMap.Add((swatchName, colorId));
                }
            }

            if (displayOptions.Count == 0)
            {
                EditorGUI.LabelField(position, "No colors available");
                return;
            }

            // Find current index
            string currentSwatchName = swatchNameProp.stringValue;
            string currentColorId = colorIdProp.stringValue;
            int currentIndex = -1;
            for (int i = 0; i < swatchColorMap.Count; i++)
            {
                if (swatchColorMap[i].swatchName == currentSwatchName && swatchColorMap[i].colorId == currentColorId)
                {
                    currentIndex = i;
                    break;
                }
            }

            if (currentIndex == -1 && swatchColorMap.Count > 0)
            {
                currentIndex = 0;
                swatchNameProp.stringValue = swatchColorMap[0].swatchName;
                colorIdProp.stringValue = swatchColorMap[0].colorId;
            }

            // Draw dropdown
            int newIndex = EditorGUI.Popup(position, currentIndex, displayOptions.ToArray());
            if (newIndex != currentIndex && newIndex >= 0 && newIndex < swatchColorMap.Count)
            {
                swatchNameProp.stringValue = swatchColorMap[newIndex].swatchName;
                colorIdProp.stringValue = swatchColorMap[newIndex].colorId;
                swatchNameProp.serializedObject.ApplyModifiedProperties();
                colorIdProp.serializedObject.ApplyModifiedProperties();
            }
        }

        private void DrawColorPreview(Rect position, string swatchName, string colorId)
        {
            Color color;
            try
            {
                if (string.IsNullOrEmpty(colorId) || !ColorModule.HasColor(swatchName, colorId))
                    color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                else
                    color = ColorModule.GetColor(swatchName, colorId);
            }
            catch
            {
                color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            }

            const float b = 1f;
            Color border = Color.black;
            Rect inner = new Rect(position.x + b, position.y + b, position.width - b * 2f, position.height - b * 2f);

            // Border first (outer frame)
            EditorGUI.DrawRect(new Rect(position.x, position.y, position.width, b), border);
            EditorGUI.DrawRect(new Rect(position.x, position.yMax - b, position.width, b), border);
            EditorGUI.DrawRect(new Rect(position.x, position.y, b, position.height), border);
            EditorGUI.DrawRect(new Rect(position.xMax - b, position.y, b, position.height), border);

            const float alphaBarHeight = 3f;
            Rect colorRect = new Rect(inner.x, inner.y, inner.width, inner.height - alphaBarHeight);
            Rect alphaBarRect = new Rect(inner.x, inner.yMax - alphaBarHeight, inner.width, alphaBarHeight);

            // Main area: opaque color (Unity-style)
            EditorGUI.DrawRect(colorRect, new Color(color.r, color.g, color.b, 1f));

            // Alpha bar at bottom
            Color barBg = EditorGUIUtility.isProSkin ? new Color(0.2f, 0.2f, 0.2f) : new Color(0.7f, 0.7f, 0.7f);
            EditorGUI.DrawRect(alphaBarRect, barBg);
            if (color.a > 0.001f)
            {
                Rect alphaFill = new Rect(alphaBarRect.x, alphaBarRect.y, alphaBarRect.width * color.a, alphaBarRect.height);
                EditorGUI.DrawRect(alphaFill, Color.white);
            }
        }
    }
} 