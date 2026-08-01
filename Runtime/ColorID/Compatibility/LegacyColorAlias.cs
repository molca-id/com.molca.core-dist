using System;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// A legacy V1 lookup key: the <c>(swatch, colorId)</c> pair that 194 shipped
    /// <see cref="ColorID"/> components and every <see cref="ColorIDReference"/> still serialize.
    /// </summary>
    /// <remarks>
    /// Comparison and hashing are case-insensitive and culture-invariant. V1 lookups were
    /// case-sensitive ordinal dictionary hits, but authored data reached them through inspectors and
    /// hand-edited YAML, so <c>Default.Text</c> and <c>default.text</c> both exist in the wild;
    /// resolving one and not the other would present as a random magenta.
    /// </remarks>
    [Serializable]
    public readonly struct LegacyColorKey : IEquatable<LegacyColorKey>
    {
        /// <summary>The V1 swatch name, for example <c>Default</c> or <c>Gray</c>.</summary>
        public string SwatchName { get; }

        /// <summary>The V1 colour ID within that swatch, for example <c>Primary</c> or <c>20</c>.</summary>
        public string ColorId { get; }

        /// <summary>Creates a legacy key.</summary>
        /// <param name="swatchName">The V1 swatch name. <c>null</c> is treated as empty.</param>
        /// <param name="colorId">The V1 colour ID. <c>null</c> is treated as empty.</param>
        public LegacyColorKey(string swatchName, string colorId)
        {
            SwatchName = swatchName ?? string.Empty;
            ColorId = colorId ?? string.Empty;
        }

        /// <summary>Whether both parts are present.</summary>
        public bool IsAssigned => !string.IsNullOrEmpty(SwatchName) && !string.IsNullOrEmpty(ColorId);

        /// <inheritdoc/>
        public bool Equals(LegacyColorKey other) =>
            string.Equals(SwatchName, other.SwatchName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ColorId, other.ColorId, StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is LegacyColorKey other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            // Must agree with the case-insensitive Equals above, or a dictionary built on this key
            // would miss entries that compare equal.
            int swatchHash = StringComparer.OrdinalIgnoreCase.GetHashCode(SwatchName ?? string.Empty);
            int colorHash = StringComparer.OrdinalIgnoreCase.GetHashCode(ColorId ?? string.Empty);
            unchecked { return swatchHash * 397 ^ colorHash; }
        }

        /// <summary>The dotted composite spelling used by <see cref="ColorModule"/> cache keys.</summary>
        public override string ToString() => $"{SwatchName}.{ColorId}";
    }

    /// <summary>
    /// Maps one legacy <c>(swatch, colorId)</c> pair to the canonical token that replaces it.
    /// </summary>
    /// <remarks>
    /// This is what lets existing content keep working while new authoring uses canonical tokens: the
    /// 194 shipped components are not rewritten, their pairs are translated. Aliases are authored
    /// deliberately, never generated from name similarity — the revamp plan is explicit that
    /// ambiguous legacy names such as <c>Secondary</c>, <c>Accent</c> and <c>Disabled</c> must have
    /// their real call sites reviewed before being assigned a semantic role, and that one legacy key
    /// may legitimately split across several canonical tokens at different call sites while keeping a
    /// single compatibility fallback here.
    /// <para/>
    /// Compatibility is observable, not silent: resolution records that it went through an alias, so
    /// migration progress can be measured and the removal gate can be evidence-based.
    /// </remarks>
    [Serializable]
    public class LegacyColorAlias
    {
        [SerializeField] private string _legacySwatchName;
        [SerializeField] private string _legacyColorId;
        [SerializeField] private string _canonicalTokenId;
        [SerializeField] private string _deprecatedSince;
        [SerializeField] private string _removalVersion;
        [SerializeField, TextArea(1, 3)] private string _note;

        /// <summary>The V1 swatch name this alias matches.</summary>
        public string LegacySwatchName => _legacySwatchName;

        /// <summary>The V1 colour ID this alias matches.</summary>
        public string LegacyColorId => _legacyColorId;

        /// <summary>The canonical token the legacy pair resolves through.</summary>
        public string CanonicalTokenId => _canonicalTokenId;

        /// <summary>Core version in which the legacy pair became deprecated. Informational.</summary>
        public string DeprecatedSince => _deprecatedSince;

        /// <summary>
        /// Major version in which this alias may be removed. Removal still requires an audit showing
        /// no blocking usage; this field records intent, it does not authorize anything.
        /// </summary>
        public string RemovalVersion => _removalVersion;

        /// <summary>Why this mapping was chosen — especially for a reviewed ambiguous key.</summary>
        public string Note => _note;

        /// <summary>The legacy key this alias matches.</summary>
        public LegacyColorKey Key => new LegacyColorKey(_legacySwatchName, _legacyColorId);

        /// <summary>Creates an alias. Intended for authoring tools, importers and tests.</summary>
        /// <param name="legacySwatchName">The V1 swatch name.</param>
        /// <param name="legacyColorId">The V1 colour ID.</param>
        /// <param name="canonicalTokenId">The canonical token to resolve through.</param>
        /// <param name="note">Why this mapping was chosen.</param>
        /// <param name="deprecatedSince">
        /// Core version in which the legacy pair became deprecated. Appended as an optional parameter so
        /// every existing caller — importer, test fixture, hand-built alias — still compiles.
        /// </param>
        /// <param name="removalVersion">Major version in which this alias may be removed.</param>
        public LegacyColorAlias(string legacySwatchName, string legacyColorId, string canonicalTokenId,
            string note = null, string deprecatedSince = null, string removalVersion = null)
        {
            _legacySwatchName = legacySwatchName;
            _legacyColorId = legacyColorId;
            _canonicalTokenId = canonicalTokenId;
            _note = note;
            _deprecatedSince = deprecatedSince;
            _removalVersion = removalVersion;
        }

        /// <summary>Whether this alias declares both ends of its lifecycle.</summary>
        /// <remarks>
        /// The removal gate is evidence-based: an alias with no declared removal version can never be
        /// scheduled for removal, because nothing records which release consumers were told to expect.
        /// Shipped aliases are required to declare both; an alias a project author or importer creates is
        /// not, which is why this is a query rather than part of <see cref="Validate"/>.
        /// </remarks>
        public bool HasDeclaredLifecycle =>
            !string.IsNullOrEmpty(_deprecatedSince) && !string.IsNullOrEmpty(_removalVersion);

        /// <summary>Validates this alias's shape.</summary>
        /// <param name="error">The first problem found, or <c>null</c> when valid.</param>
        /// <returns><c>true</c> when the alias is well-formed.</returns>
        public bool Validate(out string error)
        {
            if (string.IsNullOrEmpty(_legacySwatchName) || string.IsNullOrEmpty(_legacyColorId))
            {
                error = "Legacy alias is missing its swatch name or colour ID.";
                return false;
            }

            if (!ColorTokenId.Validate(_canonicalTokenId, out string idError))
            {
                error = $"Legacy alias '{Key}' names canonical token '{_canonicalTokenId}', "
                        + $"which is invalid: {idError}";
                return false;
            }

            error = null;
            return true;
        }
    }
}
