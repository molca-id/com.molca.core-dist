using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// The single source of truth for a project's colour theme: one token contract, the variants that
    /// supply values for it, the accessibility rules those values must satisfy, and the aliases that
    /// keep legacy content resolving.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/ColorID/Themes/</c>.
    /// <b>Base class:</b> <see cref="ScriptableObject"/>.
    /// <b>Registration:</b> none — this asset never self-registers. It is referenced by the project's
    /// <c>ColorThemeSettings</c> module, which <c>GlobalSettings</c> owns.
    /// <para/>
    /// The structural inversion versus V1: token <i>definitions</i> live here, once, and a
    /// <see cref="ColorThemeVariant"/> supplies values for them. V1 gave each <c>ColorModule</c>
    /// its own independent list, which is why a key could exist in Dark and not in Light and switching
    /// theme turned it magenta. A variant here cannot introduce an undeclared token, and validation
    /// rejects a set whose variant omits a required one — so parity is structural rather than
    /// something to remember.
    /// <para/>
    /// This is authored configuration and is <b>read-only at runtime</b>, like every other Molca
    /// ScriptableObject. The active variant is mutable state and lives on <c>ColorThemeState</c>;
    /// the flattened lookup lives on an immutable <see cref="ResolvedColorTheme"/> snapshot.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Color Theme Set", menuName = "Molca/Settings/Color Theme Set", order = 11)]
    public class ColorThemeSet : ScriptableObject
    {
        /// <summary>Schema version of the serialized theme-set shape.</summary>
        /// <remarks>
        /// Bumped only when the serialized layout changes in a way that needs migration. Persisted
        /// alongside the active variant so a stored preference written by a newer schema can be
        /// detected rather than misread.
        /// </remarks>
        public const int CurrentSchemaVersion = 1;

        [SerializeField] private string _stableSetId;
        [SerializeField] private string _displayName;
        [SerializeField] private int _schemaVersion = CurrentSchemaVersion;
        [SerializeField] private List<ColorTokenDefinition> _tokenDefinitions = new List<ColorTokenDefinition>();
        [SerializeField] private List<ColorThemeVariant> _variants = new List<ColorThemeVariant>();
        [SerializeField] private List<LegacyColorAlias> _legacyAliases = new List<LegacyColorAlias>();
        [SerializeField] private List<ColorContrastRequirement> _accessibilityRequirements =
            new List<ColorContrastRequirement>();

        // Built lazily from _tokenDefinitions / _legacyAliases and invalidated by OnValidate.
        // Not serialized: a cache of authored data, never a second source of truth.
        private Dictionary<string, ColorTokenDefinition> _definitionsById;
        private Dictionary<LegacyColorKey, LegacyColorAlias> _aliasesByKey;

        /// <summary>
        /// Stable identity, generated once at authoring time and never changed.
        /// </summary>
        /// <remarks>
        /// Namespaces persistence keys and generated artifacts, so renaming the asset or its display
        /// name does not orphan a user's saved theme preference. This is the fix for V1's persistence
        /// keying, which derived from <c>typeof(ColorModule).FullName</c> and therefore made every
        /// variant share one key.
        /// </remarks>
        public string StableSetId => _stableSetId;

        /// <summary>Author-facing name. Presentation only.</summary>
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? name : _displayName;

        /// <summary>Serialized schema version of this asset.</summary>
        public int SchemaVersion => _schemaVersion;

        /// <summary>The token contract every variant supplies values for.</summary>
        public IReadOnlyList<ColorTokenDefinition> TokenDefinitions => _tokenDefinitions;

        /// <summary>The selectable variants.</summary>
        public IReadOnlyList<ColorThemeVariant> Variants => _variants;

        /// <summary>Legacy <c>(swatch, colorId)</c> to canonical-token mappings.</summary>
        public IReadOnlyList<LegacyColorAlias> LegacyAliases => _legacyAliases;

        /// <summary>Authored contrast requirements.</summary>
        public IReadOnlyList<ColorContrastRequirement> AccessibilityRequirements => _accessibilityRequirements;

        /// <summary>Normalizes a variant ID to canonical form (lower-case, hyphen-separated).</summary>
        /// <param name="variantId">The raw variant ID.</param>
        /// <returns>The canonical form, or <c>null</c> when the input is blank.</returns>
        /// <remarks>
        /// Variant IDs are flatter than token IDs — <c>dark</c>, <c>high-contrast-dark</c> — so they
        /// need no separator and are not required to have two segments.
        /// </remarks>
        public static string NormalizeVariantId(string variantId)
        {
            if (string.IsNullOrWhiteSpace(variantId)) return null;

            var chars = variantId.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c >= 'A' && c <= 'Z') chars[i] = (char)(c + ('a' - 'A'));
                else if (c == ' ' || c == '_' || c == '.' || c == '/') chars[i] = '-';
            }
            return new string(chars);
        }

        /// <summary>Finds a token definition by canonical ID.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        /// <returns>The definition, or <c>null</c> when the contract does not declare it.</returns>
        public ColorTokenDefinition GetDefinition(string tokenId)
        {
            if (string.IsNullOrEmpty(tokenId)) return null;
            EnsureIndexes();
            return _definitionsById.TryGetValue(tokenId, out var definition) ? definition : null;
        }

        /// <summary>Finds a variant by ID.</summary>
        /// <param name="variantId">The variant ID; matched case-insensitively.</param>
        /// <returns>The variant, or <c>null</c> when this set has no such variant.</returns>
        public ColorThemeVariant GetVariant(string variantId)
        {
            if (string.IsNullOrEmpty(variantId)) return null;

            foreach (var variant in _variants)
            {
                if (variant != null
                    && string.Equals(variant.Id, variantId, StringComparison.OrdinalIgnoreCase))
                    return variant;
            }
            return null;
        }

        /// <summary>Resolves a legacy <c>(swatch, colorId)</c> pair to its canonical token ID.</summary>
        /// <param name="key">The legacy key.</param>
        /// <returns>The canonical token ID, or <c>null</c> when no alias covers the pair.</returns>
        public string ResolveLegacyToken(LegacyColorKey key)
        {
            if (!key.IsAssigned) return null;
            EnsureIndexes();
            return _aliasesByKey.TryGetValue(key, out var alias) ? alias.CanonicalTokenId : null;
        }

        /// <summary>Every variant ID in authored order.</summary>
        /// <returns>A fresh array; safe for the caller to keep.</returns>
        public string[] GetVariantIds()
        {
            var ids = new List<string>(_variants.Count);
            foreach (var variant in _variants)
            {
                if (variant != null && !string.IsNullOrEmpty(variant.Id)) ids.Add(variant.Id);
            }
            return ids.ToArray();
        }

        private void EnsureIndexes()
        {
            if (_definitionsById != null && _aliasesByKey != null) return;

            _definitionsById = new Dictionary<string, ColorTokenDefinition>(StringComparer.Ordinal);
            foreach (var definition in _tokenDefinitions)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Id)) continue;
                // First wins; duplicates are a validation error rather than something to merge.
                if (!_definitionsById.ContainsKey(definition.Id))
                    _definitionsById.Add(definition.Id, definition);
            }

            _aliasesByKey = new Dictionary<LegacyColorKey, LegacyColorAlias>();
            foreach (var alias in _legacyAliases)
            {
                if (alias == null) continue;
                var key = alias.Key;
                if (!key.IsAssigned) continue;
                if (!_aliasesByKey.ContainsKey(key)) _aliasesByKey.Add(key, alias);
            }
        }

        /// <summary>
        /// Drops the lookup caches so the next read rebuilds them from the serialized lists.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="OnValidate"/> and by authoring tools after mutating the asset in the
        /// editor. Deliberately does not itself re-run validation, save, or touch anything outside
        /// this asset — the opposite of V1's <c>ColorModule.OnValidate</c>, which
        /// cleared persisted overrides and recoloured every open scene as a side effect of a keystroke.
        /// </remarks>
        public void InvalidateIndexes()
        {
            _definitionsById = null;
            _aliasesByKey = null;
        }

        /// <summary>
        /// Validates the whole set: token contract, variant shape and coverage, alias targets, and
        /// contrast-requirement targets.
        /// </summary>
        /// <param name="errors">Every problem found is appended here.</param>
        /// <returns><c>true</c> when the set is structurally valid.</returns>
        /// <remarks>
        /// Structure only. Alias <i>graph</i> integrity — cycles, chain depth, unresolvable targets
        /// within a variant — needs per-variant flattening and is checked by
        /// <see cref="ColorThemeResolver"/> during activation. Contrast <i>ratios</i> likewise need
        /// resolved values.
        /// <para/>
        /// Every problem is collected rather than short-circuiting: an author repairing an imported
        /// set wants the full list.
        /// </remarks>
        public bool Validate(List<string> errors)
        {
            if (errors == null) throw new ArgumentNullException(nameof(errors));
            int before = errors.Count;

            if (string.IsNullOrEmpty(_stableSetId))
            {
                errors.Add($"Theme set '{name}' has no stable set ID. Generate one before shipping — "
                           + "persistence keys and generated artifacts are namespaced by it.");
            }

            if (_schemaVersion > CurrentSchemaVersion)
            {
                errors.Add($"Theme set '{name}' declares schema version {_schemaVersion}, but this "
                           + $"version of Core understands at most {CurrentSchemaVersion}.");
            }

            ValidateTokenContract(errors);
            ValidateVariants(errors);
            ValidateAliases(errors);
            ValidateContrastRequirements(errors);

            return errors.Count == before;
        }

        private void ValidateTokenContract(List<string> errors)
        {
            if (_tokenDefinitions.Count == 0)
            {
                errors.Add($"Theme set '{name}' declares no tokens.");
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seenCaseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var definition in _tokenDefinitions)
            {
                if (definition == null)
                {
                    errors.Add($"Theme set '{name}' contains a null token definition.");
                    continue;
                }

                if (!definition.Validate(out string definitionError))
                {
                    errors.Add(definitionError);
                    continue;
                }

                if (!seen.Add(definition.Id))
                {
                    errors.Add($"Token '{definition.Id}' is declared more than once.");
                    continue;
                }

                // Canonical IDs are lower-case, so a case-insensitive collision that is not an exact
                // duplicate means two IDs differ only by characters the grammar already forbids —
                // caught above — or that generated artifact names would collide.
                if (!seenCaseInsensitive.Add(definition.Id))
                {
                    errors.Add($"Token '{definition.Id}' collides with another token when compared "
                               + "case-insensitively; generated artifact names would clash.");
                }
            }

            // A replacement must exist, or migrating off the deprecated token has nowhere to go.
            foreach (var definition in _tokenDefinitions)
            {
                if (definition == null || !definition.Deprecated) continue;
                if (string.IsNullOrEmpty(definition.ReplacementId)) continue;
                if (!seen.Contains(definition.ReplacementId))
                {
                    errors.Add($"Deprecated token '{definition.Id}' names replacement "
                               + $"'{definition.ReplacementId}', which this set does not declare.");
                }
            }
        }

        private void ValidateVariants(List<string> errors)
        {
            if (_variants.Count == 0)
            {
                errors.Add($"Theme set '{name}' declares no variants, so nothing can be activated.");
                return;
            }

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in _tokenDefinitions)
            {
                if (definition != null && !string.IsNullOrEmpty(definition.Id)) declared.Add(definition.Id);
            }

            foreach (var variant in _variants)
            {
                if (variant == null)
                {
                    errors.Add($"Theme set '{name}' contains a null variant.");
                    continue;
                }

                variant.Validate(errors);

                if (!string.IsNullOrEmpty(variant.Id) && !seenIds.Add(variant.Id))
                {
                    errors.Add($"Variant ID '{variant.Id}' is used more than once.");
                }

                var covered = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in variant.Values)
                {
                    if (value == null || string.IsNullOrEmpty(value.TokenId)) continue;

                    // The contract owns the token list. A variant carrying a value for something the
                    // contract does not declare is exactly the V1 drift this model exists to prevent.
                    if (!declared.Contains(value.TokenId))
                    {
                        errors.Add($"Variant '{variant.Id}' defines token '{value.TokenId}', which the "
                                   + "token contract does not declare.");
                        continue;
                    }
                    covered.Add(value.TokenId);
                }

                foreach (var definition in _tokenDefinitions)
                {
                    if (definition == null || !definition.Required) continue;
                    if (!covered.Contains(definition.Id))
                    {
                        errors.Add($"Variant '{variant.Id}' does not supply required token "
                                   + $"'{definition.Id}'.");
                    }
                }
            }
        }

        private void ValidateAliases(List<string> errors)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in _tokenDefinitions)
            {
                if (definition != null && !string.IsNullOrEmpty(definition.Id)) declared.Add(definition.Id);
            }

            var seenKeys = new HashSet<LegacyColorKey>();
            foreach (var alias in _legacyAliases)
            {
                if (alias == null)
                {
                    errors.Add($"Theme set '{name}' contains a null legacy alias.");
                    continue;
                }

                if (!alias.Validate(out string aliasError))
                {
                    errors.Add(aliasError);
                    continue;
                }

                // An ambiguous alias would make legacy resolution depend on list order — the same
                // class of order-dependence the duplicate-key checks exist to remove.
                if (!seenKeys.Add(alias.Key))
                {
                    errors.Add($"Legacy alias '{alias.Key}' is mapped more than once.");
                    continue;
                }

                if (!declared.Contains(alias.CanonicalTokenId))
                {
                    errors.Add($"Legacy alias '{alias.Key}' maps to token '{alias.CanonicalTokenId}', "
                               + "which this set does not declare.");
                }
            }
        }

        private void ValidateContrastRequirements(List<string> errors)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in _tokenDefinitions)
            {
                if (definition != null && !string.IsNullOrEmpty(definition.Id)) declared.Add(definition.Id);
            }

            foreach (var requirement in _accessibilityRequirements)
            {
                if (requirement == null)
                {
                    errors.Add($"Theme set '{name}' contains a null contrast requirement.");
                    continue;
                }

                if (!requirement.Validate(out string requirementError))
                {
                    errors.Add(requirementError);
                    continue;
                }

                foreach (var tokenId in new[]
                         {
                             requirement.ForegroundTokenId,
                             requirement.BackgroundTokenId,
                             requirement.UnderSurfaceTokenId
                         })
                {
                    if (string.IsNullOrEmpty(tokenId)) continue;
                    if (!declared.Contains(tokenId))
                    {
                        errors.Add($"Contrast requirement references token '{tokenId}', which this set "
                                   + "does not declare.");
                    }
                }

                foreach (var variantId in requirement.AppliesToVariants)
                {
                    if (string.IsNullOrEmpty(variantId)) continue;
                    if (GetVariant(variantId) == null)
                    {
                        errors.Add($"Contrast requirement targets variant '{variantId}', which this set "
                                   + "does not declare.");
                    }
                }
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Authoring helper: assigns a stable set ID if the asset has none.
        /// </summary>
        /// <returns>The stable set ID after the call.</returns>
        /// <remarks>
        /// Editor-only and idempotent. An existing ID is never regenerated — doing so would orphan
        /// every persisted preference and generated artifact keyed to it.
        /// </remarks>
        public string EnsureStableSetId()
        {
            if (string.IsNullOrEmpty(_stableSetId))
            {
                _stableSetId = Guid.NewGuid().ToString("N");
                UnityEditor.EditorUtility.SetDirty(this);
            }
            return _stableSetId;
        }

        /// <remarks>
        /// Cache invalidation only. See <see cref="InvalidateIndexes"/> for why this is deliberately
        /// inert beyond that.
        /// </remarks>
        private void OnValidate() => InvalidateIndexes();
#endif
    }
}
