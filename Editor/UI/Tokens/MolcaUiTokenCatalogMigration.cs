using System.Collections.Generic;
using System.Text;
using Molca.ColorID;
using Molca.ColorID.Editor;
using Molca.ColorID.Editor.Upgrade;
using Molca.UI.Tokens;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.UI.Tokens
{
    /// <summary>What a migration would do to one catalog colour entry.</summary>
    public sealed class MolcaUiTokenColorMigration
    {
        /// <summary>Index of the entry in the catalog's token list.</summary>
        public int Index { get; }

        /// <summary>The catalog token id, e.g. <c>color/primary</c>.</summary>
        public string CatalogTokenId { get; }

        /// <summary>The legacy V1 swatch name the entry carries.</summary>
        public string SwatchName { get; }

        /// <summary>The legacy V1 colour ID the entry carries.</summary>
        public string ColorId { get; }

        /// <summary>The canonical token an exact alias maps the pair to, or <c>null</c>.</summary>
        public string CanonicalTokenId { get; }

        /// <summary>Variants in which <see cref="CanonicalTokenId"/> does not resolve. Empty when clean.</summary>
        public IReadOnlyList<string> MissingInVariants { get; }

        /// <summary>Whether this entry can be migrated.</summary>
        public bool CanMigrate => CanonicalTokenId != null && MissingInVariants.Count == 0;

        /// <summary>Why it cannot be migrated, or <c>null</c>.</summary>
        public string BlockedReason => CanonicalTokenId == null
            ? $"No legacy alias maps '{SwatchName}.{ColorId}' to a canonical token."
            : MissingInVariants.Count > 0
                ? $"'{CanonicalTokenId}' does not resolve in: {string.Join(", ", MissingInVariants)}."
                : null;

        internal MolcaUiTokenColorMigration(int index, string catalogTokenId, string swatchName,
            string colorId, string canonicalTokenId, List<string> missingInVariants)
        {
            Index = index;
            CatalogTokenId = catalogTokenId;
            SwatchName = swatchName;
            ColorId = colorId;
            CanonicalTokenId = canonicalTokenId;
            MissingInVariants = missingInVariants ?? new List<string>();
        }

        /// <inheritdoc/>
        public override string ToString() => CanMigrate
            ? $"{CatalogTokenId}: {SwatchName}.{ColorId} -> {CanonicalTokenId}"
            : $"{CatalogTokenId}: {SwatchName}.{ColorId} — BLOCKED: {BlockedReason}";
    }

    /// <summary>
    /// A previewed catalog colour migration. Building one changes nothing.
    /// </summary>
    public sealed class MolcaUiTokenCatalogMigrationPlan
    {
        /// <summary>The catalog this plan targets.</summary>
        public MolcaUiTokenCatalog Catalog { get; }

        /// <summary>The theme set whose alias map drives the mapping.</summary>
        public ColorThemeSet ThemeSet { get; }

        /// <summary>Every legacy colour entry found, migratable or not.</summary>
        public IReadOnlyList<MolcaUiTokenColorMigration> Entries { get; }

        /// <summary>
        /// Canonical tokens that more than one catalog entry would map to.
        /// </summary>
        /// <remarks>
        /// Not an error. Two V1 keys aliased to one token is exactly the de-duplication the rebuilt
        /// vocabulary intends, and the catalog ids stay distinct so nothing collides. Reported because it
        /// tells an author which catalog entries have become synonyms and could be collapsed by hand.
        /// </remarks>
        public IReadOnlyDictionary<string, List<string>> Synonyms { get; }

        /// <summary>Reasons the plan cannot be applied at all, or empty.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>Whether the plan can be applied.</summary>
        public bool IsValid => Errors.Count == 0;

        /// <summary>How many entries would be rewritten.</summary>
        public int MigratableCount
        {
            get
            {
                int count = 0;
                foreach (var entry in Entries) if (entry.CanMigrate) count++;
                return count;
            }
        }

        /// <summary>How many entries would be left alone.</summary>
        public int BlockedCount => Entries.Count - MigratableCount;

        internal MolcaUiTokenCatalogMigrationPlan(MolcaUiTokenCatalog catalog, ColorThemeSet themeSet,
            List<MolcaUiTokenColorMigration> entries, Dictionary<string, List<string>> synonyms,
            List<string> errors)
        {
            Catalog = catalog;
            ThemeSet = themeSet;
            Entries = entries ?? new List<MolcaUiTokenColorMigration>();
            Synonyms = synonyms ?? new Dictionary<string, List<string>>();
            Errors = errors ?? new List<string>();
        }

        /// <summary>A human-readable preview.</summary>
        public string ToPreview()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Catalog colour migration: '{Catalog?.name}'");

            if (Errors.Count > 0)
            {
                builder.AppendLine("BLOCKED:");
                foreach (string error in Errors) builder.AppendLine($"  - {error}");
                return builder.ToString();
            }

            builder.AppendLine($"{MigratableCount} entry/entries will be migrated, {BlockedCount} left "
                               + "on the legacy pair.");

            foreach (var entry in Entries) builder.AppendLine($"  {entry}");

            foreach (var pair in Synonyms)
            {
                builder.AppendLine($"  ! '{pair.Key}' is now named by {pair.Value.Count} catalog entries: "
                                   + $"{string.Join(", ", pair.Value)}");
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Migrates a <see cref="MolcaUiTokenCatalog"/>'s colour entries from V1 <c>(swatch, colourId)</c> pairs
    /// onto canonical colour tokens.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/UI/Tokens/</c>.
    /// <b>Shape:</b> editor-only static service. Menu items live on <c>Molca ▸ ColorID</c>.
    /// <para/>
    /// <b>Only an exact alias migrates an entry.</b> The mapping comes from
    /// <see cref="ColorThemeSet.ResolveLegacyToken"/>, which consults the authored alias map and nothing
    /// else. The runtime adapter has two further fallbacks — treating the pair as a canonical id, and
    /// matching on a token's last segment when exactly one candidate exists — and both are deliberately
    /// <i>not</i> used here. They are reasonable guesses to keep a shipped component rendering; writing one
    /// into an asset would promote a guess to authored data, and the author would never see that it had been
    /// guessed.
    /// <para/>
    /// <b>The legacy pair is kept by default.</b> Migration adds the canonical token and leaves the old
    /// fields in place, so a batch can be reverted by clearing one field and the entry keeps resolving
    /// either way — <see cref="MolcaUiToken.ColorToken"/> takes precedence when assigned. Clearing the pair
    /// is a separate, opt-in second pass once the batch has been verified in context.
    /// <para/>
    /// <b>Every write goes through <see cref="SerializedObject"/>,</b> not through rebuilt token objects.
    /// A colour entry could carry fields this code does not know about, and reconstructing it would silently
    /// drop them.
    /// </remarks>
    public static class MolcaUiTokenCatalogMigration
    {
        /// <summary>
        /// Previews migrating a catalog's colour entries.
        /// </summary>
        /// <param name="catalog">The catalog to inspect.</param>
        /// <returns>A plan. Nothing is written.</returns>
        public static MolcaUiTokenCatalogMigrationPlan Plan(MolcaUiTokenCatalog catalog)
        {
            var entries = new List<MolcaUiTokenColorMigration>();
            var synonyms = new Dictionary<string, List<string>>();
            var errors = new List<string>();

            if (catalog == null)
            {
                errors.Add("No catalog supplied.");
                return new MolcaUiTokenCatalogMigrationPlan(null, null, entries, synonyms, errors);
            }

            var readiness = ColorThemeUpgradeReadiness.Evaluate();
            if (!readiness.IsReady)
            {
                errors.Add(readiness.Message);
                return new MolcaUiTokenCatalogMigrationPlan(catalog, null, entries, synonyms, errors);
            }
            var themeSet = readiness.ThemeSet;

            string path = AssetDatabase.GetAssetPath(catalog);
            string refusal = ColorThemeAssetWriteAccess.DescribeRefusal(path);
            if (refusal != null) errors.Add(refusal);

            var resolvedThemes = ResolveEveryVariant(themeSet, errors);
            if (errors.Count > 0)
                return new MolcaUiTokenCatalogMigrationPlan(catalog, themeSet, entries, synonyms, errors);

            var tokens = catalog.AllTokens;
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token == null || token.Category != MolcaUiTokenCategory.Color) continue;
                if (!token.HasLegacyColorPair) continue;   // Already migrated, or empty.

                string canonical = themeSet.ResolveLegacyToken(
                    new LegacyColorKey(token.SwatchName, token.ColorId));

                var missing = new List<string>();
                if (canonical != null)
                {
                    // A token absent from some variant would leave the entry rendering nothing there.
                    // Pinning the catalog to it would convert a working legacy lookup into a per-variant
                    // hole, so it is blocked rather than migrated with a warning.
                    foreach (var pair in resolvedThemes)
                    {
                        if (!pair.Value.Contains(canonical)) missing.Add(pair.Key);
                    }
                }

                var entry = new MolcaUiTokenColorMigration(i, token.Id, token.SwatchName, token.ColorId,
                    canonical, missing);
                entries.Add(entry);

                if (!entry.CanMigrate) continue;

                if (!synonyms.TryGetValue(canonical, out var sharing))
                {
                    sharing = new List<string>();
                    synonyms[canonical] = sharing;
                }
                sharing.Add(token.Id);
            }

            // Only genuine synonyms are interesting; a token named by exactly one entry is the normal case.
            var singles = new List<string>();
            foreach (var pair in synonyms) if (pair.Value.Count < 2) singles.Add(pair.Key);
            foreach (string key in singles) synonyms.Remove(key);

            return new MolcaUiTokenCatalogMigrationPlan(catalog, themeSet, entries, synonyms, errors);
        }

        /// <summary>
        /// Applies a plan, rewriting every migratable entry in one undo group.
        /// </summary>
        /// <param name="plan">The previewed plan.</param>
        /// <param name="clearLegacyPairs">
        /// Whether to also clear the legacy swatch and colour ID. Off by default so a batch stays
        /// revertible and keeps resolving either way.
        /// </param>
        /// <returns>How many entries were rewritten, or -1 when the plan was refused.</returns>
        public static int Apply(MolcaUiTokenCatalogMigrationPlan plan, bool clearLegacyPairs = false)
        {
            if (plan == null || !plan.IsValid || plan.Catalog == null) return -1;

            var serialized = new SerializedObject(plan.Catalog);
            var list = serialized.FindProperty("_tokens");
            if (list == null || !list.isArray) return -1;

            int applied = 0;
            foreach (var entry in plan.Entries)
            {
                if (!entry.CanMigrate) continue;
                if (entry.Index < 0 || entry.Index >= list.arraySize) continue;

                var element = list.GetArrayElementAtIndex(entry.Index);

                // Re-check identity before writing. A plan is a snapshot of list positions, and an author
                // who reordered or edited the catalog between preview and apply would otherwise have a
                // different entry rewritten than the one they reviewed.
                var idProperty = element.FindPropertyRelative("_id");
                if (idProperty == null || idProperty.stringValue != entry.CatalogTokenId) continue;

                // Dotted relative path into the nested ColorTokenReference struct.
                var tokenIdProperty = element.FindPropertyRelative("_colorToken._tokenId");
                if (tokenIdProperty == null) continue;

                tokenIdProperty.stringValue = entry.CanonicalTokenId;

                if (clearLegacyPairs)
                {
                    var swatch = element.FindPropertyRelative("_swatchName");
                    var colorId = element.FindPropertyRelative("_colorId");
                    if (swatch != null) swatch.stringValue = string.Empty;
                    if (colorId != null) colorId.stringValue = string.Empty;
                }

                applied++;
            }

            if (applied == 0) return 0;

            Undo.SetCurrentGroupName("Migrate catalog colour tokens");
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(plan.Catalog);
            AssetDatabase.SaveAssets();
            return applied;
        }

        /// <summary>Resolves every variant once, so per-entry coverage checks are a dictionary hit.</summary>
        private static Dictionary<string, ResolvedColorTheme> ResolveEveryVariant(ColorThemeSet themeSet,
            List<string> errors)
        {
            var resolved = new Dictionary<string, ResolvedColorTheme>();

            foreach (string variantId in themeSet.GetVariantIds())
            {
                if (ColorThemeResolver.TryResolve(themeSet, variantId, 0, out var theme, out var diagnostics)
                    == ColorThemeActivation.Activated)
                {
                    resolved[variantId] = theme;
                    continue;
                }

                // Migrating against a set whose variants do not resolve would produce coverage answers
                // that mean nothing.
                errors.Add($"Variant '{variantId}' does not resolve: {string.Join("; ", diagnostics)}");
            }

            if (resolved.Count == 0 && errors.Count == 0)
                errors.Add("The theme set declares no variants.");

            return resolved;
        }

        // ── menu items ─────────────────────────────────────────────────────────────────────────

        /// <summary>Previews migrating the selected catalog, writing nothing.</summary>
        [MenuItem("Molca/ColorID/Preview UI Token Catalog Colour Migration", priority = 60)]
        private static void PreviewSelected()
        {
            var catalog = Selection.activeObject as MolcaUiTokenCatalog;
            if (catalog == null)
            {
                Debug.LogWarning("[Molca UI] Select a UI Token Catalog asset first.");
                return;
            }

            Debug.Log(Plan(catalog).ToPreview(), catalog);
        }

        /// <summary>Migrates the selected catalog after logging what it will do.</summary>
        [MenuItem("Molca/ColorID/Migrate UI Token Catalog Colours", priority = 61)]
        private static void MigrateSelected()
        {
            var catalog = Selection.activeObject as MolcaUiTokenCatalog;
            if (catalog == null)
            {
                Debug.LogWarning("[Molca UI] Select a UI Token Catalog asset first.");
                return;
            }

            var plan = Plan(catalog);
            Debug.Log(plan.ToPreview(), catalog);

            if (!plan.IsValid) return;

            int applied = Apply(plan);
            Debug.Log(applied < 0
                ? "[Molca UI] Migration was refused."
                : $"[Molca UI] Migrated {applied} colour entry/entries in '{catalog.name}'. "
                  + $"{plan.BlockedCount} left on the legacy pair. The legacy swatch and colour ID are "
                  + "kept; clear them in a second pass once the batch is verified.", catalog);
        }
    }
}
