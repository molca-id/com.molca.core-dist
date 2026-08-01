using UnityEngine;
using UnityEngine.Serialization;
using System;

namespace Molca.ColorID
{
    /// <summary>
    /// Simplified color reference field that gets colors from ColorModule
    /// </summary>
    [Serializable]
    public class ColorIDReference
    {
        [SerializeField, FormerlySerializedAs("swatchName")] private string _swatchName = "Default";
        [SerializeField, FormerlySerializedAs("colorId")] private string _colorId = "Primary";

        /// <summary>
        /// The swatch name to reference from ColorModule
        /// </summary>
        public string SwatchName
        {
            get => _swatchName ??= "Default";
            set => _swatchName = value;
        }

        /// <summary>
        /// The color ID to reference from ColorModule
        /// </summary>
        public string ColorId
        {
            get => _colorId ??= "Primary";
            set => _colorId = value;
        }

        /// <summary>
        /// Gets the color from ColorModule
        /// </summary>
        /// <remarks>
        /// Reads through <see cref="SwatchName"/>/<see cref="ColorId"/> rather than the backing
        /// fields so a reference deserialized from data that predates those fields (both null)
        /// resolves against the documented defaults instead of a null cache key.
        /// </remarks>
        public Color Color
        {
            get => ColorModule.ResolveActiveProvider().GetColor(SwatchName, ColorId);
        }

        /// <summary>
        /// Assigns both parts of this reference from a composite identifier.
        /// </summary>
        /// <param name="composite">
        /// A composite identifier in either supported spelling (<c>"Swatch/Color"</c> or
        /// <c>"Swatch.Color"</c>), or a bare colour ID — which updates <see cref="ColorId"/> only
        /// and leaves <see cref="SwatchName"/> as-is.
        /// </param>
        /// <remarks>
        /// Use this for any value that came out of <see cref="GetAvailableColorIds"/> or
        /// <see cref="IColorProvider.GetAllColorIds"/>: those return <i>dotted composite</i> keys.
        /// Assigning one straight into <see cref="ColorId"/> produces the doubled key
        /// <c>"Default.Default.Primary"</c>, which never resolves — the mistake the shipped example
        /// made. Parsing is shared with <see cref="ColorID.TryParseComposite"/>.
        /// </remarks>
        public void SetFromComposite(string composite)
        {
            if (ColorID.TryParseComposite(composite, out string parsedSwatch, out string parsedColorId))
            {
                _swatchName = parsedSwatch;
                _colorId = parsedColorId;
            }
            else
            {
                _colorId = composite;
            }
        }

        /// <summary>
        /// Creates a new ColorIDReference with the specified color ID and swatch
        /// </summary>
        /// <param name="colorId">The color ID to reference</param>
        /// <param name="swatchName">The swatch name (defaults to "Default")</param>
        public ColorIDReference(string _colorId = "Primary", string _swatchName = "Default")
        {
            this._colorId = _colorId;
            this._swatchName = _swatchName;
        }

        /// <summary>
        /// Gets the color with a specific alpha value
        /// </summary>
        /// <param name="alpha">The alpha value to apply</param>
        /// <returns>The color with the specified alpha</returns>
        public Color GetColorWithAlpha(float alpha)
        {
            return ColorModule.ResolveActiveProvider().GetColor(SwatchName, ColorId, Mathf.Clamp01(alpha));
        }

        /// <summary>
        /// Checks if the referenced color ID exists in ColorModule
        /// </summary>
        /// <returns>True if the color ID exists</returns>
        public bool IsValid()
        {
            return ColorModule.ResolveActiveProvider().HasColor(SwatchName, ColorId);
        }

        /// <summary>
        /// Gets all available color IDs from ColorModule
        /// </summary>
        /// <returns>
        /// Array of <b>dotted composite</b> identifiers (<c>"Swatch.Color"</c>) — not bare colour
        /// IDs. Feed these to <see cref="SetFromComposite"/>, never to <see cref="ColorId"/>.
        /// </returns>
        public static string[] GetAvailableColorIds()
        {
            return ColorModule.ResolveActiveProvider().GetAllColorIds();
        }

        /// <summary>
        /// Resolves this legacy pair against an explicit theme service.
        /// </summary>
        /// <param name="service">The theme service. May be <c>null</c>.</param>
        /// <param name="color">The resolved colour, or <see cref="Color.clear"/> on failure.</param>
        /// <returns>
        /// <c>false</c> when the service is unavailable, has no active theme, or the active theme's set
        /// carries no alias for this pair.
        /// </returns>
        /// <remarks>
        /// The replacement for <see cref="Color"/> and for the removed implicit conversion. Three
        /// differences that matter: the dependency on a theme is visible at the call site, failure is a
        /// return value rather than magenta, and a test or tool can resolve against a theme it chose
        /// instead of whatever global happens to be installed.
        /// <para/>
        /// New code should hold a <see cref="ColorTokenReference"/> instead; this exists so content that
        /// still serializes a legacy pair has a correct way to read it.
        /// </remarks>
        public bool TryResolve(IColorThemeService service, out Color color)
        {
            color = Color.clear;
            if (service?.ActiveTheme == null) return false;

            // Translated to a canonical token first, then resolved: the alias map lives on the set, and the
            // resolved theme is keyed by canonical ID only. A pair with no alias fails here rather than
            // reaching the resolver with a key it could never match.
            string tokenId = GetCanonicalTokenId(service.ThemeSet);
            return !string.IsNullOrEmpty(tokenId) && service.TryResolve(tokenId, out color);
        }

        /// <summary>
        /// The canonical token this legacy pair resolves through, or <c>null</c> when the set has no alias
        /// for it.
        /// </summary>
        /// <param name="themeSet">The theme set to ask. May be <c>null</c>.</param>
        /// <returns>The canonical token ID, or <c>null</c>.</returns>
        /// <remarks>
        /// Inspection only — it does not rewrite this reference. Migration tooling uses it to report what a
        /// site <i>would</i> become; use <see cref="ColorTokenReference"/> to actually hold the result.
        /// </remarks>
        public string GetCanonicalTokenId(ColorThemeSet themeSet) =>
            themeSet == null ? null : themeSet.ResolveLegacyToken(new LegacyColorKey(SwatchName, ColorId));

        /// <summary>
        /// Implicit conversion to Color. <b>Deprecated</b> — see <see cref="TryResolve"/>.
        /// </summary>
        /// <remarks>
        /// Retained for compatibility and scheduled for removal in Core 2.0.0. It is the most costly
        /// convenience in the V1 API: <c>image.color = myRef;</c> reads hidden global state, so it works
        /// after bootstrap and yields magenta before it, with nothing at the call site to suggest that
        /// ordering matters. It also throws a <see cref="NullReferenceException"/> on a null reference,
        /// where an explicit call can report failure.
        /// <para/>
        /// Migrating: <c>TryResolve(service, out var c)</c> for content that still holds a legacy pair, or
        /// a <see cref="ColorTokenReference"/> for new authoring. <c>.Color</c> stays available as a
        /// like-for-like stopgap and is not deprecated, because it at least makes the read explicit.
        /// </remarks>
        [Obsolete("The implicit ColorIDReference -> Color conversion resolves through hidden global state "
                  + "and returns magenta before bootstrap. Call TryResolve(IColorThemeService, out Color), "
                  + "or use ColorTokenReference for new authoring. Reading .Color explicitly also works. "
                  + "Scheduled for removal in Core 2.0.0.")]
        public static implicit operator Color(ColorIDReference reference)
        {
            return reference.Color;
        }

        /// <summary>
        /// Implicit conversion from string to ColorIDReference
        /// </summary>
        public static implicit operator ColorIDReference(string _colorId)
        {
            return new ColorIDReference(_colorId);
        }
    }
}