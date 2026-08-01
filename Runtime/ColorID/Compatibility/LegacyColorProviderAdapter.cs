using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>How a legacy <c>(swatch, colorId)</c> lookup found its value.</summary>
    /// <remarks>
    /// Compatibility has to be <i>observable</i>. If legacy resolution silently succeeded there would
    /// be no way to tell a properly-aliased project from one coasting on a lucky fallback, and no
    /// evidence base for deciding when the legacy API can be removed.
    /// </remarks>
    public enum LegacyResolutionKind
    {
        /// <summary>An authored alias mapped the pair to a canonical token. The healthy path.</summary>
        ExactAlias = 0,

        /// <summary>
        /// No alias existed, but the pair spelled a canonical token ID directly once normalized.
        /// </summary>
        DirectTokenId = 1,

        /// <summary>
        /// Resolved by searching for a token whose last segment matches the bare colour ID. Ambiguous
        /// by nature — reported so it can be replaced with an explicit alias.
        /// </summary>
        BareIdSearch = 2,

        /// <summary>Nothing matched; the caller received the magenta sentinel.</summary>
        Unresolved = 3
    }

    /// <summary>
    /// Presents the active V2 theme snapshot through the legacy <see cref="IColorProvider"/> API, so
    /// V1 content resolves against V2 data without its serialized values being rewritten.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/ColorID/Compatibility/</c>.
    /// <b>Shape:</b> plain C# class, created and owned by <see cref="ColorSchemeManager"/>; never
    /// registered as a service in its own right — it is reached through
    /// <see cref="IColorThemeService.LegacyProvider"/>.
    /// <para/>
    /// This is what makes the compatibility window real rather than aspirational: 194 shipped
    /// <see cref="ColorID"/> components and every <see cref="ColorIDReference"/> keep their
    /// <c>(swatch, colorId)</c> pairs, and those pairs are <i>translated</i> at lookup time instead of
    /// being migrated in the assets.
    /// <para/>
    /// Note what this class does <b>not</b> do: it never constructs a <see cref="ColorModule"/>. The
    /// revamp plan is explicit that a legacy view must be an adapter or a prebuilt editor-generated
    /// asset, never a ScriptableObject fabricated at runtime.
    /// <para/>
    /// The snapshot is captured at construction and never mutated, so a caller holding this adapter
    /// reads one coherent variant. A new adapter is built for each activation.
    /// </remarks>
    internal sealed class LegacyColorProviderAdapter : IColorProvider
    {
        private readonly ColorThemeSet _themeSet;
        private readonly ResolvedColorTheme _theme;

        // Memoizes translation, not colour: the (swatch, colorId) -> canonical token decision is
        // pure string work that would otherwise repeat on every lookup for the same pair, and
        // ColorID components look up the same pair on every theme change.
        private readonly Dictionary<LegacyColorKey, string> _translationCache =
            new Dictionary<LegacyColorKey, string>();

        // Bare-ID suffix index, built lazily because a well-aliased project never needs it.
        private Dictionary<string, List<string>> _tokensByLastSegment;

        /// <summary>Legacy pairs that resolved through an ambiguous or absent alias.</summary>
        /// <remarks>
        /// Accumulated for migration reporting. Bounded by the number of distinct pairs in the project,
        /// so it cannot grow without limit at runtime.
        /// </remarks>
        internal IReadOnlyDictionary<LegacyColorKey, LegacyResolutionKind> Findings => _findings;

        private readonly Dictionary<LegacyColorKey, LegacyResolutionKind> _findings =
            new Dictionary<LegacyColorKey, LegacyResolutionKind>();

        internal LegacyColorProviderAdapter(ColorThemeSet themeSet, ResolvedColorTheme theme)
        {
            _themeSet = themeSet;
            _theme = theme;
        }

        /// <summary>The snapshot this adapter reads.</summary>
        internal ResolvedColorTheme Theme => _theme;

        /// <summary>
        /// Translates a legacy pair to a canonical token ID and reports how it got there.
        /// </summary>
        /// <param name="key">The legacy pair.</param>
        /// <param name="kind">How the translation was found.</param>
        /// <returns>The canonical token ID, or <c>null</c> when nothing matched.</returns>
        internal string Translate(LegacyColorKey key, out LegacyResolutionKind kind)
        {
            if (!key.IsAssigned)
            {
                kind = LegacyResolutionKind.Unresolved;
                return null;
            }

            if (_translationCache.TryGetValue(key, out string cached))
            {
                kind = _findings.TryGetValue(key, out var recorded)
                    ? recorded
                    : LegacyResolutionKind.ExactAlias;
                return cached;
            }

            string tokenId = _themeSet?.ResolveLegacyToken(key);
            kind = LegacyResolutionKind.ExactAlias;

            if (tokenId == null)
            {
                // "Default/Primary" normalizes to "default/primary", which is a legitimate canonical
                // ID. A project that named its tokens after its old swatches needs no alias table.
                string normalized = ColorTokenId.Normalize($"{key.SwatchName}/{key.ColorId}");
                if (normalized != null && _theme != null && _theme.Contains(normalized))
                {
                    tokenId = normalized;
                    kind = LegacyResolutionKind.DirectTokenId;
                }
            }

            if (tokenId == null)
            {
                tokenId = FindByLastSegment(key.ColorId);
                if (tokenId != null) kind = LegacyResolutionKind.BareIdSearch;
            }

            if (tokenId == null) kind = LegacyResolutionKind.Unresolved;

            _translationCache[key] = tokenId;
            _findings[key] = kind;
            return tokenId;
        }

        // Last-resort match on the final ID segment: "Primary" finds "action/primary/fill" only when
        // exactly one token ends in "primary". More than one match is deliberately treated as no
        // match — picking one would be the same silent-wrong-answer behaviour the revamp exists to
        // remove, and the ambiguity is reported instead.
        private string FindByLastSegment(string bareId)
        {
            if (string.IsNullOrEmpty(bareId) || _theme == null) return null;

            if (_tokensByLastSegment == null)
            {
                _tokensByLastSegment = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string tokenId in _theme.GetTokenIds())
                {
                    int separator = tokenId.LastIndexOf(ColorTokenId.Separator);
                    string lastSegment = separator >= 0 ? tokenId.Substring(separator + 1) : tokenId;

                    if (!_tokensByLastSegment.TryGetValue(lastSegment, out var matches))
                    {
                        matches = new List<string>();
                        _tokensByLastSegment[lastSegment] = matches;
                    }
                    matches.Add(tokenId);
                }
            }

            return _tokensByLastSegment.TryGetValue(bareId, out var candidates) && candidates.Count == 1
                ? candidates[0]
                : null;
        }

        private Color Lookup(string swatchName, string colorId)
        {
            var key = new LegacyColorKey(swatchName, colorId);
            string tokenId = Translate(key, out _);

            if (tokenId != null && _theme != null && _theme.TryGetColor(tokenId, out Color color))
                return color;

            // Magenta preserved on purpose: legacy callers were built around a loudly wrong colour,
            // and changing that during the compatibility window would hide breakage rather than
            // surface it. New code uses IColorThemeService.TryResolve and gets a typed miss instead.
            Debug.LogWarning($"[ColorTheme] Legacy colour '{key}' does not resolve in variant "
                             + $"'{_theme?.VariantId}'. Add a legacy alias mapping it to a canonical token.");
            return Color.magenta;
        }

        private bool Has(string swatchName, string colorId)
        {
            string tokenId = Translate(new LegacyColorKey(swatchName, colorId), out _);
            return tokenId != null && _theme != null && _theme.Contains(tokenId);
        }

        #region IColorProvider

        Color IColorProvider.GetColor(string colorId) => LookupBare(colorId);

        Color IColorProvider.GetColor(string swatchName, string colorId) => Lookup(swatchName, colorId);

        Color IColorProvider.GetColor(string colorId, float alpha)
        {
            Color color = LookupBare(colorId);
            color.a = alpha;
            return color;
        }

        Color IColorProvider.GetColor(string swatchName, string colorId, float alpha)
        {
            Color color = Lookup(swatchName, colorId);
            color.a = alpha;
            return color;
        }

        bool IColorProvider.HasColor(string colorId) =>
            ColorID.TryParseComposite(colorId, out string swatch, out string bare)
                ? Has(swatch, bare)
                : Has(null, colorId);

        bool IColorProvider.HasColor(string swatchName, string colorId) => Has(swatchName, colorId);

        string[] IColorProvider.GetAllColorIds() => _theme?.GetTokenIds() ?? Array.Empty<string>();

        /// <remarks>
        /// A canonical token's first segment stands in for the legacy swatch, so
        /// <c>GetColorIdsInSwatch("text")</c> lists the <c>text/*</c> family. That is the closest honest
        /// mapping: V2 has no swatches, and returning nothing would break legacy pickers entirely.
        /// </remarks>
        string[] IColorProvider.GetColorIdsInSwatch(string swatchName)
        {
            if (_theme == null || string.IsNullOrEmpty(swatchName)) return Array.Empty<string>();

            string prefix = ColorTokenId.Normalize($"{swatchName}/x");
            if (prefix == null) return Array.Empty<string>();
            prefix = prefix.Substring(0, prefix.Length - 1); // drop the placeholder segment, keep "<name>/"

            var matches = new List<string>();
            foreach (string tokenId in _theme.GetTokenIds())
            {
                if (tokenId.StartsWith(prefix, StringComparison.Ordinal))
                    matches.Add(tokenId.Substring(prefix.Length));
            }
            return matches.ToArray();
        }

        string[] IColorProvider.GetSwatchNames()
        {
            if (_theme == null) return Array.Empty<string>();

            var groups = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string tokenId in _theme.GetTokenIds())
            {
                int separator = tokenId.IndexOf(ColorTokenId.Separator);
                if (separator <= 0) continue;
                string group = tokenId.Substring(0, separator);
                if (seen.Add(group)) groups.Add(group);
            }
            return groups.ToArray();
        }

        #endregion

        private Color LookupBare(string colorId) =>
            ColorID.TryParseComposite(colorId, out string swatch, out string bare)
                ? Lookup(swatch, bare)
                : Lookup(null, colorId);
    }
}
