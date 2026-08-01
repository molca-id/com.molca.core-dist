#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using Molca.ColorID;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// The colour-theme JSON interchange format: schema identity, and the colour encoding both directions
    /// share.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Interchange/</c>.
    /// <para/>
    /// <b>Shape, and how it relates to W3C Design Tokens.</b> The DTCG format is a flat map of tokens, each
    /// with <c>$type</c>, <c>$value</c> and <c>$description</c>, and it has no ratified answer for
    /// multi-mode (light/dark) values. So this format follows DTCG exactly where DTCG has an answer and puts
    /// everything else under <c>$extensions.molca</c>, which is the mechanism DTCG defines for precisely
    /// this:
    /// <list type="bullet">
    /// <item><description>
    /// <c>$value</c> carries the <i>default variant's</i> resolved colour, so a plain DTCG reader sees a
    /// usable single-mode palette rather than an error.
    /// </description></item>
    /// <item><description>
    /// <c>$extensions.molca.modes</c> carries every variant's authored expression — a literal, an alias, or
    /// an alias with an alpha multiplier. This is the lossless representation; <c>$value</c> is the lossy
    /// courtesy copy.
    /// </description></item>
    /// <item><description>
    /// Token metadata that DTCG has no field for — usage, required, kind, deprecation — lives under
    /// <c>$extensions.molca</c> too, as do the accessibility requirements and the legacy alias map.
    /// </description></item>
    /// </list>
    /// Being explicit about which half is standard matters: a consumer can read the standard half and ignore
    /// the rest, and a future DTCG modes proposal can be adopted without changing what <c>$value</c> means.
    /// <para/>
    /// <b>No secrets.</b> The format carries tokens, variants, aliases and accessibility rules. Figma access
    /// tokens, file keys and private remote configuration are deliberately absent — an exported theme is a
    /// file people mail to each other.
    /// </remarks>
    public static class ColorThemeInterchange
    {
        /// <summary>Schema identifier written into and required by every document.</summary>
        public const string SchemaId = "molca.colortheme.interchange.v1";

        /// <summary>DTCG type for a colour token.</summary>
        public const string ColorType = "color";

        /// <summary>Formats a colour as <c>#RRGGBBAA</c>.</summary>
        /// <param name="color">The colour to encode.</param>
        /// <returns>An eight-digit hex string with a leading <c>#</c>.</returns>
        /// <remarks>
        /// Always eight digits, including a fully opaque alpha. A format that omitted <c>FF</c> would make
        /// "no alpha specified" and "alpha is 1" indistinguishable, and the alpha ramps this vocabulary is
        /// built on make that distinction load-bearing.
        /// <para/>
        /// Quantized to 8 bits, matching what <see cref="ResolvedColorTheme.SourceFingerprint"/> compares
        /// and what a display shows — a round trip through this format is therefore lossless at the
        /// precision anyone can observe.
        /// </remarks>
        public static string ToHex(Color color)
        {
            byte r = ToByte(color.r), g = ToByte(color.g), b = ToByte(color.b), a = ToByte(color.a);
            return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
        }

        /// <summary>Parses <c>#RRGGBB</c>, <c>#RRGGBBAA</c>, <c>#RGB</c> or <c>#RGBA</c>.</summary>
        /// <param name="hex">The string to parse; the leading <c>#</c> is optional.</param>
        /// <param name="color">The parsed colour, or <see cref="Color.clear"/>.</param>
        /// <returns><c>false</c> when the string is not a colour.</returns>
        /// <remarks>
        /// The short forms are accepted because hand-written and design-tool-exported files use them, and
        /// rejecting a file over <c>#FFF</c> would be pedantry. Missing alpha means opaque.
        /// </remarks>
        public static bool TryParseHex(string hex, out Color color)
        {
            color = Color.clear;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            string digits = hex.Trim();
            if (digits.StartsWith("#", StringComparison.Ordinal)) digits = digits.Substring(1);

            if (digits.Length == 3 || digits.Length == 4)
            {
                // Shorthand: each digit is doubled, so F becomes FF — the same expansion CSS uses.
                var expanded = new char[digits.Length * 2];
                for (int i = 0; i < digits.Length; i++)
                {
                    expanded[i * 2] = digits[i];
                    expanded[i * 2 + 1] = digits[i];
                }
                digits = new string(expanded);
            }

            if (digits.Length != 6 && digits.Length != 8) return false;

            if (!TryHexByte(digits, 0, out byte r) ||
                !TryHexByte(digits, 2, out byte g) ||
                !TryHexByte(digits, 4, out byte b)) return false;

            byte a = 255;
            if (digits.Length == 8 && !TryHexByte(digits, 6, out a)) return false;

            color = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
            return true;
        }

        private static bool TryHexByte(string digits, int offset, out byte value) =>
            byte.TryParse(digits.Substring(offset, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out value);

        private static byte ToByte(float channel) =>
            (byte)Mathf.Clamp(Mathf.RoundToInt(channel * 255f), 0, 255);
    }

    /// <summary>How one variant supplies a token's value, in interchange form.</summary>
    /// <remarks>
    /// Mirrors <see cref="ColorExpression"/> rather than flattening to a colour. Exporting only resolved
    /// colours would discard the alias structure, so a round trip would turn every alias into a literal and
    /// silently detach it from the primitive it was tracking — the single most valuable thing the model has.
    /// </remarks>
    public sealed class ColorThemeInterchangeValue
    {
        /// <summary>Literal colour as <c>#RRGGBBAA</c>, or <c>null</c> when this is an alias.</summary>
        public string Hex { get; set; }

        /// <summary>The token this aliases, or <c>null</c> when this is a literal.</summary>
        public string Alias { get; set; }

        /// <summary>Alpha multiplier applied to the alias target. <c>1</c> when there is none.</summary>
        public float AliasAlpha { get; set; } = 1f;

        /// <summary>Whether this value is an alias rather than a literal.</summary>
        public bool IsAlias => !string.IsNullOrEmpty(Alias);
    }

    /// <summary>One token in an interchange document.</summary>
    public sealed class ColorThemeInterchangeToken
    {
        /// <summary>The canonical token ID.</summary>
        public string Id { get; set; }

        /// <summary>Primitive or semantic.</summary>
        public ColorTokenKind Kind { get; set; } = ColorTokenKind.Semantic;

        /// <summary>What the token colours.</summary>
        public ColorTokenUsage Usage { get; set; } = ColorTokenUsage.None;

        /// <summary>Whether every variant must resolve it.</summary>
        public bool Required { get; set; } = true;

        /// <summary>Author-facing label, or <c>null</c>.</summary>
        public string DisplayName { get; set; }

        /// <summary>Guidance, or <c>null</c>.</summary>
        public string Description { get; set; }

        /// <summary>Whether the token is deprecated.</summary>
        public bool Deprecated { get; set; }

        /// <summary>The replacement for a deprecated token, or <c>null</c>.</summary>
        public string ReplacementId { get; set; }

        /// <summary>Free-form tags.</summary>
        public List<string> Tags { get; } = new List<string>();

        /// <summary>Per-variant values, keyed by variant ID.</summary>
        public Dictionary<string, ColorThemeInterchangeValue> Modes { get; } =
            new Dictionary<string, ColorThemeInterchangeValue>(StringComparer.Ordinal);
    }

    /// <summary>One variant in an interchange document.</summary>
    public sealed class ColorThemeInterchangeVariant
    {
        /// <summary>The variant ID.</summary>
        public string Id { get; set; }

        /// <summary>Author-facing label, or <c>null</c>.</summary>
        public string DisplayName { get; set; }

        /// <summary>Whether this variant is a high-contrast presentation.</summary>
        public bool IsHighContrast { get; set; }
    }

    /// <summary>One legacy alias in an interchange document.</summary>
    public sealed class ColorThemeInterchangeAlias
    {
        /// <summary>The V1 swatch name.</summary>
        public string Swatch { get; set; }

        /// <summary>The V1 colour ID.</summary>
        public string ColorId { get; set; }

        /// <summary>The canonical token it maps to.</summary>
        public string Token { get; set; }

        /// <summary>Why the mapping was chosen.</summary>
        public string Note { get; set; }
    }

    /// <summary>One accessibility requirement in an interchange document.</summary>
    public sealed class ColorThemeInterchangeContrast
    {
        /// <summary>The foreground token.</summary>
        public string Foreground { get; set; }

        /// <summary>The background token.</summary>
        public string Background { get; set; }

        /// <summary>The opaque surface a translucent background composites over, or <c>null</c>.</summary>
        public string UnderSurface { get; set; }

        /// <summary>The minimum acceptable ratio.</summary>
        public float MinimumRatio { get; set; } = ColorContrast.MinimumNormalText;

        /// <summary>Whether a failure blocks a build.</summary>
        public ColorContrastSeverity Severity { get; set; } = ColorContrastSeverity.Error;

        /// <summary>Variants this applies to; empty means all.</summary>
        public List<string> AppliesToVariants { get; } = new List<string>();

        /// <summary>Why the requirement exists.</summary>
        public string Rationale { get; set; }
    }

    /// <summary>A complete interchange document.</summary>
    public sealed class ColorThemeInterchangeDocument
    {
        /// <summary>Schema identifier. Must be <see cref="ColorThemeInterchange.SchemaId"/>.</summary>
        public string Schema { get; set; } = ColorThemeInterchange.SchemaId;

        /// <summary>The theme set's stable ID.</summary>
        public string SetId { get; set; }

        /// <summary>The theme set's display name.</summary>
        public string DisplayName { get; set; }

        /// <summary>The serialized schema version of the set this came from.</summary>
        public int SchemaVersion { get; set; } = ColorThemeSet.CurrentSchemaVersion;

        /// <summary>The variant whose value is written to each token's <c>$value</c>.</summary>
        public string DefaultVariantId { get; set; }

        /// <summary>The variants, in authored order.</summary>
        public List<ColorThemeInterchangeVariant> Variants { get; } =
            new List<ColorThemeInterchangeVariant>();

        /// <summary>The tokens, in authored order.</summary>
        public List<ColorThemeInterchangeToken> Tokens { get; } = new List<ColorThemeInterchangeToken>();

        /// <summary>The legacy alias map.</summary>
        public List<ColorThemeInterchangeAlias> LegacyAliases { get; } =
            new List<ColorThemeInterchangeAlias>();

        /// <summary>The accessibility requirements.</summary>
        public List<ColorThemeInterchangeContrast> Accessibility { get; } =
            new List<ColorThemeInterchangeContrast>();

        /// <summary>Fields the reader did not understand, as JSON paths.</summary>
        /// <remarks>
        /// Collected and reported rather than ignored. A file from a newer exporter may carry meaning this
        /// version drops, and an import preview that said nothing about it would look complete.
        /// </remarks>
        public List<string> UnsupportedFields { get; } = new List<string>();
    }
}
#endif
