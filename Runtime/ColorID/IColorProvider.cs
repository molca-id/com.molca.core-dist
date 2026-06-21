using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// Instance lookup API for a color palette. Implemented by <see cref="ColorModule"/>;
    /// obtain the active provider via <see cref="IColorSchemeService.ActiveScheme"/>.
    /// Replaces the legacy static lookup surface on <see cref="ColorModule"/>,
    /// which remains as obsolete shims.
    /// </summary>
    public interface IColorProvider
    {
        /// <summary>
        /// Gets a color by ID, searching the default swatch first and then all swatches
        /// in list order. Also accepts the composite format <c>"SwatchName.ColorId"</c>.
        /// </summary>
        /// <param name="colorId">The color ID, or composite <c>"SwatchName.ColorId"</c>.</param>
        /// <returns>The color, or magenta if not found.</returns>
        Color GetColor(string colorId);

        /// <summary>Gets a color by swatch name and color ID.</summary>
        /// <param name="swatchName">The name of the swatch.</param>
        /// <param name="colorId">The ID of the color to retrieve.</param>
        /// <returns>The color, or magenta if not found.</returns>
        Color GetColor(string swatchName, string colorId);

        /// <summary>Gets a color by ID with the given alpha applied.</summary>
        /// <param name="colorId">The color ID, or composite <c>"SwatchName.ColorId"</c>.</param>
        /// <param name="alpha">The alpha value to apply.</param>
        /// <returns>The color with the specified alpha, or magenta if not found.</returns>
        Color GetColor(string colorId, float alpha);

        /// <summary>Gets a color by swatch name and color ID with the given alpha applied.</summary>
        /// <param name="swatchName">The name of the swatch.</param>
        /// <param name="colorId">The ID of the color to retrieve.</param>
        /// <param name="alpha">The alpha value to apply.</param>
        /// <returns>The color with the specified alpha, or magenta if not found.</returns>
        Color GetColor(string swatchName, string colorId, float alpha);

        /// <summary>
        /// Checks whether a color ID exists, searching the default swatch first and then
        /// all swatches in list order. Also accepts the composite format.
        /// </summary>
        /// <param name="colorId">The color ID, or composite <c>"SwatchName.ColorId"</c>.</param>
        /// <returns>True if the color ID exists.</returns>
        bool HasColor(string colorId);

        /// <summary>Checks whether a color ID exists in a specific swatch.</summary>
        /// <param name="swatchName">The swatch name.</param>
        /// <param name="colorId">The ID to check.</param>
        /// <returns>True if the color ID exists in the swatch.</returns>
        bool HasColor(string swatchName, string colorId);

        /// <summary>Gets all available color IDs in composite format <c>"SwatchName.ColorId"</c>.</summary>
        /// <returns>Array of all color IDs.</returns>
        string[] GetAllColorIds();

        /// <summary>Gets all color IDs in a specific swatch.</summary>
        /// <param name="swatchName">The swatch name.</param>
        /// <returns>Array of color IDs in the swatch.</returns>
        string[] GetColorIdsInSwatch(string swatchName);

        /// <summary>Gets all swatch names.</summary>
        /// <returns>Array of swatch names.</returns>
        string[] GetSwatchNames();
    }
}
