using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>One token's value inside a variant.</summary>
    [Serializable]
    public class ColorVariantValue
    {
        [SerializeField] private string _tokenId;
        [SerializeField] private ColorExpression _expression;

        /// <summary>The canonical ID of the token this value is for.</summary>
        public string TokenId => _tokenId;

        /// <summary>How the value is produced — literal, alias, or alias with alpha.</summary>
        public ColorExpression Expression => _expression;

        /// <summary>Creates a variant value.</summary>
        /// <param name="tokenId">The canonical token ID.</param>
        /// <param name="expression">The value expression.</param>
        public ColorVariantValue(string tokenId, ColorExpression expression)
        {
            _tokenId = tokenId;
            _expression = expression;
        }
    }

    /// <summary>
    /// One selectable appearance — Dark, Light, High Contrast, or a product-specific mode — supplying
    /// values for the theme set's shared token contract.
    /// </summary>
    /// <remarks>
    /// A variant carries values, never definitions. It cannot introduce a token the contract does not
    /// declare, and validation rejects it if it omits a required one. Variant IDs follow the same
    /// stable naming discipline as tokens but need not be hierarchical — <c>dark</c>, <c>light</c>,
    /// <c>high-contrast-dark</c>.
    /// </remarks>
    [Serializable]
    public class ColorThemeVariant
    {
        [SerializeField] private string _id;
        [SerializeField] private string _displayName;
        [SerializeField] private List<ColorVariantValue> _values = new List<ColorVariantValue>();
        [SerializeField] private bool _isHighContrast;
        [SerializeField] private List<string> _tags = new List<string>();

        /// <summary>The stable variant ID, persisted as the user's active-theme preference.</summary>
        public string Id => _id;

        /// <summary>Author-facing label shown in theme pickers.</summary>
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? _id : _displayName;

        /// <summary>This variant's token values.</summary>
        public IReadOnlyList<ColorVariantValue> Values => _values;

        /// <summary>
        /// Whether this variant is an accessibility high-contrast mode. Contrast requirements can
        /// target high-contrast variants with stricter ratios.
        /// </summary>
        public bool IsHighContrast => _isHighContrast;

        /// <summary>Free-form tags for filtering.</summary>
        public IReadOnlyList<string> Tags => _tags;

        /// <summary>Creates a variant. Intended for authoring tools, importers and tests.</summary>
        /// <param name="id">The stable variant ID.</param>
        /// <param name="displayName">Optional author-facing label.</param>
        /// <param name="isHighContrast">Whether this is a high-contrast accessibility mode.</param>
        public ColorThemeVariant(string id, string displayName = null, bool isHighContrast = false)
        {
            _id = id;
            _displayName = displayName;
            _isHighContrast = isHighContrast;
        }

        /// <summary>
        /// Adds or replaces this variant's value for a token.
        /// </summary>
        /// <param name="tokenId">The canonical token ID.</param>
        /// <param name="expression">The value expression.</param>
        /// <remarks>
        /// Authoring-time only. Variants live inside a <see cref="ColorThemeSet"/> asset, which is
        /// read-only configuration at runtime; mutating one during play would violate the
        /// ScriptableObject rule and is blocked by <see cref="ColorThemeSet"/>.
        /// </remarks>
        internal void SetValue(string tokenId, ColorExpression expression)
        {
            for (int i = 0; i < _values.Count; i++)
            {
                if (_values[i]?.TokenId == tokenId)
                {
                    _values[i] = new ColorVariantValue(tokenId, expression);
                    return;
                }
            }
            _values.Add(new ColorVariantValue(tokenId, expression));
        }

        /// <summary>
        /// Validates this variant's shape: ID grammar, no duplicate token values, well-formed
        /// expressions.
        /// </summary>
        /// <param name="errors">Every problem found is appended here.</param>
        /// <returns><c>true</c> when no problems were found.</returns>
        /// <remarks>
        /// Coverage against the token contract and alias-graph integrity are set-level and
        /// variant-graph properties respectively, checked by <see cref="ColorThemeSet"/> and
        /// <see cref="ColorThemeResolver"/>. All problems are collected rather than short-circuiting,
        /// because an author fixing a palette wants the whole list, not one error per save.
        /// </remarks>
        public bool Validate(List<string> errors)
        {
            int before = errors.Count;

            if (string.IsNullOrWhiteSpace(_id))
            {
                errors.Add("A variant has an empty ID.");
            }
            else if (ColorThemeSet.NormalizeVariantId(_id) != _id)
            {
                errors.Add($"Variant ID '{_id}' is not in canonical form "
                           + $"(expected '{ColorThemeSet.NormalizeVariantId(_id)}').");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in _values)
            {
                if (value == null)
                {
                    errors.Add($"Variant '{_id}' contains a null value entry.");
                    continue;
                }

                if (string.IsNullOrEmpty(value.TokenId))
                {
                    errors.Add($"Variant '{_id}' contains a value with no token ID.");
                    continue;
                }

                // A duplicate makes the winning value depend on serialization order, so the same
                // asset could resolve differently after an unrelated reorder.
                if (!seen.Add(value.TokenId))
                {
                    errors.Add($"Variant '{_id}' defines token '{value.TokenId}' more than once.");
                    continue;
                }

                if (value.Expression == null)
                {
                    errors.Add($"Variant '{_id}' has no expression for token '{value.TokenId}'.");
                }
                else if (!value.Expression.Validate(out string expressionError))
                {
                    errors.Add($"Variant '{_id}', token '{value.TokenId}': {expressionError}");
                }
            }

            return errors.Count == before;
        }
    }
}
