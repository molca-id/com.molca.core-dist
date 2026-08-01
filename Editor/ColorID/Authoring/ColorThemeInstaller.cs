#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Molca.ColorID;
using Molca.Settings;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Installs the V2 colour-theme path into a project by adding a
    /// <see cref="ColorThemeSettings"/> module to its <c>GlobalSettings</c>.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Authoring/</c>.
    /// <b>Registration:</b> <c>[MenuItem]</c>. Also called by Quick Setup and the onboarding wizard once
    /// those adopt V2.
    /// <para/>
    /// <b>This is the switch.</b> <see cref="ColorSchemeManager"/> chooses its generation purely from
    /// configuration: with a <see cref="ColorThemeSettings"/> module carrying a theme set it runs V2, and
    /// without one it runs the legacy <see cref="ColorModule"/> array. Installing the module therefore
    /// moves every shipped <see cref="ColorID"/> component onto the token resolution path in one step,
    /// via the theme set's legacy alias map.
    /// <para/>
    /// <b>What is deliberately left alone.</b> The existing <see cref="ColorModule"/> palettes stay in the
    /// module list and the Runtime Manager prefab keeps its <c>Available Schemes</c> references. Both are
    /// inert in V2 — <see cref="ColorSchemeManager"/> never reads them once the theme settings module is
    /// present — and leaving them makes the switch a one-line revert instead of a data migration. Removing
    /// them belongs to the deprecation phase, once the alias map has been proven against real content.
    /// </remarks>
    public static class ColorThemeInstaller
    {
        /// <summary>Where the generated settings module asset is written.</summary>
        public const string SettingsAssetPath =
            "Assets/_MolcaSDK/Settings/Global/Color Theme Settings.asset";

        /// <summary>Creates the theme settings module and registers it with the project's GlobalSettings.</summary>
        [MenuItem("Molca/ColorID/Install Color Theme Settings (V1 → V2)", priority = 42)]
        public static void Install()
        {
            var themeSet = AssetDatabase.LoadAssetAtPath<ColorThemeSet>(ColorThemeSetBootstrap.AssetPath);
            if (themeSet == null)
            {
                // Generating it here rather than failing: an installer that tells the author to go run
                // another menu item first is just a worse installer.
                ColorThemeSetBootstrap.CreateOrUpdate();
                themeSet = AssetDatabase.LoadAssetAtPath<ColorThemeSet>(ColorThemeSetBootstrap.AssetPath);
            }

            if (themeSet == null)
            {
                Debug.LogError("[ColorTheme] Install aborted: no theme set could be created at "
                               + $"'{ColorThemeSetBootstrap.AssetPath}'.");
                return;
            }

            var errors = new List<string>();
            if (!themeSet.Validate(errors))
            {
                // Installing an invalid set would put the project into the degraded emergency fallback on
                // its next launch, which is strictly worse than staying on V1.
                Debug.LogError($"[ColorTheme] Install aborted: '{themeSet.name}' does not validate.\n  "
                               + string.Join("\n  ", errors));
                return;
            }

            var globalSettings = MolcaProjectSettings.Instance?.GlobalSettings;
            if (globalSettings == null)
            {
                Debug.LogError("[ColorTheme] Install aborted: this project has no GlobalSettings assigned "
                               + "on MolcaProjectSettings.");
                return;
            }

            var settings = LoadOrCreateSettings(themeSet);
            bool added = AddToModules(globalSettings, settings);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ColorTheme] V2 installed. '{settings.name}' "
                      + (added ? "added to" : "already present in") + $" '{globalSettings.name}'.\n"
                      + $"  Theme set: {themeSet.DisplayName} ({themeSet.StableSetId})\n"
                      + $"  Default variant: {settings.DefaultVariantId}\n"
                      + $"  Variants: {string.Join(", ", themeSet.GetVariantIds())}\n"
                      + $"  Legacy aliases: {themeSet.LegacyAliases.Count}\n"
                      + "  ColorSchemeManager now resolves through the theme set; the ColorModule "
                      + "palettes remain in the module list but are inert. Run the ColorID audit "
                      + "(Doctor → color-theme-audit) to see which authored (swatch, colorId) pairs "
                      + "the alias map does not cover.");
        }

        /// <summary>Reports whether V2 is installed, without changing anything.</summary>
        [MenuItem("Molca/ColorID/Report Colour Theme Installation", priority = 43)]
        public static void Report()
        {
            var globalSettings = MolcaProjectSettings.Instance?.GlobalSettings;
            if (globalSettings == null)
            {
                Debug.LogWarning("[ColorTheme] No GlobalSettings is assigned on MolcaProjectSettings.");
                return;
            }

            var installed = FindModule<ColorThemeSettings>(globalSettings);
            int palettes = CountModules<ColorModule>(globalSettings);

            if (installed == null)
            {
                Debug.Log($"[ColorTheme] '{globalSettings.name}' is on the legacy V1 path: no "
                          + $"ColorThemeSettings module, {palettes} ColorModule palette(s).");
                return;
            }

            Debug.Log($"[ColorTheme] '{globalSettings.name}' is on the V2 path.\n"
                      + $"  Theme set: {(installed.ThemeSet != null ? installed.ThemeSet.StableSetId : "<none — falls back to V1>")}\n"
                      + $"  Default variant: {installed.DefaultVariantId ?? "<none>"}\n"
                      + $"  Runtime switching: {installed.AllowRuntimeSwitching}\n"
                      + $"  Persistence: {installed.PersistencePolicy}\n"
                      + $"  Inert ColorModule palettes still listed: {palettes}");
        }

        /// <summary>
        /// Loads the settings asset, creating it when absent, and points it at the theme set.
        /// </summary>
        /// <param name="themeSet">The theme set to install.</param>
        /// <returns>The settings asset, saved and pointing at <paramref name="themeSet"/>.</returns>
        /// <remarks>
        /// Written through <see cref="SerializedObject"/> rather than reflection: these are plain
        /// serialized fields on an editable project asset, so the normal editor path applies. The
        /// reflection in <c>ColorThemeSetEditing</c> exists only because the theme <i>set</i> deliberately
        /// exposes no mutators.
        /// <para/>
        /// The default variant is only filled in when blank, so re-running the installer never overrides
        /// an author's choice.
        /// </remarks>
        private static ColorThemeSettings LoadOrCreateSettings(ColorThemeSet themeSet)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsAssetPath));

            var settings = AssetDatabase.LoadAssetAtPath<ColorThemeSettings>(SettingsAssetPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<ColorThemeSettings>();
                AssetDatabase.CreateAsset(settings, SettingsAssetPath);
            }

            var serialized = new SerializedObject(settings);
            serialized.FindProperty("_themeSet").objectReferenceValue = themeSet;

            var defaultVariant = serialized.FindProperty("_defaultVariantId");
            if (string.IsNullOrEmpty(defaultVariant.stringValue))
            {
                var ids = themeSet.GetVariantIds();
                defaultVariant.stringValue = ids.Length > 0 ? ids[0] : string.Empty;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            return settings;
        }

        /// <summary>Appends a module to GlobalSettings unless one of its type is already listed.</summary>
        /// <param name="globalSettings">The project's settings graph.</param>
        /// <param name="module">The module to register.</param>
        /// <returns><c>true</c> when the list was changed.</returns>
        /// <remarks>
        /// Idempotent by type, not by reference: <c>GlobalSettings.GetModule&lt;T&gt;</c> resolves by type
        /// and would return whichever came first, so two <see cref="ColorThemeSettings"/> assets in one
        /// list is an ambiguity rather than a duplicate. If a different asset of the same type is already
        /// installed, that one is left in place and the caller is told.
        /// </remarks>
        private static bool AddToModules(GlobalSettings globalSettings, SettingModule module)
        {
            var existing = FindModule<ColorThemeSettings>(globalSettings);
            if (existing != null)
            {
                if (existing != module)
                {
                    Debug.LogWarning($"[ColorTheme] '{globalSettings.name}' already lists a different "
                                     + $"ColorThemeSettings ('{existing.name}'); it was left in place and "
                                     + $"'{module.name}' was not added.");
                }
                return false;
            }

            var serialized = new SerializedObject(globalSettings);
            var modules = serialized.FindProperty("modules");
            int index = modules.arraySize;
            modules.InsertArrayElementAtIndex(index);
            modules.GetArrayElementAtIndex(index).objectReferenceValue = module;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(globalSettings);
            return true;
        }

        /// <summary>
        /// Finds the first module of a type by reading the serialized array directly.
        /// </summary>
        /// <remarks>
        /// Not <c>GlobalSettings.GetModule&lt;T&gt;</c>: that reads a cache built by
        /// <c>Initialize()</c>, which has never run in the editor, and its uninitialized path depends on
        /// <c>GlobalSettings.main</c> resolving to the same asset. Reading the array is unambiguous.
        /// </remarks>
        private static T FindModule<T>(GlobalSettings globalSettings) where T : SettingModule
        {
            if (globalSettings.modules == null) return null;
            foreach (var module in globalSettings.modules)
            {
                if (module is T typed) return typed;
            }
            return null;
        }

        private static int CountModules<T>(GlobalSettings globalSettings) where T : SettingModule
        {
            if (globalSettings.modules == null) return 0;

            int count = 0;
            foreach (var module in globalSettings.modules)
            {
                if (module is T) count++;
            }
            return count;
        }
    }
}
#endif
