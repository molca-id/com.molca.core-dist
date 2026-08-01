using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.ColorID
{
    /// <summary>One generated variant stylesheet and the variant it represents.</summary>
    [Serializable]
    public class ColorThemeVariantStylesheet
    {
        [SerializeField] private string _variantId;
        [SerializeField] private StyleSheet _stylesheet;

        /// <summary>The variant this stylesheet was generated for.</summary>
        public string VariantId => _variantId;

        /// <summary>The generated USS asset.</summary>
        public StyleSheet Stylesheet => _stylesheet;

        /// <summary>Creates an entry.</summary>
        /// <param name="variantId">The variant ID.</param>
        /// <param name="stylesheet">The generated stylesheet.</param>
        public ColorThemeVariantStylesheet(string variantId, StyleSheet stylesheet)
        {
            _variantId = variantId;
            _stylesheet = stylesheet;
        }
    }

    /// <summary>
    /// Records what was generated from a <see cref="ColorThemeSet"/>, and the fingerprint it was
    /// generated from.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> generated into the consumer project, normally
    /// <c>Assets/_MolcaSDK/Generated/Themes/&lt;set-id&gt;/</c> — never under package source.
    /// <b>Base class:</b> <see cref="ScriptableObject"/>.
    /// <b>Registration:</b> referenced by <see cref="ColorThemeDocumentBinder"/>; does not self-register.
    /// <para/>
    /// <b>Generated output is derived, never a source of truth.</b> The fingerprints recorded here are
    /// what make that checkable rather than aspirational: the same theme set always produces the same
    /// fingerprints, so a stale stylesheet is detectable instead of silently shipping last week's
    /// colours. A production build gate refuses stale output; the editor shows it as a status.
    /// <para/>
    /// The generation timestamp is <b>diagnostics only</b> and deliberately excluded from every
    /// freshness comparison — including it would make output that is byte-identical in content read as
    /// changed on every regeneration.
    /// </remarks>
    public class ColorThemeManifest : ScriptableObject
    {
        [SerializeField] private string _themeSetStableId;
        [SerializeField] private ColorThemeSet _themeSet;
        [SerializeField] private int _generatorVersion;
        [SerializeField] private List<ColorThemeVariantStylesheet> _variants =
            new List<ColorThemeVariantStylesheet>();
        [SerializeField] private List<string> _variantFingerprints = new List<string>();
        [SerializeField] private int _generatedTokenCount;
        [SerializeField] private string _generatedAtUtc;

        /// <summary>Stable ID of the theme set this output came from.</summary>
        public string ThemeSetStableId => _themeSetStableId;

        /// <summary>The source theme set, for navigation and regeneration.</summary>
        public ColorThemeSet ThemeSet => _themeSet;

        /// <summary>Version of the generator that produced this output.</summary>
        /// <remarks>
        /// A bump invalidates existing output even when the theme set has not changed, which is how a
        /// fix to the generator itself reaches already-generated projects.
        /// </remarks>
        public int GeneratorVersion => _generatorVersion;

        /// <summary>The generated stylesheets, one per variant.</summary>
        public IReadOnlyList<ColorThemeVariantStylesheet> Variants => _variants;

        /// <summary>Number of tokens exported into each stylesheet.</summary>
        public int GeneratedTokenCount => _generatedTokenCount;

        /// <summary>When this output was generated. Diagnostics only; never compared.</summary>
        public string GeneratedAtUtc => _generatedAtUtc;

        /// <summary>Finds the stylesheet generated for a variant.</summary>
        /// <param name="variantId">The variant ID; matched case-insensitively.</param>
        /// <returns>The stylesheet, or <c>null</c> when this manifest has none for that variant.</returns>
        public StyleSheet GetStylesheet(string variantId)
        {
            if (string.IsNullOrEmpty(variantId)) return null;

            foreach (var entry in _variants)
            {
                if (entry?.Stylesheet != null
                    && string.Equals(entry.VariantId, variantId, StringComparison.OrdinalIgnoreCase))
                    return entry.Stylesheet;
            }
            return null;
        }

        /// <summary>The fingerprint recorded for a variant at generation time.</summary>
        /// <param name="variantId">The variant ID; matched case-insensitively.</param>
        /// <returns>The fingerprint, or <c>null</c> when unknown.</returns>
        public string GetFingerprint(string variantId)
        {
            for (int i = 0; i < _variants.Count && i < _variantFingerprints.Count; i++)
            {
                if (_variants[i] != null
                    && string.Equals(_variants[i].VariantId, variantId, StringComparison.OrdinalIgnoreCase))
                    return _variantFingerprints[i];
            }
            return null;
        }

        /// <summary>
        /// Whether the generated output for a variant still matches what the theme set would produce.
        /// </summary>
        /// <param name="theme">A freshly resolved snapshot of the variant.</param>
        /// <param name="expectedGeneratorVersion">The current generator version.</param>
        /// <param name="reason">Why it is stale, or <c>null</c> when fresh.</param>
        /// <returns><c>true</c> when the recorded output is current.</returns>
        public bool IsFresh(ResolvedColorTheme theme, int expectedGeneratorVersion, out string reason)
        {
            if (theme == null)
            {
                reason = "No resolved theme to compare against.";
                return false;
            }

            if (_generatorVersion != expectedGeneratorVersion)
            {
                reason = $"Generated by generator version {_generatorVersion}; current is "
                         + $"{expectedGeneratorVersion}.";
                return false;
            }

            if (!string.Equals(_themeSetStableId, theme.SetId, StringComparison.Ordinal))
            {
                reason = $"Generated from theme set '{_themeSetStableId}' but the active set is "
                         + $"'{theme.SetId}'.";
                return false;
            }

            if (GetStylesheet(theme.VariantId) == null)
            {
                reason = $"No stylesheet was generated for variant '{theme.VariantId}'.";
                return false;
            }

            string recorded = GetFingerprint(theme.VariantId);
            if (!string.Equals(recorded, theme.SourceFingerprint, StringComparison.Ordinal))
            {
                reason = $"Variant '{theme.VariantId}' fingerprint is '{theme.SourceFingerprint}' but "
                         + $"the generated output records '{recorded}'. Regenerate the theme output.";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>Populates this manifest. Called only by the editor generator.</summary>
        /// <param name="themeSet">The source theme set.</param>
        /// <param name="generatorVersion">The generator version.</param>
        /// <param name="variants">The generated stylesheets.</param>
        /// <param name="fingerprints">Fingerprints, index-aligned with <paramref name="variants"/>.</param>
        /// <param name="tokenCount">Tokens exported per stylesheet.</param>
        /// <param name="generatedAtUtc">Timestamp for diagnostics.</param>
        internal void Populate(ColorThemeSet themeSet, int generatorVersion,
            List<ColorThemeVariantStylesheet> variants, List<string> fingerprints, int tokenCount,
            string generatedAtUtc)
        {
            _themeSet = themeSet;
            _themeSetStableId = themeSet != null ? themeSet.StableSetId : null;
            _generatorVersion = generatorVersion;
            _variants = variants ?? new List<ColorThemeVariantStylesheet>();
            _variantFingerprints = fingerprints ?? new List<string>();
            _generatedTokenCount = tokenCount;
            _generatedAtUtc = generatedAtUtc;
        }
    }
}
