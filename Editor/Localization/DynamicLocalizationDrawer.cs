using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Localization;
using Molca.Settings;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Non-destructive drawer for schema-v2 <see cref="LocalizedValue"/> and its
    /// <see cref="DynamicLocalization"/> compatibility subclass.
    /// </summary>
    [CustomPropertyDrawer(typeof(LocalizedValue), true)]
    public class DynamicLocalizationDrawer : PropertyDrawer
    {
        private const float MessageHeight = 40f;
        private static float LineHeight => EditorGUIUtility.singleLineHeight;
        private static float Spacing => EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var foldoutKey = "LocalizedValue_" + property.propertyPath;
            var expanded = EditorGUI.Foldout(
                new Rect(position.x, position.y, position.width, LineHeight),
                MolcaEditorPrefs.GetBool(foldoutKey, true),
                label,
                true);
            MolcaEditorPrefs.SetBool(foldoutKey, expanded);
            if (!expanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            var disabled = property.FindPropertyRelative("disabled");
            var schemaVersion = property.FindPropertyRelative("schemaVersion");
            var legacy = IsLegacy(property);
            var y = position.y + LineHeight + Spacing;
            disabled.boolValue = EditorGUI.ToggleLeft(
                new Rect(position.x, y, position.width, LineHeight),
                "Disabled",
                disabled.boolValue);
            y += LineHeight + Spacing;

            if (legacy)
            {
                EditorGUI.HelpBox(
                    new Rect(position.x, y, position.width, MessageHeight),
                    $"Legacy schema v{schemaVersion?.intValue ?? 1} payload. It remains readable; " +
                    "use Localization Hub migration preview to convert without data loss.",
                    MessageType.Warning);
                y += MessageHeight + Spacing;
                if (!disabled.boolValue)
                    DrawLegacy(position, ref y, property);
            }
            else if (!disabled.boolValue)
            {
                DrawExplicit(position, ref y, property);
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var foldoutKey = "LocalizedValue_" + property.propertyPath;
            if (!MolcaEditorPrefs.GetBool(foldoutKey, true))
                return LineHeight;

            var height = LineHeight + Spacing + LineHeight + Spacing;
            if (property.FindPropertyRelative("disabled")?.boolValue == true)
                return height;
            if (IsLegacy(property))
                return height + MessageHeight + Spacing + GetLegacyContentHeight(property);
            return height + GetExplicitContentHeight(property);
        }

        private static bool IsLegacy(SerializedProperty property)
        {
            return !LocalizedValueSerializedUtility.TryDescribe(property, out var descriptor) ||
                   descriptor.IsLegacy;
        }

        private static void DrawExplicit(
            Rect position,
            ref float y,
            SerializedProperty property)
        {
            var sourceKind = property.FindPropertyRelative("sourceKind");
            EditorGUI.PropertyField(
                new Rect(position.x, y, position.width, LineHeight),
                sourceKind,
                new GUIContent("Source"));
            y += LineHeight + Spacing;

            if ((LocalizedValueSourceKind)sourceKind.enumValueIndex ==
                LocalizedValueSourceKind.Catalog)
            {
                var reference = property
                    .FindPropertyRelative("catalogSource")
                    ?.FindPropertyRelative("reference");
                var height = EditorGUI.GetPropertyHeight(reference, true);
                EditorGUI.PropertyField(
                    new Rect(position.x, y, position.width, height),
                    reference,
                    true);
                return;
            }

            if ((LocalizedValueSourceKind)sourceKind.enumValueIndex ==
                LocalizedValueSourceKind.Inline)
            {
                var values = property
                    .FindPropertyRelative("inlineSource")
                    ?.FindPropertyRelative("values");
                DrawInlineValues(
                    position,
                    ref y,
                    property,
                    values,
                    "localeCode",
                    "value");
                return;
            }

            EditorGUI.HelpBox(
                new Rect(position.x, y, position.width, MessageHeight),
                "Choose Catalog or Inline. None resolves to an empty value.",
                MessageType.Info);
        }

        private static void DrawLegacy(
            Rect position,
            ref float y,
            SerializedProperty property)
        {
            var useCatalog = property.FindPropertyRelative("useLocalizedString");
            useCatalog.boolValue = EditorGUI.ToggleLeft(
                new Rect(position.x, y, position.width, LineHeight),
                "Use legacy LocalizedString",
                useCatalog.boolValue);
            y += LineHeight + Spacing;
            if (useCatalog.boolValue)
            {
                var reference = property.FindPropertyRelative("localizedString");
                var height = EditorGUI.GetPropertyHeight(reference, true);
                EditorGUI.PropertyField(
                    new Rect(position.x, y, position.width, height),
                    reference,
                    true);
                return;
            }

            DrawInlineValues(
                position,
                ref y,
                property,
                property.FindPropertyRelative("translations"),
                "languageCode",
                "text");
        }

        private static void DrawInlineValues(
            Rect position,
            ref float y,
            SerializedProperty owner,
            SerializedProperty rows,
            string codeField,
            string valueField)
        {
            var module = GetLocalizationModule();
            if (module == null)
            {
                EditorGUI.HelpBox(
                    new Rect(position.x, y, position.width, MessageHeight),
                    "LocalizationModule not found in GlobalSettings.",
                    MessageType.Error);
                return;
            }

            var configuredCodes = module.LanguageCode
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (configuredCodes.Length == 0)
            {
                EditorGUI.HelpBox(
                    new Rect(position.x, y, position.width, MessageHeight),
                    "No valid locale codes are configured.",
                    MessageType.Warning);
                return;
            }

            var diagnostics = LocalizationAuthoringUtility.Analyze(
                rows,
                configuredCodes,
                codeField);
            if (diagnostics.HasFindings)
            {
                EditorGUI.HelpBox(
                    new Rect(position.x, y, position.width, MessageHeight),
                    diagnostics.Message,
                    diagnostics.HasInvalidOrDuplicateCodes
                        ? MessageType.Warning
                        : MessageType.Info);
                y += MessageHeight + Spacing;
            }

            if (diagnostics.MissingCodes.Count > 0 &&
                !owner.serializedObject.isEditingMultipleObjects)
            {
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                {
                    if (GUI.Button(
                            new Rect(position.x, y, position.width, LineHeight),
                            $"Add Missing Locales ({diagnostics.MissingCodes.Count})"))
                        LocalizationAuthoringUtility.AddMissing(
                            rows,
                            diagnostics.MissingCodes,
                            codeField,
                            valueField);
                }
                y += LineHeight + Spacing;
            }

            var boxHeight = Math.Max(1, rows.arraySize) * (LineHeight + Spacing) + Spacing;
            GUI.Box(new Rect(position.x, y, position.width, boxHeight), GUIContent.none);
            y += Spacing;
            if (rows.arraySize == 0)
            {
                EditorGUI.LabelField(
                    new Rect(position.x + 5f, y, position.width - 10f, LineHeight),
                    "No inline values authored.");
                return;
            }

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                for (var index = 0; index < rows.arraySize; index++)
                {
                    var entry = rows.GetArrayElementAtIndex(index);
                    var code = entry.FindPropertyRelative(codeField);
                    var value = entry.FindPropertyRelative(valueField);
                    var known = configuredCodes.Contains(
                        code.stringValue,
                        StringComparer.OrdinalIgnoreCase);
                    var duplicate = diagnostics.DuplicateCodes.Contains(
                        code.stringValue,
                        StringComparer.OrdinalIgnoreCase);
                    var previousColor = GUI.color;
                    if (!known || duplicate)
                        GUI.color = new Color(1f, 0.72f, 0.45f);
                    EditorGUI.PropertyField(
                        new Rect(position.x + 5f, y, 80f, LineHeight),
                        code,
                        GUIContent.none);
                    GUI.color = previousColor;
                    EditorGUI.PropertyField(
                        new Rect(position.x + 90f, y, position.width - 95f, LineHeight),
                        value,
                        GUIContent.none);
                    y += LineHeight + Spacing;
                }
            }
        }

        private static float GetExplicitContentHeight(SerializedProperty property)
        {
            var sourceKind = property.FindPropertyRelative("sourceKind");
            var height = LineHeight + Spacing;
            if ((LocalizedValueSourceKind)sourceKind.enumValueIndex ==
                LocalizedValueSourceKind.Catalog)
            {
                var reference = property
                    .FindPropertyRelative("catalogSource")
                    ?.FindPropertyRelative("reference");
                return height + EditorGUI.GetPropertyHeight(reference, true);
            }
            if ((LocalizedValueSourceKind)sourceKind.enumValueIndex !=
                LocalizedValueSourceKind.Inline)
                return height + MessageHeight;
            return height + GetInlineHeight(
                property,
                property.FindPropertyRelative("inlineSource")?.FindPropertyRelative("values"),
                "localeCode");
        }

        private static float GetLegacyContentHeight(SerializedProperty property)
        {
            var useCatalog = property.FindPropertyRelative("useLocalizedString");
            var height = LineHeight + Spacing;
            if (useCatalog?.boolValue == true)
                return height + EditorGUI.GetPropertyHeight(
                    property.FindPropertyRelative("localizedString"),
                    true);
            return height + GetInlineHeight(
                property,
                property.FindPropertyRelative("translations"),
                "languageCode");
        }

        private static float GetInlineHeight(
            SerializedProperty owner,
            SerializedProperty rows,
            string codeField)
        {
            var module = GetLocalizationModule();
            if (module == null || module.LanguageCode.Length == 0)
                return MessageHeight;
            var codes = module.LanguageCode
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var diagnostics = LocalizationAuthoringUtility.Analyze(rows, codes, codeField);
            var height = Math.Max(1, rows.arraySize) * (LineHeight + Spacing) + Spacing * 2;
            if (diagnostics.HasFindings)
                height += MessageHeight + Spacing;
            if (diagnostics.MissingCodes.Count > 0 &&
                !owner.serializedObject.isEditingMultipleObjects)
                height += LineHeight + Spacing;
            return height;
        }

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
    }

    /// <summary>Shared non-destructive diagnostics for v1 and v2 inline rows.</summary>
    internal static class LocalizationAuthoringUtility
    {
        internal static LocalizationAuthoringDiagnostics Analyze(
            SerializedProperty rows,
            IReadOnlyCollection<string> configuredCodes,
            string codeField = "languageCode")
        {
            var rowCodes = new List<string>();
            for (var index = 0; index < rows.arraySize; index++)
                rowCodes.Add(rows.GetArrayElementAtIndex(index)
                    .FindPropertyRelative(codeField)?.stringValue ?? string.Empty);

            var missing = configuredCodes
                .Where(configured => !rowCodes.Contains(
                    configured,
                    StringComparer.OrdinalIgnoreCase))
                .ToList();
            var invalid = rowCodes
                .Where(code => string.IsNullOrWhiteSpace(code) ||
                               !configuredCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var duplicates = rowCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            return new LocalizationAuthoringDiagnostics(missing, invalid, duplicates);
        }

        internal static void AddMissing(
            SerializedProperty rows,
            IReadOnlyList<string> missingCodes,
            string codeField = "languageCode",
            string valueField = "text")
        {
            foreach (var code in missingCodes)
            {
                var index = rows.arraySize;
                rows.InsertArrayElementAtIndex(index);
                var entry = rows.GetArrayElementAtIndex(index);
                entry.FindPropertyRelative(codeField).stringValue = code;
                entry.FindPropertyRelative(valueField).stringValue = string.Empty;
            }
        }
    }

    internal sealed class LocalizationAuthoringDiagnostics
    {
        internal LocalizationAuthoringDiagnostics(
            IReadOnlyList<string> missingCodes,
            IReadOnlyList<string> invalidCodes,
            IReadOnlyList<string> duplicateCodes)
        {
            MissingCodes = missingCodes;
            InvalidCodes = invalidCodes;
            DuplicateCodes = duplicateCodes;
        }

        internal IReadOnlyList<string> MissingCodes { get; }
        internal IReadOnlyList<string> InvalidCodes { get; }
        internal IReadOnlyList<string> DuplicateCodes { get; }
        internal bool HasInvalidOrDuplicateCodes =>
            InvalidCodes.Count > 0 || DuplicateCodes.Count > 0;
        internal bool HasFindings =>
            MissingCodes.Count > 0 || HasInvalidOrDuplicateCodes;

        internal string Message
        {
            get
            {
                var parts = new List<string>();
                if (MissingCodes.Count > 0)
                    parts.Add("Missing: " + string.Join(", ", MissingCodes));
                if (InvalidCodes.Count > 0)
                    parts.Add("Unknown/blank: " + string.Join(", ", InvalidCodes));
                if (DuplicateCodes.Count > 0)
                    parts.Add("Duplicates: " + string.Join(", ", DuplicateCodes));
                return string.Join(". ", parts) + ". Existing rows are preserved.";
            }
        }
    }
}
