#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using Molca.ColorID;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// The only place that mutates a <see cref="ColorThemeSet"/>'s serialized lists.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Transactions/</c>.
    /// <b>Shape:</b> editor-only static service. Called by
    /// <see cref="ColorThemeTransactionExecutor"/> only — never directly by a view.
    /// <para/>
    /// <b>Why reflection.</b> <see cref="ColorThemeSet"/> exposes its lists as
    /// <see cref="IReadOnlyList{T}"/> and offers no mutators, on purpose: it is read-only configuration,
    /// and a public setter would be reachable from runtime code where writing it violates the framework's
    /// ScriptableObject rule. Adding internal mutators to the runtime type would weaken that guarantee for
    /// the sake of editor convenience. Confining the reflection to this one editor-only class keeps the
    /// runtime contract intact and makes every mutation site auditable — it is all in this file.
    /// <para/>
    /// Callers are responsible for <c>Undo.RecordObject</c>, <c>SetDirty</c> and saving; this class only
    /// changes in-memory state and returns how many changes it made.
    /// </remarks>
    internal static class ColorThemeSetEditing
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>Reads a private serialized <see cref="List{T}"/> off any theme-model object.</summary>
        /// <remarks>
        /// Generic over the owner so the same accessor reaches both the set's lists and a variant's
        /// values; a missing field means the runtime model was restructured without this file following,
        /// which must fail loudly rather than silently apply nothing.
        /// </remarks>
        private static List<T> Field<T>(object owner, string name)
        {
            var info = owner.GetType().GetField(name, Instance);
            if (info == null)
                throw new InvalidOperationException(
                    $"{owner.GetType().Name} has no field '{name}'. The theme model changed and "
                    + "ColorThemeSetEditing needs updating.");
            return (List<T>)info.GetValue(owner);
        }

        private static void SetPrivate(object target, string name, object value)
        {
            var info = target.GetType().GetField(name, Instance);
            if (info == null)
                throw new InvalidOperationException($"{target.GetType().Name} has no field '{name}'.");
            info.SetValue(target, value);
        }

        /// <summary>
        /// Writes a complete vocabulary into a theme set, replacing whatever it held.
        /// </summary>
        /// <param name="themeSet">The set to populate.</param>
        /// <param name="stableSetId">Stable identity; namespaces persistence and generated output.</param>
        /// <param name="displayName">Author-facing name.</param>
        /// <param name="tokens">The token contract.</param>
        /// <param name="variants">The variants, already carrying values for the contract.</param>
        /// <param name="aliases">Legacy compatibility mappings.</param>
        /// <param name="requirements">Authored contrast requirements.</param>
        /// <remarks>
        /// Wholesale replacement, used when authoring a set from a code-defined vocabulary. Incremental
        /// edits go through the other methods here so they can report how much they changed.
        /// </remarks>
        internal static void Populate(ColorThemeSet themeSet, string stableSetId, string displayName,
            List<ColorTokenDefinition> tokens, List<ColorThemeVariant> variants,
            List<LegacyColorAlias> aliases, List<ColorContrastRequirement> requirements)
        {
            SetPrivate(themeSet, "_stableSetId", stableSetId);
            SetPrivate(themeSet, "_displayName", displayName);
            SetPrivate(themeSet, "_schemaVersion", ColorThemeSet.CurrentSchemaVersion);
            SetPrivate(themeSet, "_tokenDefinitions", tokens ?? new List<ColorTokenDefinition>());
            SetPrivate(themeSet, "_variants", variants ?? new List<ColorThemeVariant>());
            SetPrivate(themeSet, "_legacyAliases", aliases ?? new List<LegacyColorAlias>());
            SetPrivate(themeSet, "_accessibilityRequirements",
                requirements ?? new List<ColorContrastRequirement>());
            themeSet.InvalidateIndexes();
        }

        /// <summary>
        /// Scopes a contrast requirement to specific variants.
        /// </summary>
        /// <param name="requirement">The requirement to scope.</param>
        /// <param name="variantIds">The variants it applies to; empty means every variant.</param>
        /// <remarks>
        /// The variant list is a private serialized field with no constructor parameter, because scoping is
        /// the uncommon case and a six-argument constructor was not worth it. An importer must be able to
        /// set it: a requirement that silently applied to every variant would change what the imported file
        /// means, and would fail pairs the author had deliberately excluded.
        /// </remarks>
        internal static void SetContrastVariantScope(ColorContrastRequirement requirement,
            IEnumerable<string> variantIds)
        {
            if (requirement == null) return;

            var scope = Field<string>(requirement, "_appliesToVariants");
            scope.Clear();
            if (variantIds == null) return;

            foreach (string variantId in variantIds)
            {
                if (!string.IsNullOrEmpty(variantId)) scope.Add(variantId);
            }
        }

        /// <summary>
        /// Sets one token's value in one variant.
        /// </summary>
        /// <param name="themeSet">The set to edit.</param>
        /// <param name="variantId">The variant whose value changes.</param>
        /// <param name="tokenId">The token to set.</param>
        /// <param name="expression">The new expression.</param>
        /// <returns><c>true</c> when the variant exists and was written.</returns>
        /// <remarks>
        /// <b>Not a transaction, deliberately.</b> Transactions exist because renaming or deleting a token
        /// can repoint references that live outside the set — that is what the fingerprint guard protects.
        /// Changing a <i>value</i> cannot: every reference still names the same token, and only the colour
        /// it resolves to moves. Routing it through a plan-and-confirm round trip would put a whole-project
        /// audit behind every drag of a colour picker and teach authors to click past previews.
        /// <para/>
        /// The caller is responsible for <see cref="Undo.RecordObject"/> and
        /// <see cref="EditorUtility.SetDirty"/>, so a multi-row edit groups as one undo step rather than
        /// one per row.
        /// </remarks>
        internal static bool SetTokenValue(ColorThemeSet themeSet, string variantId, string tokenId,
            ColorExpression expression)
        {
            if (themeSet == null || string.IsNullOrEmpty(tokenId)) return false;

            foreach (var variant in Field<ColorThemeVariant>(themeSet, "_variants"))
            {
                if (variant == null || variant.Id != variantId) continue;

                variant.SetValue(tokenId, expression);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The expression a variant currently holds for a token, or <c>null</c> when it holds none.
        /// </summary>
        /// <param name="themeSet">The set to read.</param>
        /// <param name="variantId">The variant.</param>
        /// <param name="tokenId">The token.</param>
        /// <remarks>
        /// Authoring needs the <i>authored</i> expression, not the resolved colour: an editor that showed a
        /// resolved value and wrote back a literal would silently convert every alias it touched into a
        /// hard-coded copy, which is precisely the property the alias tier exists to provide.
        /// </remarks>
        internal static ColorExpression? GetTokenValue(ColorThemeSet themeSet, string variantId,
            string tokenId)
        {
            if (themeSet == null || string.IsNullOrEmpty(tokenId)) return null;

            foreach (var variant in Field<ColorThemeVariant>(themeSet, "_variants"))
            {
                if (variant == null || variant.Id != variantId) continue;

                foreach (var value in variant.Values)
                {
                    if (value != null && value.TokenId == tokenId) return value.Expression;
                }
                return null;
            }

            return null;
        }

        /// <summary>
        /// Changes a token's canonical ID everywhere the set names it.
        /// </summary>
        /// <param name="themeSet">The set to edit.</param>
        /// <param name="fromTokenId">The current ID.</param>
        /// <param name="toTokenId">The new ID.</param>
        /// <returns>How many places were changed.</returns>
        /// <remarks>
        /// A rename touches five places, and missing any one leaves the set invalid: the definition, every
        /// variant's value key, every alias expression that <i>targets</i> it, every legacy alias mapping
        /// to it, and every contrast requirement naming it. They are all done here so a rename cannot be
        /// half-applied.
        /// </remarks>
        internal static int RenameToken(ColorThemeSet themeSet, string fromTokenId, string toTokenId)
        {
            int changes = 0;

            foreach (var definition in Field<ColorTokenDefinition>(themeSet, "_tokenDefinitions"))
            {
                if (definition == null) continue;

                if (definition.Id == fromTokenId)
                {
                    SetPrivate(definition, "_id", toTokenId);
                    changes++;
                }

                // A deprecated token pointing at the renamed one must follow it, or migration guidance
                // starts naming a token that no longer exists.
                if (definition.ReplacementId == fromTokenId)
                {
                    SetPrivate(definition, "_replacementId", toTokenId);
                    changes++;
                }
            }

            foreach (var variant in Field<ColorThemeVariant>(themeSet, "_variants"))
            {
                if (variant == null) continue;

                foreach (var value in Field<ColorVariantValue>(variant, "_values"))
                {
                    if (value == null) continue;

                    if (value.TokenId == fromTokenId)
                    {
                        SetPrivate(value, "_tokenId", toTokenId);
                        changes++;
                    }

                    // An alias whose target was renamed would otherwise dangle and fail resolution.
                    if (value.Expression != null && value.Expression.IsAlias
                        && value.Expression.AliasTokenId == fromTokenId)
                    {
                        SetPrivate(value.Expression, "_aliasTokenId", toTokenId);
                        changes++;
                    }
                }
            }

            foreach (var alias in Field<LegacyColorAlias>(themeSet, "_legacyAliases"))
            {
                if (alias == null || alias.CanonicalTokenId != fromTokenId) continue;
                SetPrivate(alias, "_canonicalTokenId", toTokenId);
                changes++;
            }

            foreach (var requirement in Field<ColorContrastRequirement>(themeSet, "_accessibilityRequirements"))
            {
                if (requirement == null) continue;

                foreach (string field in new[]
                         { "_foregroundTokenId", "_backgroundTokenId", "_underSurfaceTokenId" })
                {
                    var info = typeof(ColorContrastRequirement).GetField(field, Instance);
                    if (info == null) continue;
                    if ((string)info.GetValue(requirement) != fromTokenId) continue;
                    info.SetValue(requirement, toTokenId);
                    changes++;
                }
            }

            return changes;
        }

        /// <summary>Declares a token and gives every variant a literal value for it.</summary>
        /// <param name="themeSet">The set to edit.</param>
        /// <param name="tokenId">The canonical ID.</param>
        /// <param name="color">The literal every variant starts with.</param>
        /// <param name="kind">Primitive or semantic.</param>
        /// <param name="usage">What the token colours.</param>
        /// <param name="required">Whether every variant must resolve it.</param>
        /// <returns>How many places were changed.</returns>
        /// <remarks>
        /// Every variant is seeded in the same call. A token declared without a value in some variant
        /// would make the set invalid the moment it is added, which is exactly the state the contract
        /// model exists to prevent.
        /// </remarks>
        internal static int AddToken(ColorThemeSet themeSet, string tokenId, Color color,
            ColorTokenKind kind, ColorTokenUsage usage, bool required)
        {
            Field<ColorTokenDefinition>(themeSet, "_tokenDefinitions")
                .Add(new ColorTokenDefinition(tokenId, kind, usage, required));
            int changes = 1;

            foreach (var variant in Field<ColorThemeVariant>(themeSet, "_variants"))
            {
                if (variant == null) continue;
                // SetValue is internal on the runtime type; Molca.Editor has InternalsVisibleTo.
                variant.SetValue(tokenId, ColorExpression.FromLiteral(color));
                changes++;
            }

            return changes;
        }

        /// <summary>Maps a legacy pair to a canonical token.</summary>
        /// <param name="themeSet">The set to edit.</param>
        /// <param name="swatchName">The V1 swatch name.</param>
        /// <param name="colorId">The V1 colour ID.</param>
        /// <param name="canonicalTokenId">The canonical token to map to.</param>
        /// <param name="note">Why this mapping was chosen.</param>
        /// <returns>1 when added, 0 when the pair was already mapped.</returns>
        internal static int AddAlias(ColorThemeSet themeSet, string swatchName, string colorId,
            string canonicalTokenId, string note)
        {
            var aliases = Field<LegacyColorAlias>(themeSet, "_legacyAliases");
            var key = new LegacyColorKey(swatchName, colorId);

            foreach (var existing in aliases)
            {
                // Duplicate aliases make legacy resolution order-dependent, which the set's own
                // validation rejects — so adding one is a no-op rather than an error to recover from.
                if (existing != null && existing.Key.Equals(key)) return 0;
            }

            aliases.Add(new LegacyColorAlias(swatchName, colorId, canonicalTokenId, note));
            return 1;
        }

        /// <summary>Removes a variant, leaving the token contract untouched.</summary>
        /// <param name="themeSet">The set to edit.</param>
        /// <param name="variantId">The variant to remove.</param>
        /// <returns>1 when removed, 0 when not found.</returns>
        internal static int RemoveVariant(ColorThemeSet themeSet, string variantId)
        {
            var variants = Field<ColorThemeVariant>(themeSet, "_variants");
            for (int i = 0; i < variants.Count; i++)
            {
                if (variants[i] == null
                    || !string.Equals(variants[i].Id, variantId, StringComparison.OrdinalIgnoreCase))
                    continue;

                variants.RemoveAt(i);
                return 1;
            }
            return 0;
        }

        /// <summary>Adds a variant seeded from an existing one, so it satisfies the contract immediately.</summary>
        /// <param name="themeSet">The set to edit.</param>
        /// <param name="variantId">The new variant ID.</param>
        /// <param name="seedFromVariantId">The variant to copy values from, or <c>null</c> for the first.</param>
        /// <param name="displayName">Optional author-facing label.</param>
        /// <returns>How many values were seeded, plus one for the variant itself.</returns>
        internal static int AddVariant(ColorThemeSet themeSet, string variantId,
            string seedFromVariantId, string displayName = null)
        {
            var variants = Field<ColorThemeVariant>(themeSet, "_variants");
            var seed = themeSet.GetVariant(seedFromVariantId)
                       ?? (variants.Count > 0 ? variants[0] : null);

            var created = new ColorThemeVariant(variantId, displayName);
            int changes = 1;

            // Seeded rather than empty: an empty variant fails required-token validation the instant it
            // exists, which would make "add variant" produce an invalid set every time.
            if (seed != null)
            {
                foreach (var value in seed.Values)
                {
                    if (value?.Expression == null) continue;
                    created.SetValue(value.TokenId, value.Expression);
                    changes++;
                }
            }

            variants.Add(created);
            return changes;
        }
    }
}
#endif
