using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Molca.Editor.Migration;
using Molca.Localization;
using UnityEditor;

namespace Molca.Editor
{
    /// <summary>
    /// One prefab instance's overrides of a legacy <c>LocalizedValue</c>, and what migration does with them.
    /// </summary>
    /// <remarks>
    /// The quieter half of the same defect <c>ColorContentMigration</c> already refused: migrating the
    /// source rewrites <c>translations</c> into <c>inlineSource.values</c> and then empties the legacy
    /// array, so an instance overriding a row's text keeps overriding a field nothing reads. Unlike the
    /// colour case there is no missing component to make it obvious — the migration reports success, and
    /// the loss surfaces later as a panel rendering the wrong string.
    /// </remarks>
    public sealed class LocalizedValueInstanceOverride
    {
        /// <summary>The prefab or scene holding the overriding instance.</summary>
        public string ContainingAssetPath { get; }

        /// <summary>The <c>PrefabInstance</c>'s local file id inside that asset.</summary>
        public long InstanceFileId { get; }

        /// <summary>The legacy property paths this instance overrides.</summary>
        public IReadOnlyList<string> LegacyPropertyPaths { get; }

        /// <summary>The schema-v2 modifications that would replace them.</summary>
        public IReadOnlyList<(string PropertyPath, string Value)> Translated { get; }

        /// <summary>Why the override cannot be carried, or <c>null</c>.</summary>
        public string Refusal { get; }

        /// <summary>Whether migration can carry this override onto the new schema.</summary>
        public bool CanBeCarried => string.IsNullOrEmpty(Refusal);

        internal LocalizedValueInstanceOverride(string containingAssetPath, long instanceFileId,
            IReadOnlyList<string> legacyPropertyPaths,
            IReadOnlyList<(string PropertyPath, string Value)> translated, string refusal)
        {
            ContainingAssetPath = containingAssetPath;
            InstanceFileId = instanceFileId;
            LegacyPropertyPaths = legacyPropertyPaths;
            Translated = translated;
            Refusal = refusal;
        }

        /// <inheritdoc/>
        public override string ToString() => CanBeCarried
            ? $"{ContainingAssetPath}: {Translated.Count} modification(s) carried"
            : $"{ContainingAssetPath}: {Refusal}";
    }

    /// <summary>
    /// Finds and translates the instance overrides a <c>LocalizedValue</c> migration would orphan.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Localization/</c>.
    /// <b>Shape:</b> editor-only, built once per inventory. Read-only; the write goes through
    /// <see cref="PrefabInstanceOverrideWriter"/>.
    /// <para/>
    /// <b>The translation is total, which is why it is a translation and not a refusal.</b> Every legacy
    /// field has exactly one schema-v2 counterpart, at the same array index, because
    /// <c>LocalizedValueSerializedUtility.MigrateLegacy</c> copies row <c>i</c> to row <c>i</c>. That is
    /// a stronger position than the colour migration is in, where a legacy pair may have no authored
    /// alias at all — here the only refusals are structural: an unwritable container, or a legacy path
    /// with no rule, which would mean the schema grew a field this mapping does not know about.
    /// </remarks>
    public sealed class LocalizedValueInstanceOverrideDetector
    {
        /// <summary>Legacy field names an instance can override, as serialized.</summary>
        private static readonly string[] LegacyRoots = { "translations", "useLocalizedString", "localizedString" };

        private readonly PrefabInstanceOverrideSnapshot _snapshot;

        private LocalizedValueInstanceOverrideDetector(PrefabInstanceOverrideSnapshot snapshot) =>
            _snapshot = snapshot;

        /// <summary>Assets the index could not read.</summary>
        public IReadOnlyList<string> UnreadableAssets => _snapshot.UnreadableAssets;

        /// <summary>Scans the project once for overrides of legacy localized values.</summary>
        /// <returns>The detector; never <c>null</c>.</returns>
        public static LocalizedValueInstanceOverrideDetector Build() =>
            new LocalizedValueInstanceOverrideDetector(
                PrefabInstanceOverrideIndex.Scan(MentionsLegacyField, LegacyRoots));

        /// <summary>Whether a serialized path reaches into one of the legacy fields.</summary>
        private static bool MentionsLegacyField(string propertyPath)
        {
            foreach (string root in LegacyRoots)
            {
                if (string.Equals(propertyPath, root, StringComparison.Ordinal)) return true;
                if (propertyPath.StartsWith(root + ".", StringComparison.Ordinal)) return true;
                if (propertyPath.EndsWith("." + root, StringComparison.Ordinal)) return true;
                if (propertyPath.IndexOf("." + root + ".", StringComparison.Ordinal) >= 0) return true;
            }

            return false;
        }

        /// <summary>
        /// The overrides that reach into one legacy value, translated onto schema v2 where possible.
        /// </summary>
        /// <param name="target">The object carrying the legacy value.</param>
        /// <param name="valuePropertyPath">The <c>LocalizedValue</c> field's serialized path.</param>
        /// <returns>One entry per overriding instance; empty when nothing overrides it.</returns>
        public IReadOnlyList<LocalizedValueInstanceOverride> Resolve(UnityEngine.Object target,
            string valuePropertyPath)
        {
            var results = new List<LocalizedValueInstanceOverride>();
            if (target == null || string.IsNullOrEmpty(valuePropertyPath)) return results;

            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out string guid, out long fileId))
                return results;

            // Scoped to this one field. Two localized values on the same component are migrated
            // independently, and an override of one says nothing about the other.
            string prefix = valuePropertyPath + ".";
            var relevant = _snapshot.ForObject(guid, fileId)
                .Where(o => o.PropertyPath.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            if (relevant.Count == 0) return results;

            foreach (var instance in relevant
                         .GroupBy(o => (o.ContainingAssetPath, o.InstanceFileId))
                         .OrderBy(g => g.Key.ContainingAssetPath, StringComparer.Ordinal))
            {
                results.Add(Translate(instance.Key.ContainingAssetPath, instance.Key.InstanceFileId,
                    valuePropertyPath, instance.ToList()));
            }

            return results;
        }

        private static LocalizedValueInstanceOverride Translate(string containingAssetPath,
            long instanceFileId, string valuePropertyPath,
            IReadOnlyList<PrefabInstanceOverride> modifications)
        {
            var legacyPaths = modifications.Select(m => m.PropertyPath).ToList();

            if (!IsWritable(containingAssetPath))
            {
                return new LocalizedValueInstanceOverride(containingAssetPath, instanceFileId, legacyPaths,
                    Array.Empty<(string, string)>(),
                    $"'{containingAssetPath}' is not writable, so its override cannot be carried onto the "
                    + "new schema; migrate it in the package that owns it");
            }

            var translated = new List<(string PropertyPath, string Value)>();
            var unmapped = new List<string>();

            foreach (var modification in modifications)
            {
                string tail = modification.PropertyPath.Substring(valuePropertyPath.Length + 1);
                if (TryTranslate(tail, modification.Value, out string newTail, out string newValue))
                    translated.Add(($"{valuePropertyPath}.{newTail}", newValue));
                else
                    unmapped.Add(modification.PropertyPath);
            }

            if (unmapped.Count > 0)
            {
                return new LocalizedValueInstanceOverride(containingAssetPath, instanceFileId, legacyPaths,
                    Array.Empty<(string, string)>(),
                    "no schema-v2 counterpart is defined for "
                    + string.Join(", ", unmapped)
                    + "; the legacy schema has a field this migration does not know how to carry");
            }

            return new LocalizedValueInstanceOverride(containingAssetPath, instanceFileId, legacyPaths,
                translated, null);
        }

        /// <summary>
        /// Maps one legacy serialized path and value onto its schema-v2 counterpart.
        /// </summary>
        /// <remarks>
        /// Mirrors <c>LocalizedValueSerializedUtility.MigrateLegacy</c> field for field. If that method
        /// ever changes where it puts something, this must change with it — which is why the pairing is
        /// spelled out here rather than inferred, and why a path with no rule is a refusal rather than a
        /// best guess.
        /// </remarks>
        private static bool TryTranslate(string legacyTail, string value, out string newTail,
            out string newValue)
        {
            newTail = null;
            newValue = value;

            // useLocalizedString was the schema-v1 way of saying "this reads from the catalog". The v2
            // schema says it with an enum, so the bool becomes the matching member's numeric value.
            if (string.Equals(legacyTail, "useLocalizedString", StringComparison.Ordinal))
            {
                bool usesCatalog = value == "1"
                                   || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                newTail = "sourceKind";
                newValue = ((int)(usesCatalog
                    ? LocalizedValueSourceKind.Catalog
                    : LocalizedValueSourceKind.Inline)).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (legacyTail.StartsWith("localizedString.", StringComparison.Ordinal))
            {
                newTail = "catalogSource.reference." + legacyTail.Substring("localizedString.".Length);
                return true;
            }

            if (string.Equals(legacyTail, "translations.Array.size", StringComparison.Ordinal))
            {
                newTail = "inlineSource.values.Array.size";
                return true;
            }

            if (legacyTail.StartsWith("translations.Array.data[", StringComparison.Ordinal))
            {
                int close = legacyTail.IndexOf("].", StringComparison.Ordinal);
                if (close < 0) return false;

                string index = legacyTail.Substring("translations.Array.data[".Length,
                    close - "translations.Array.data[".Length);
                string field = legacyTail.Substring(close + 2);

                // Row i migrates to row i, so the index carries across unchanged.
                string mapped = field switch
                {
                    "languageCode" => "localeCode",
                    "text" => "value",
                    _ => null,
                };

                if (mapped == null) return false;

                newTail = $"inlineSource.values.Array.data[{index}].{mapped}";
                return true;
            }

            return false;
        }

        private static bool IsWritable(string assetPath) =>
            assetPath.StartsWith("Assets/", StringComparison.Ordinal)
            && AssetDatabase.IsOpenForEdit(assetPath);
    }
}
