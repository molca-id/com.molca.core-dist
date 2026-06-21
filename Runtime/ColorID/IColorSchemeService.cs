using System;

namespace Molca.ColorID
{
    /// <summary>
    /// Instance API of the color-scheme subsystem. Resolve via
    /// <c>RuntimeManager.GetService&lt;IColorSchemeService&gt;()</c> or inject with
    /// <c>[Inject] IColorSchemeService</c>. Replaces the legacy static surface on
    /// <see cref="ColorSchemeManager"/>, which remains as obsolete shims.
    /// </summary>
    public interface IColorSchemeService
    {
        /// <summary>The currently active color scheme, or <c>null</c> if none is set.</summary>
        ColorModule ActiveScheme { get; }

        /// <summary>Index of the currently active scheme, or -1 if none is set.</summary>
        int ActiveSchemeIndex { get; }

        /// <summary>Display names of all configured color schemes.</summary>
        string[] SchemeNames { get; }

        /// <summary>Number of configured color schemes.</summary>
        int SchemeCount { get; }

        /// <summary>Raised when the active color scheme changes; passes the new active <see cref="ColorModule"/>.</summary>
        event Action<ColorModule> SchemeChanged;

        /// <summary>Activates the scheme at the given index.</summary>
        /// <param name="schemeIndex">Index of the scheme to activate.</param>
        /// <param name="save">Whether to persist the preference.</param>
        void SetScheme(int schemeIndex, bool save = true);

        /// <summary>Activates the scheme with the given asset name.</summary>
        /// <param name="schemeName">Name of the scheme (ColorModule asset name) to activate.</param>
        /// <param name="save">Whether to persist the preference.</param>
        void SetScheme(string schemeName, bool save = true);

        /// <summary>Cycles to the next scheme. Useful for simple Light/Dark toggling.</summary>
        /// <param name="save">Whether to persist the preference.</param>
        void ToggleScheme(bool save = true);

        /// <summary>Moves to the next scheme cyclically.</summary>
        /// <param name="save">Whether to persist the preference.</param>
        void NextScheme(bool save = true);

        /// <summary>Moves to the previous scheme cyclically.</summary>
        /// <param name="save">Whether to persist the preference.</param>
        void PreviousScheme(bool save = true);

        /// <summary>
        /// Forces a refresh of all <see cref="ColorID"/> components with the current scheme.
        /// Useful after dynamic changes or scene loads.
        /// </summary>
        void RefreshAllColorIDs();

        /// <summary>Gets a scheme by index without activating it.</summary>
        /// <param name="index">Index of the scheme.</param>
        /// <returns>The <see cref="ColorModule"/> at the index, or <c>null</c> if invalid.</returns>
        ColorModule GetScheme(int index);

        /// <summary>Gets a scheme by name without activating it.</summary>
        /// <param name="schemeName">Name of the scheme.</param>
        /// <returns>The <see cref="ColorModule"/> with the given name, or <c>null</c> if not found.</returns>
        ColorModule GetScheme(string schemeName);
    }
}
