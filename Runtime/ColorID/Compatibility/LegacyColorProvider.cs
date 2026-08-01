namespace Molca.ColorID
{
    /// <summary>
    /// Creates a legacy <see cref="IColorProvider"/> view of any variant of a theme set.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/ColorID/Compatibility/</c>.
    /// <para/>
    /// <see cref="ColorSchemeManager"/> already exposes a provider for the <i>active</i> variant, and that
    /// is what shipped content resolves through. This exists for the other case: a tool that must read a
    /// <i>chosen</i> variant, not whichever one the session happens to be running.
    /// <para/>
    /// The concrete adapter's constructor stays internal — it is bound to a live snapshot and a manager owns
    /// its lifetime — so this factory is the supported way for another assembly to obtain one. It resolves
    /// the variant itself, which means the caller cannot hand it a half-built snapshot.
    /// <para/>
    /// The motivating case is design-tool import. Snapping Figma colours against "the palette" silently
    /// meant the first <c>ColorModule</c> in the settings list, so a file authored in light mode was matched
    /// against dark values and every result came back a poor match with no indication why.
    /// </remarks>
    public static class LegacyColorProvider
    {
        /// <summary>
        /// Resolves a variant and returns a legacy-API view of it.
        /// </summary>
        /// <param name="themeSet">The theme set to read.</param>
        /// <param name="variantId">
        /// The variant to resolve. Blank picks the set's first declared variant, which is the closest thing
        /// to a default a theme set knows about on its own.
        /// </param>
        /// <param name="provider">The provider, or <c>null</c> on failure.</param>
        /// <param name="resolvedVariantId">Which variant was actually resolved, for reporting.</param>
        /// <param name="error">Why it failed, or <c>null</c>.</param>
        /// <returns><c>true</c> when a provider was created.</returns>
        /// <remarks>
        /// The resolved variant ID is an <c>out</c> parameter rather than something the caller infers: a
        /// tool that fell back to a default must be able to say so in its report, because "which variant did
        /// this run compare against" is the first question to ask about a poor match.
        /// </remarks>
        public static bool TryCreate(ColorThemeSet themeSet, string variantId,
            out IColorProvider provider, out string resolvedVariantId, out string error)
        {
            provider = null;
            resolvedVariantId = null;
            error = null;

            if (themeSet == null)
            {
                error = "No theme set supplied.";
                return false;
            }

            var variantIds = themeSet.GetVariantIds();
            if (variantIds.Length == 0)
            {
                error = $"Theme set '{themeSet.DisplayName}' declares no variants.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(variantId))
            {
                resolvedVariantId = variantIds[0];
            }
            else if (themeSet.GetVariant(variantId) != null)
            {
                // Normalized through the set so the reported ID is the authored spelling, not the caller's.
                resolvedVariantId = themeSet.GetVariant(variantId).Id;
            }
            else
            {
                error = $"Theme set '{themeSet.DisplayName}' has no variant '{variantId}'. Available: "
                        + $"{string.Join(", ", variantIds)}.";
                return false;
            }

            var outcome = ColorThemeResolver.TryResolve(themeSet, resolvedVariantId, 0, out var theme,
                out var diagnostics);

            if (outcome != ColorThemeActivation.Activated)
            {
                error = $"Variant '{resolvedVariantId}' did not resolve ({outcome}): "
                        + string.Join("; ", diagnostics);
                resolvedVariantId = null;
                return false;
            }

            provider = new LegacyColorProviderAdapter(themeSet, theme);
            return true;
        }
    }
}
