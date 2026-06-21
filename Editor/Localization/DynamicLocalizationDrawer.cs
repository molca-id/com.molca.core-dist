using UnityEngine;
using UnityEditor;
using Molca.Localization;
using Molca.Settings;

namespace Molca.Editor
{
    [CustomPropertyDrawer(typeof(DynamicLocalization))]
    public class DynamicLocalizationDrawer : PropertyDrawer
    {
        private static LocalizationModule GetLocalizationModule()
        {
            try
            {
                return GlobalSettings.GetModule<LocalizationModule>();
            }
            catch
            {
                return null;
            }
        }

        // Height allocated for the "module not found" error box.
        private const float ErrorBoxHeight = 38f;

        private static float LineH => EditorGUIUtility.singleLineHeight;
        private static float Spacing => EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var disabledProp = property.FindPropertyRelative("disabled");
            var useLocalizedStringProp = property.FindPropertyRelative("useLocalizedString");
            var localizedStringProp = property.FindPropertyRelative("localizedString");
            var translationsProp = property.FindPropertyRelative("translations");

            string foldoutKey = "DynamicLocalization_" + property.propertyPath;
            bool isExpanded = MolcaEditorPrefs.GetBool(foldoutKey, true);

            Rect foldoutRect = new Rect(position.x, position.y, position.width, LineH);
            isExpanded = EditorGUI.Foldout(foldoutRect, isExpanded, label, true);
            MolcaEditorPrefs.SetBool(foldoutKey, isExpanded);

            if (!isExpanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            float toggleY = position.y + LineH + Spacing;
            float toggleWidth = position.width * 0.5f - 5f;

            disabledProp.boolValue = EditorGUI.ToggleLeft(
                new Rect(position.x, toggleY, toggleWidth, LineH),
                "Disabled", disabledProp.boolValue);

            useLocalizedStringProp.boolValue = EditorGUI.ToggleLeft(
                new Rect(position.x + toggleWidth + 10f, toggleY, toggleWidth, LineH),
                "Use LocalizedString", useLocalizedStringProp.boolValue);

            float contentY = toggleY + LineH + Spacing;

            if (!disabledProp.boolValue)
            {
                if (useLocalizedStringProp.boolValue)
                {
                    float lsHeight = EditorGUI.GetPropertyHeight(localizedStringProp, true);
                    EditorGUI.PropertyField(
                        new Rect(position.x, contentY, position.width, lsHeight),
                        localizedStringProp);
                }
                else
                {
                    var localizationModule = GetLocalizationModule();
                    if (localizationModule == null)
                    {
                        EditorGUI.HelpBox(
                            new Rect(position.x, contentY, position.width, ErrorBoxHeight),
                            "LocalizationModule not found in GlobalSettings!", MessageType.Error);
                    }
                    else
                    {
                        var languageCodes = localizationModule.LanguageCode;
                        if (languageCodes == null || languageCodes.Length == 0)
                        {
                            EditorGUI.HelpBox(
                                new Rect(position.x, contentY, position.width, ErrorBoxHeight),
                                "No languages configured in LocalizationModule.", MessageType.Warning);
                        }
                        else
                        {
                            DrawTranslations(position, contentY, property, translationsProp, languageCodes);
                        }
                    }
                }
            }

            EditorGUI.EndProperty();
        }

        private void DrawTranslations(Rect position, float startY, SerializedProperty property,
            SerializedProperty translationsProp, string[] languageCodes)
        {
            bool isMultiEdit = property.serializedObject.isEditingMultipleObjects;
            int targetSize = languageCodes.Length;

            // Resize the list to match the language count, initialising any new elements properly.
            if (!isMultiEdit && translationsProp.arraySize != targetSize)
            {
                // Shrink
                while (translationsProp.arraySize > targetSize)
                    translationsProp.DeleteArrayElementAtIndex(translationsProp.arraySize - 1);

                // Grow — InsertArrayElementAtIndex properly initialises class elements.
                while (translationsProp.arraySize < targetSize)
                {
                    int idx = translationsProp.arraySize;
                    translationsProp.InsertArrayElementAtIndex(idx);
                    var newEl = translationsProp.GetArrayElementAtIndex(idx);
                    newEl.FindPropertyRelative("languageCode").stringValue = languageCodes[idx];
                    newEl.FindPropertyRelative("text").stringValue = string.Empty;
                }
            }

            bool showWarning = isMultiEdit && translationsProp.hasMultipleDifferentValues;
            float innerY = startY + Spacing;

            if (showWarning)
            {
                EditorGUI.HelpBox(
                    new Rect(position.x + 5, innerY, position.width - 10, LineH),
                    "Multiple objects selected — editing will apply to all", MessageType.Info);
                innerY += LineH + Spacing;
            }

            int displayCount = Mathf.Min(translationsProp.arraySize, targetSize);
            float boxHeight = (showWarning ? LineH + Spacing : 0f)
                              + (LineH + Spacing) * displayCount + Spacing;
            GUI.Box(new Rect(position.x, startY, position.width, boxHeight), GUIContent.none);

            bool wasEnabled = GUI.enabled;
            GUI.enabled = !Application.isPlaying;

            for (int i = 0; i < displayCount; i++)
            {
                var entry = translationsProp.GetArrayElementAtIndex(i);
                var langCodeProp = entry.FindPropertyRelative("languageCode");
                var textProp = entry.FindPropertyRelative("text");

                if (langCodeProp == null || textProp == null) continue;

                // Stamp language code if missing (e.g. newly inserted element missed Init above).
                if (!isMultiEdit && string.IsNullOrEmpty(langCodeProp.stringValue))
                    langCodeProp.stringValue = languageCodes[i];

                string displayCode = string.IsNullOrEmpty(langCodeProp.stringValue)
                    ? languageCodes[i] : langCodeProp.stringValue;

                EditorGUI.LabelField(new Rect(position.x + 5, innerY, 60, LineH), displayCode);

                EditorGUI.showMixedValue = textProp.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                string newText = EditorGUI.TextField(
                    new Rect(position.x + 70, innerY, position.width - 75, LineH),
                    textProp.stringValue);
                if (EditorGUI.EndChangeCheck())
                    textProp.stringValue = newText;
                EditorGUI.showMixedValue = false;

                innerY += LineH + Spacing;
            }

            GUI.enabled = wasEnabled;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            string foldoutKey = "DynamicLocalization_" + property.propertyPath;
            bool isExpanded = MolcaEditorPrefs.GetBool(foldoutKey, true);

            if (!isExpanded)
                return LineH;

            var disabledProp = property.FindPropertyRelative("disabled");
            var useLocalizedStringProp = property.FindPropertyRelative("useLocalizedString");
            var localizedStringProp = property.FindPropertyRelative("localizedString");
            var translationsProp = property.FindPropertyRelative("translations");

            // Foldout + toggles
            float height = LineH + Spacing + LineH + Spacing;

            if (!disabledProp.boolValue)
            {
                if (useLocalizedStringProp.boolValue)
                {
                    height += EditorGUI.GetPropertyHeight(localizedStringProp, true);
                }
                else
                {
                    var localizationModule = GetLocalizationModule();
                    if (localizationModule == null)
                    {
                        height += ErrorBoxHeight;
                    }
                    else
                    {
                        var languageCodes = localizationModule.LanguageCode;
                        if (languageCodes == null || languageCodes.Length == 0)
                        {
                            height += ErrorBoxHeight;
                        }
                        else
                        {
                            bool showWarning = property.serializedObject.isEditingMultipleObjects
                                              && translationsProp.hasMultipleDifferentValues;
                            int displayCount = languageCodes.Length;

                            height += Spacing
                                      + (showWarning ? LineH + Spacing : 0f)
                                      + (LineH + Spacing) * displayCount + Spacing;
                        }
                    }
                }
            }

            return height;
        }
    }
}
