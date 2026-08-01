using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// WCAG 2.x relative-luminance and contrast-ratio maths, with explicit alpha compositing.
    /// </summary>
    /// <remarks>
    /// Two things are easy to get wrong here and are handled deliberately:
    /// <list type="number">
    /// <item><description>
    /// <b>Gamma.</b> WCAG relative luminance is defined on <i>sRGB</i> channel values, but Unity's
    /// <see cref="Color"/> in a linear-space project holds linear values. Feeding linear values
    /// straight into the WCAG formula double-applies the transfer function and inflates every ratio.
    /// <see cref="RelativeLuminance"/> therefore takes sRGB input, and callers holding engine colours
    /// convert first — the theme model stores authored sRGB values, which is what
    /// <see cref="ResolvedColorTheme"/> hands over.
    /// </description></item>
    /// <item><description>
    /// <b>Alpha.</b> A ratio is only meaningful between two opaque colours. A translucent foreground
    /// or background must first be composited over a known under-surface. There is no sensible
    /// default for that surface, so the API requires it rather than silently assuming white or
    /// black — assuming one is how a checker reports a comfortable pass for something the user
    /// cannot read.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class ColorContrast
    {
        /// <summary>WCAG AA minimum for normal-size text.</summary>
        public const float MinimumNormalText = 4.5f;

        /// <summary>WCAG AA minimum for large text and non-text critical indicators.</summary>
        public const float MinimumLargeText = 3f;

        /// <summary>WCAG AAA enhanced target for normal-size text.</summary>
        public const float EnhancedNormalText = 7f;

        /// <summary>
        /// WCAG relative luminance of an <b>opaque sRGB</b> colour.
        /// </summary>
        /// <param name="srgb">The colour, with channels in sRGB space. Alpha is ignored.</param>
        /// <returns>Relative luminance in [0, 1].</returns>
        public static float RelativeLuminance(Color srgb)
        {
            float r = LinearizeChannel(srgb.r);
            float g = LinearizeChannel(srgb.g);
            float b = LinearizeChannel(srgb.b);
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }

        // The WCAG piecewise sRGB -> linear transfer function. Not the same curve as Unity's
        // Color.linear, which is why this is spelled out rather than delegated.
        private static float LinearizeChannel(float channel)
        {
            channel = Mathf.Clamp01(channel);
            return channel <= 0.04045f
                ? channel / 12.92f
                : Mathf.Pow((channel + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>
        /// Composites a possibly-translucent colour over an opaque backdrop.
        /// </summary>
        /// <param name="source">The colour to composite. Its alpha drives the blend.</param>
        /// <param name="backdrop">The opaque colour underneath. Its alpha is ignored.</param>
        /// <returns>The opaque composited result.</returns>
        public static Color Composite(Color source, Color backdrop)
        {
            float alpha = Mathf.Clamp01(source.a);
            return new Color(
                Mathf.Lerp(backdrop.r, source.r, alpha),
                Mathf.Lerp(backdrop.g, source.g, alpha),
                Mathf.Lerp(backdrop.b, source.b, alpha),
                1f);
        }

        /// <summary>
        /// Contrast ratio between two <b>opaque</b> sRGB colours.
        /// </summary>
        /// <param name="foreground">The foreground colour. Alpha is ignored.</param>
        /// <param name="background">The background colour. Alpha is ignored.</param>
        /// <returns>A ratio in [1, 21], where 1 means identical luminance.</returns>
        public static float Ratio(Color foreground, Color background)
        {
            float first = RelativeLuminance(foreground);
            float second = RelativeLuminance(background);
            float lighter = Mathf.Max(first, second);
            float darker = Mathf.Min(first, second);
            return (lighter + 0.05f) / (darker + 0.05f);
        }

        /// <summary>
        /// Contrast ratio between colours that may be translucent, composited over a named surface.
        /// </summary>
        /// <param name="foreground">The foreground colour, possibly translucent.</param>
        /// <param name="background">The background colour, possibly translucent.</param>
        /// <param name="underSurface">
        /// The opaque surface beneath <paramref name="background"/>. Required whenever either input
        /// is translucent; pass the background itself when it is already opaque.
        /// </param>
        /// <returns>A ratio in [1, 21].</returns>
        /// <remarks>
        /// The background is composited over the under-surface first, then the foreground is
        /// composited over that result — the same order the renderer draws them, so the ratio matches
        /// what a user actually sees.
        /// </remarks>
        public static float RatioComposited(Color foreground, Color background, Color underSurface)
        {
            Color opaqueBackground = background.a >= 1f ? background : Composite(background, underSurface);
            Color opaqueForeground = foreground.a >= 1f ? foreground : Composite(foreground, opaqueBackground);
            return Ratio(opaqueForeground, opaqueBackground);
        }

        /// <summary>
        /// Whether a pair needs a declared under-surface before its contrast can be judged.
        /// </summary>
        /// <param name="foreground">The foreground colour.</param>
        /// <param name="background">The background colour.</param>
        /// <returns>
        /// <c>true</c> when the background is translucent, meaning the result depends on whatever sits
        /// beneath it. A requirement in this state is reported as <i>incomplete</i> rather than as a
        /// pass or a failure.
        /// </returns>
        /// <remarks>
        /// A translucent <i>foreground</i> does not make the pair unknowable — it composites over the
        /// background, which is known. Only an unknown backdrop does. The shipped V1 palettes make
        /// this concrete: <c>Default.Background</c> has alpha 0.901961 in both variants, so every
        /// contrast claim against it is incomplete until the surface beneath it is named.
        /// </remarks>
        public static bool RequiresUnderSurface(Color foreground, Color background) => background.a < 1f;
    }
}
