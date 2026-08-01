using System;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// How a variant supplies the value for one colour token: a literal colour, an alias to another
    /// token, or an alias with an alpha multiplier applied.
    /// </summary>
    /// <remarks>
    /// Modelled as a kind discriminator plus fields rather than a class hierarchy because Unity
    /// serializes a polymorphic reference inside a <see cref="System.Collections.Generic.List{T}"/>
    /// only with <c>[SerializeReference]</c>, which stores type names in the asset and turns a
    /// namespace rename into a data-loss event. A flat shape keeps variant values diffable and
    /// migration-proof.
    /// <para/>
    /// <see cref="Kind.AliasWithAlpha"/> is what replaces duplicated opacity families: the shipped V1
    /// palettes spend 15 of their 31 keys on <c>Black.*</c>, <c>White.*</c> and <c>Text.*</c> ramps
    /// that are one base colour at five alpha levels. Expressing that relationship keeps the
    /// resolved colour identical while making the intent explicit and the base colour editable in
    /// one place.
    /// </remarks>
    [Serializable]
    public class ColorExpression
    {
        /// <summary>The kind of value a <see cref="ColorExpression"/> carries.</summary>
        public enum Kind
        {
            /// <summary>A literal RGBA colour authored directly on the variant.</summary>
            Literal = 0,

            /// <summary>The resolved colour of another token in the same variant, unchanged.</summary>
            Alias = 1,

            /// <summary>
            /// The resolved colour of another token with its alpha multiplied by
            /// <see cref="AlphaMultiplier"/>.
            /// </summary>
            AliasWithAlpha = 2
        }

        [SerializeField] private Kind _kind = Kind.Literal;
        [SerializeField] private Color _literal = Color.magenta;
        [SerializeField] private string _aliasTokenId;
        [SerializeField, Range(0f, 1f)] private float _alphaMultiplier = 1f;

        /// <summary>Which kind of value this expression carries.</summary>
        public Kind ExpressionKind => _kind;

        /// <summary>
        /// The literal colour. Only meaningful when <see cref="ExpressionKind"/> is
        /// <see cref="Kind.Literal"/>.
        /// </summary>
        public Color Literal => _literal;

        /// <summary>
        /// The canonical ID of the token this expression aliases, or <c>null</c> for a literal.
        /// </summary>
        public string AliasTokenId => _aliasTokenId;

        /// <summary>
        /// Multiplier applied to the aliased colour's alpha, in [0, 1]. Only meaningful when
        /// <see cref="ExpressionKind"/> is <see cref="Kind.AliasWithAlpha"/>.
        /// </summary>
        public float AlphaMultiplier => _alphaMultiplier;

        /// <summary>Whether this expression refers to another token.</summary>
        public bool IsAlias => _kind == Kind.Alias || _kind == Kind.AliasWithAlpha;

        private ColorExpression() { }

        /// <summary>Creates a literal-colour expression.</summary>
        /// <param name="color">The RGBA value this token resolves to in the owning variant.</param>
        public static ColorExpression FromLiteral(Color color) =>
            new ColorExpression { _kind = Kind.Literal, _literal = color, _aliasTokenId = null };

        /// <summary>Creates an alias expression that adopts another token's resolved colour.</summary>
        /// <param name="tokenId">The canonical ID of the token to adopt.</param>
        public static ColorExpression FromAlias(string tokenId) =>
            new ColorExpression { _kind = Kind.Alias, _aliasTokenId = tokenId, _alphaMultiplier = 1f };

        /// <summary>
        /// Creates an alias expression that adopts another token's colour with scaled alpha.
        /// </summary>
        /// <param name="tokenId">The canonical ID of the token to adopt.</param>
        /// <param name="alphaMultiplier">Alpha scale in [0, 1]; values outside are clamped.</param>
        public static ColorExpression FromAliasWithAlpha(string tokenId, float alphaMultiplier) =>
            new ColorExpression
            {
                _kind = Kind.AliasWithAlpha,
                _aliasTokenId = tokenId,
                _alphaMultiplier = Mathf.Clamp01(alphaMultiplier)
            };

        /// <summary>
        /// Validates this expression in isolation — shape only, not whether its alias target exists.
        /// </summary>
        /// <param name="error">The first problem found, or <c>null</c> when the shape is valid.</param>
        /// <returns><c>true</c> when the expression is internally well-formed.</returns>
        /// <remarks>
        /// Alias target existence, cycles and chain depth are graph properties and are checked by
        /// <see cref="ColorThemeResolver"/> against the whole variant, not here.
        /// </remarks>
        public bool Validate(out string error)
        {
            if (IsAlias)
            {
                if (string.IsNullOrEmpty(_aliasTokenId))
                {
                    error = "Alias expression has no target token ID.";
                    return false;
                }

                if (!ColorTokenId.Validate(_aliasTokenId, out string idError))
                {
                    error = $"Alias target '{_aliasTokenId}' is not a canonical token ID: {idError}";
                    return false;
                }

                // Serialized data can carry an out-of-range alpha even though the inspector slider
                // clamps it — a hand-edited asset or an import bypasses the slider entirely.
                if (_kind == Kind.AliasWithAlpha && (_alphaMultiplier < 0f || _alphaMultiplier > 1f))
                {
                    error = $"Alpha multiplier {_alphaMultiplier} is outside [0, 1].";
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>A short authoring-facing description, used in validation reports and the Hub.</summary>
        public override string ToString() => _kind switch
        {
            Kind.Literal => $"#{ColorUtilityCompat.ToHtmlStringRGBA(_literal)}",
            Kind.Alias => $"-> {_aliasTokenId}",
            Kind.AliasWithAlpha => $"-> {_aliasTokenId} @ {_alphaMultiplier:0.###}a",
            _ => "<invalid>"
        };
    }

    /// <summary>
    /// Thin indirection over <see cref="UnityEngine.ColorUtility"/>.
    /// </summary>
    /// <remarks>
    /// Exists only because <c>Molca.ColorID.ColorUtility</c> shadows
    /// <see cref="UnityEngine.ColorUtility"/> inside this namespace, so the engine helper cannot be
    /// named unqualified here. Keeping the workaround in one internal place avoids sprinkling
    /// fully-qualified engine calls through the theme model.
    /// </remarks>
    internal static class ColorUtilityCompat
    {
        /// <summary>Formats a colour as 8-digit RRGGBBAA hex.</summary>
        internal static string ToHtmlStringRGBA(Color color) =>
            UnityEngine.ColorUtility.ToHtmlStringRGBA(color);
    }
}
