using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Settings;
using UnityEditor;

namespace Molca.ColorID.Editor.Upgrade
{
    /// <summary>The read-only prerequisite check shared by the colour upgrade detectors.</summary>
    /// <remarks>
    /// A stock theme must never be created by an audit or blanket repair. A consumer may have customized
    /// the 1.x palettes, so choosing V2 values is an authoring decision even though applying an already
    /// chosen alias map is deterministic.
    /// </remarks>
    public static class ColorThemeUpgradeReadiness
    {
        /// <summary>Inspects the existing theme and settings graph without creating or moving assets.</summary>
        public static ColorThemeUpgradeReadinessResult Evaluate()
        {
            var paths = AssetDatabase.FindAssets($"t:{nameof(ColorThemeSet)}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct()
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            var themes = paths.Select(AssetDatabase.LoadAssetAtPath<ColorThemeSet>)
                .Where(theme => theme != null)
                .ToArray();

            var project = MolcaProjectSettings.LoadExistingAssetWithoutMigration();
            var modules = project?.GlobalSettings?.modules ?? Array.Empty<SettingModule>();
            var settings = modules.OfType<ColorThemeSettings>().ToArray();
            return Assess(themes, settings, paths);
        }

        /// <summary>Pure decision seam used to pin missing, ambiguous and valid configurations.</summary>
        internal static ColorThemeUpgradeReadinessResult Assess(
            IReadOnlyList<ColorThemeSet> themes,
            IReadOnlyList<ColorThemeSettings> settings,
            IReadOnlyList<string> paths = null)
        {
            themes = themes ?? Array.Empty<ColorThemeSet>();
            settings = settings ?? Array.Empty<ColorThemeSettings>();
            paths = paths ?? Array.Empty<string>();

            if (themes.Count == 0)
                return Blocked("No ColorThemeSet is installed. Review the 1.x palette values in the "
                               + "Themes workspace, then create and validate one V2 theme before applying "
                               + "the colour migrations.");

            if (themes.Count > 1)
                return Blocked($"The project contains {themes.Count} ColorThemeSet assets. Consolidate or "
                               + "choose one before migration; the upgrade will not guess which colour "
                               + "contract owns the legacy aliases.", paths);

            var theme = themes[0];
            var validationErrors = new List<string>();
            if (!theme.Validate(validationErrors))
                return Blocked($"The sole ColorThemeSet does not validate: "
                               + string.Join("; ", validationErrors.Take(3)), paths);

            if (settings.Count != 1)
                return Blocked(settings.Count == 0
                    ? "The project has no installed ColorThemeSettings module. Assign the validated theme "
                      + "in the Themes workspace before migrating legacy colour content."
                    : $"The project has {settings.Count} installed ColorThemeSettings modules. Keep one "
                      + "authoritative module before migrating legacy colour content.", paths);

            if (settings[0] == null || settings[0].ThemeSet != theme)
                return Blocked("The installed ColorThemeSettings module does not reference the sole "
                               + "ColorThemeSet. Resolve that configuration in the Themes workspace before "
                               + "migrating legacy colour content.", paths);

            return new ColorThemeUpgradeReadinessResult(true, theme, null, paths);
        }

        private static ColorThemeUpgradeReadinessResult Blocked(
            string message, IReadOnlyList<string> paths = null) =>
            new ColorThemeUpgradeReadinessResult(false, null, message,
                paths ?? Array.Empty<string>());
    }

    /// <summary>Immutable result of the read-only colour upgrade prerequisite check.</summary>
    public sealed class ColorThemeUpgradeReadinessResult
    {
        internal ColorThemeUpgradeReadinessResult(bool isReady, ColorThemeSet themeSet, string message,
            IReadOnlyList<string> locations)
        {
            IsReady = isReady;
            ThemeSet = themeSet;
            Message = message;
            Locations = locations ?? Array.Empty<string>();
        }

        /// <summary>Whether colour migrations can safely use the project's chosen alias map.</summary>
        public bool IsReady { get; }

        /// <summary>The sole installed and configured theme; null when blocked.</summary>
        public ColorThemeSet ThemeSet { get; }

        /// <summary>Why migration is blocked; null when ready.</summary>
        public string Message { get; }

        /// <summary>Relevant theme asset paths, particularly for ambiguity.</summary>
        public IReadOnlyList<string> Locations { get; }
    }
}
