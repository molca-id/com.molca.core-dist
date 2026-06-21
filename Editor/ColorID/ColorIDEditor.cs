using UnityEngine;
using UnityEditor;
using Molca.ColorID;
using System.Linq;

// Edit-mode drawers must work without a RuntimeManager; ColorModule.ResolveActive()
// is internal to the Molca runtime assembly, so the legacy static shims are used here.
#pragma warning disable CS0618

namespace Molca.ColorID.Editor
{
    [CustomEditor(typeof(ColorID))]
    [CanEditMultipleObjects]
    public class ColorIDEditor : UnityEditor.Editor
    {
        private const float ColorPreviewWidth = 30f;
        private const float ColorPreviewSpacing = 2f;

        private ColorID[] colorIDs;
        private string[] availableColorIds;
        private bool hasMultipleTargets;

        private void OnEnable()
        {
            hasMultipleTargets = targets.Length > 1;
            colorIDs = targets.Cast<ColorID>().ToArray();
            RefreshColorIds();
            
            // Don't validate on load - this was causing crashes
            // Validation will happen naturally when the inspector is drawn
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Show multi-edit indicator
            if (hasMultipleTargets)
            {
                EditorGUILayout.HelpBox($"Editing {targets.Length} ColorID components", MessageType.Info);
                EditorGUILayout.Space(5);
            }
            
            // Show validation warning if color is invalid (but don't auto-fix)
            DrawValidationWarning();

            // Color ID dropdown
            DrawColorIdDropdown();

            // Apply to children toggle
            DrawApplyToChildrenToggle();

            // Advanced settings
            var colorTargetsProperty = serializedObject.FindProperty("_colorTargets");
            if (colorTargetsProperty != null)
            {
                EditorGUILayout.PropertyField(colorTargetsProperty, true);
            }

            // Refresh button
            if (GUILayout.Button("Refresh"))
            {
                RefreshAllTargets();
            }

            serializedObject.ApplyModifiedProperties();
        }
        
        private void DrawValidationWarning()
        {
            string currentSwatchName = GetCurrentSwatchName();
            string currentColorId = GetCurrentColorId();
            
            if (string.IsNullOrEmpty(currentSwatchName))
            {
                EditorGUILayout.HelpBox("Swatch name is empty. Using 'Default'.", MessageType.Info);
                return;
            }
            
            if (!string.IsNullOrEmpty(currentColorId))
            {
                try
                {
                    if (!ColorModule.HasColor(currentSwatchName, currentColorId))
                    {
                        EditorGUILayout.HelpBox($"Color ID '{currentColorId}' not found in swatch '{currentSwatchName}'. Please select a valid color from the dropdown.", MessageType.Warning);
                    }
                }
                catch (System.Exception)
                {
                    // ColorModule not ready
                    EditorGUILayout.HelpBox("Color system is initializing...", MessageType.Info);
                }
            }
        }

        private void DrawColorIdDropdown()
        {
            string currentSwatchName = GetCurrentSwatchName();
            string currentColorId = GetCurrentColorId();
            string currentComposite = $"{currentSwatchName}/{currentColorId}";
            bool hasMixedValues = HasMixedColorIds();
            
            // Find the current color in the grouped format
            int selectedIndex = -1;
            for (int i = 0; i < availableColorIds.Length; i++)
            {
                if (availableColorIds[i] == currentComposite)
                {
                    selectedIndex = i;
                    break;
                }
            }
            
            if (selectedIndex < 0) selectedIndex = 0;
            
            // Show mixed value indicator
            string label = hasMixedValues ? "Color ID (Mixed Values)" : "Color ID";

            // Use a single control rect (like ColorIDReferenceDrawer) for correct alignment and full-width layout
            float lineHeight = EditorGUIUtility.singleLineHeight;
            float labelWidth = EditorGUIUtility.labelWidth;
            Rect rowRect = EditorGUILayout.GetControlRect(true, lineHeight);
            float remainingWidth = rowRect.width - labelWidth;
            float dropdownWidth = remainingWidth - ColorPreviewWidth - ColorPreviewSpacing;

            Rect labelRect = new Rect(rowRect.x, rowRect.y, labelWidth, lineHeight);
            Rect dropdownRect = new Rect(rowRect.x + labelWidth, rowRect.y, dropdownWidth, lineHeight);
            Rect previewRect = new Rect(rowRect.x + labelWidth + dropdownWidth + ColorPreviewSpacing, rowRect.y, ColorPreviewWidth, lineHeight);

            EditorGUI.LabelField(labelRect, label);
            int newIndex = EditorGUI.Popup(dropdownRect, selectedIndex, availableColorIds);
            DrawColorPreviewInline(previewRect, currentSwatchName, currentColorId);

            if (newIndex != selectedIndex && newIndex >= 0 && newIndex < availableColorIds.Length)
            {
                string newColorIdGrouped = availableColorIds[newIndex];
                // Extract both swatch name and color ID
                string newSwatchName = "Default";
                string newColorId = newColorIdGrouped;
                
                if (newColorIdGrouped.Contains("/"))
                {
                    int slashIndex = newColorIdGrouped.LastIndexOf('/');
                    newSwatchName = newColorIdGrouped.Substring(0, slashIndex);
                    newColorId = newColorIdGrouped.Substring(slashIndex + 1);
                }
                
                SetColor(newSwatchName, newColorId);
            }
        }

        private void DrawApplyToChildrenToggle()
        {
            bool currentValue = GetApplyToChildren();
            bool hasMixedValues = HasMixedApplyToChildren();
            
            // Show mixed value indicator
            string label = hasMixedValues ? "Apply to Children (Mixed Values)" : "Apply to Children";
            
            bool newValue = EditorGUILayout.Toggle(label, currentValue);
            if (newValue != currentValue)
            {
                SetApplyToChildren(newValue);
            }
        }

        private void DrawColorPreviewInline(Rect position, string swatchName, string colorId)
        {
            Color color;
            try
            {
                if (string.IsNullOrEmpty(colorId) || !ColorModule.HasColor(swatchName, colorId))
                {
                    color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                }
                else
                {
                    color = ColorModule.GetColor(swatchName, colorId);
                }
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

        private string GetCurrentSwatchName()
        {
            if (hasMultipleTargets)
            {
                // For multi-edit, return the first value or check if all are the same
                var swatchNameProperty = serializedObject.FindProperty("_swatchName");
                if (swatchNameProperty != null)
                {
                    return swatchNameProperty.stringValue;
                }
            }
            else
            {
                var swatchNameProperty = serializedObject.FindProperty("_swatchName");
                return swatchNameProperty != null ? swatchNameProperty.stringValue : "Default";
            }
            
            return "Default";
        }

        private string GetCurrentColorId()
        {
            if (hasMultipleTargets)
            {
                // For multi-edit, return the first value or check if all are the same
                var colorIdProperty = serializedObject.FindProperty("_colorId");
                if (colorIdProperty != null)
                {
                    return colorIdProperty.stringValue;
                }
            }
            else
            {
                var colorIdProperty = serializedObject.FindProperty("_colorId");
                return colorIdProperty != null ? colorIdProperty.stringValue : "Primary";
            }
            
            return "Primary";
        }

        private bool HasMixedColorIds()
        {
            if (!hasMultipleTargets) return false;
            
            string firstComposite = null;
            foreach (var target in targets)
            {
                var serializedTarget = new SerializedObject(target);
                var swatchNameProperty = serializedTarget.FindProperty("_swatchName");
                var colorIdProperty = serializedTarget.FindProperty("_colorId");
                
                string swatchName = swatchNameProperty != null ? swatchNameProperty.stringValue : "Default";
                string colorId = colorIdProperty != null ? colorIdProperty.stringValue : "Primary";
                string currentComposite = $"{swatchName}/{colorId}";
                
                if (firstComposite == null)
                {
                    firstComposite = currentComposite;
                }
                else if (firstComposite != currentComposite)
                {
                    return true;
                }
            }
            return false;
        }

        private bool HasMixedApplyToChildren()
        {
            if (!hasMultipleTargets) return false;
            
            bool? firstValue = null;
            foreach (var target in targets)
            {
                var serializedTarget = new SerializedObject(target);
                var applyToChildrenProperty = serializedTarget.FindProperty("_applyToChildren");
                bool currentValue = applyToChildrenProperty != null ? applyToChildrenProperty.boolValue : true;
                
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

        private void SetColor(string swatchName, string colorId)
        {
            if (hasMultipleTargets)
            {
                // Apply to all targets
                foreach (var target in targets)
                {
                    var serializedTarget = new SerializedObject(target);
                    var swatchNameProperty = serializedTarget.FindProperty("_swatchName");
                    var colorIdProperty = serializedTarget.FindProperty("_colorId");
                    
                    if (swatchNameProperty != null)
                    {
                        swatchNameProperty.stringValue = swatchName;
                    }
                    
                    if (colorIdProperty != null)
                    {
                        colorIdProperty.stringValue = colorId;
                    }
                    
                    serializedTarget.ApplyModifiedProperties();
                    
                    var colorID = target as ColorID;
                    if (colorID != null)
                    {
                        EditorApplication.delayCall += () =>
                        {
                            if (colorID != null)
                            {
                                colorID.SetColor(swatchName, colorId);
                                EditorUtility.SetDirty(target);
                            }
                        };
                    }
                }
            }
            else
            {
                var swatchNameProperty = serializedObject.FindProperty("_swatchName");
                var colorIdProperty = serializedObject.FindProperty("_colorId");
                
                if (swatchNameProperty != null)
                {
                    swatchNameProperty.stringValue = swatchName;
                }
                
                if (colorIdProperty != null)
                {
                    colorIdProperty.stringValue = colorId;
                }
                
                serializedObject.ApplyModifiedProperties();
                
                EditorApplication.delayCall += () =>
                {
                    if (colorIDs != null && colorIDs.Length > 0 && colorIDs[0] != null)
                    {
                        colorIDs[0].SetColor(swatchName, colorId);
                        EditorUtility.SetDirty(targets[0]);
                    }
                };
            }
        }

        private bool GetApplyToChildren()
        {
            if (hasMultipleTargets)
            {
                // For multi-edit, return the first value
                var applyToChildrenProperty = serializedObject.FindProperty("_applyToChildren");
                return applyToChildrenProperty != null ? applyToChildrenProperty.boolValue : true;
            }
            else
            {
                var applyToChildrenProperty = serializedObject.FindProperty("_applyToChildren");
                return applyToChildrenProperty != null ? applyToChildrenProperty.boolValue : true;
            }
        }

        private void SetApplyToChildren(bool value)
        {
            if (hasMultipleTargets)
            {
                // Apply to all targets
                foreach (var target in targets)
                {
                    var serializedTarget = new SerializedObject(target);
                    var applyToChildrenProperty = serializedTarget.FindProperty("_applyToChildren");
                    if (applyToChildrenProperty != null)
                    {
                        applyToChildrenProperty.boolValue = value;
                        serializedTarget.ApplyModifiedProperties();
                    }
                }
            }
            else
            {
                var applyToChildrenProperty = serializedObject.FindProperty("_applyToChildren");
                if (applyToChildrenProperty != null)
                {
                    applyToChildrenProperty.boolValue = value;
                    serializedObject.ApplyModifiedProperties();
                }
            }
        }

        private void RefreshAllTargets()
        {
            if (hasMultipleTargets)
            {
                // Refresh all targets
                foreach (var target in targets)
                {
                    var colorID = target as ColorID;
                    if (colorID != null)
                    {
                        colorID.Refresh();
                        EditorUtility.SetDirty(target);
                    }
                }
            }
            else
            {
                if (colorIDs != null && colorIDs.Length > 0 && colorIDs[0] != null)
                {
                    colorIDs[0].Refresh();
                    EditorUtility.SetDirty(targets[0]);
                }
            }
        }

        private void RefreshColorIds()
        {
            try
            {
                string[] swatchNames = ColorModule.GetSwatchNames();
                var colorIdsList = new System.Collections.Generic.List<string>();
                
                // Build display options with swatch grouping
                foreach (string swatchName in swatchNames)
                {
                    string[] colorIds = ColorModule.GetColorIdsInSwatch(swatchName);
                    foreach (string colorId in colorIds)
                    {
                        colorIdsList.Add($"{swatchName}/{colorId}");
                    }
                }
                
                availableColorIds = colorIdsList.ToArray();
                
                if (availableColorIds == null || availableColorIds.Length == 0)
                {
                    availableColorIds = new string[] { "Default/Primary", "Default/Secondary", "Default/Accent", "Default/Success", "Default/Warning", "Default/Error", "Default/Text", "Default/Background", "Default/Disabled" };
                }
            }
            catch (System.Exception)
            {
                // ColorModule not ready - use defaults
                availableColorIds = new string[] { "Default/Primary", "Default/Secondary", "Default/Accent", "Default/Success", "Default/Warning", "Default/Error", "Default/Text", "Default/Background", "Default/Disabled" };
            }
        }
    }
} 