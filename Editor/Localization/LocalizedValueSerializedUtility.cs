using Molca.Localization;
using UnityEditor;
using UnityEngine.Localization;

namespace Molca.Editor
{
    /// <summary>
    /// One schema-aware view over legacy DynamicLocalization and schema-v2 LocalizedValue fields.
    /// Keeping this logic shared prevents Inspector, audit, migration, and MCP from drifting.
    /// </summary>
    internal static class LocalizedValueSerializedUtility
    {
        internal static bool TryDescribe(
            SerializedProperty property,
            out LocalizedValueSerializedDescriptor descriptor)
        {
            descriptor = default;
            if (property == null || property.propertyType != SerializedPropertyType.Generic)
                return false;

            var disabled = property.FindPropertyRelative("disabled");
            var schema = property.FindPropertyRelative("schemaVersion");
            var sourceKind = property.FindPropertyRelative("sourceKind");
            var useCatalog = property.FindPropertyRelative("useLocalizedString");
            var legacyRows = property.FindPropertyRelative("translations");
            if (disabled == null || schema == null || sourceKind == null ||
                useCatalog == null || legacyRows == null || !legacyRows.isArray)
                return false;

            var legacyReference = property.FindPropertyRelative("localizedString");
            var hasLegacyReference = legacyReference != null && !IsEmptyLocalizedString(legacyReference);
            var isLegacy = schema.intValue < LocalizedValue.CurrentSchemaVersion ||
                           useCatalog.boolValue ||
                           hasLegacyReference ||
                           legacyRows.arraySize > 0;

            if (isLegacy)
            {
                descriptor = new LocalizedValueSerializedDescriptor(
                    property,
                    disabled,
                    schema,
                    useCatalog.boolValue
                        ? LocalizedValueSourceKind.Catalog
                        : LocalizedValueSourceKind.Inline,
                    true,
                    legacyRows,
                    "languageCode",
                    "text",
                    legacyReference);
                return true;
            }

            var kind = (LocalizedValueSourceKind)sourceKind.enumValueIndex;
            var inlineRows = property.FindPropertyRelative("inlineSource")
                ?.FindPropertyRelative("values");
            var catalogReference = property.FindPropertyRelative("catalogSource")
                ?.FindPropertyRelative("reference");
            descriptor = new LocalizedValueSerializedDescriptor(
                property,
                disabled,
                schema,
                kind,
                false,
                inlineRows,
                "localeCode",
                "value",
                catalogReference);
            return true;
        }

        internal static void MigrateLegacy(SerializedProperty property)
        {
            if (!TryDescribe(property, out var descriptor) || !descriptor.IsLegacy)
                return;

            property.FindPropertyRelative("schemaVersion").intValue =
                LocalizedValue.CurrentSchemaVersion;
            property.FindPropertyRelative("sourceKind").enumValueIndex =
                (int)descriptor.SourceKind;

            if (descriptor.SourceKind == LocalizedValueSourceKind.Catalog)
            {
                var destination = property.FindPropertyRelative("catalogSource")
                    ?.FindPropertyRelative("reference");
                if (destination != null && descriptor.CatalogReference != null)
                    destination.boxedValue = descriptor.CatalogReference.boxedValue;
            }
            else
            {
                var destination = property.FindPropertyRelative("inlineSource")
                    ?.FindPropertyRelative("values");
                if (destination != null)
                {
                    destination.arraySize = descriptor.Rows?.arraySize ?? 0;
                    for (var index = 0; index < destination.arraySize; index++)
                    {
                        var sourceRow = descriptor.Rows.GetArrayElementAtIndex(index);
                        var destinationRow = destination.GetArrayElementAtIndex(index);
                        destinationRow.FindPropertyRelative("localeCode").stringValue =
                            sourceRow.FindPropertyRelative("languageCode")?.stringValue ??
                            string.Empty;
                        destinationRow.FindPropertyRelative("value").stringValue =
                            sourceRow.FindPropertyRelative("text")?.stringValue ??
                            string.Empty;
                    }
                }
            }

            property.FindPropertyRelative("useLocalizedString").boolValue = false;
            property.FindPropertyRelative("translations").arraySize = 0;
            var legacyReference = property.FindPropertyRelative("localizedString");
            if (legacyReference != null)
                legacyReference.boxedValue = new UnityEngine.Localization.LocalizedString();
        }

        private static bool IsEmptyLocalizedString(SerializedProperty reference)
        {
            try
            {
                return reference.boxedValue is not LocalizedString value || value.IsEmpty;
            }
            catch
            {
                // If Unity cannot box a package-version-specific LocalizedString shape, retain it
                // as legacy rather than risk classifying authored data as empty.
                return false;
            }
        }
    }

    internal readonly struct LocalizedValueSerializedDescriptor
    {
        internal LocalizedValueSerializedDescriptor(
            SerializedProperty property,
            SerializedProperty disabled,
            SerializedProperty schemaVersion,
            LocalizedValueSourceKind sourceKind,
            bool isLegacy,
            SerializedProperty rows,
            string codeField,
            string valueField,
            SerializedProperty catalogReference)
        {
            Property = property;
            Disabled = disabled;
            SchemaVersion = schemaVersion;
            SourceKind = sourceKind;
            IsLegacy = isLegacy;
            Rows = rows;
            CodeField = codeField;
            ValueField = valueField;
            CatalogReference = catalogReference;
        }

        internal SerializedProperty Property { get; }
        internal SerializedProperty Disabled { get; }
        internal SerializedProperty SchemaVersion { get; }
        internal LocalizedValueSourceKind SourceKind { get; }
        internal bool IsLegacy { get; }
        internal SerializedProperty Rows { get; }
        internal string CodeField { get; }
        internal string ValueField { get; }
        internal SerializedProperty CatalogReference { get; }
    }
}
