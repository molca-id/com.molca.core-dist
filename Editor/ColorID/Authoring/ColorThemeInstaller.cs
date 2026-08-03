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
    /// <b>This supplies the required runtime configuration.</b> <see cref="ColorSchemeManager"/> resolves
    /// canonical tokens only when a <see cref="ColorThemeSettings"/> module carries a theme set. Projects
    /// upgrading from 1.x install this configuration before the unified upgrade repair rewrites content.
    /// <para/>
    /// Existing legacy pairs remain readable by editor-only migrators through the theme set's alias map;
    /// no v1 runtime type is required.
    /// </remarks>
    public static class ColorThemeInstaller
    {
        /// <summary>File name of the generated settings module asset.</summary>
        public const string SettingsAssetFileName = "Color Theme Settings.asset";

        /// <summary>Creates the theme settings module and registers it with the project's GlobalSettings.</summary>
        [MenuItem("Molca/ColorID/Install Color Theme Settings (V1 → V2)", priority = 42)]
        public static void Install()
        {
            var themeSetPath = ColorThemeSetBootstrap.ResolveAssetPath(out var setAmbiguity);
            if (themeSetPath == null)
            {
                Debug.LogError($"[ColorTheme] Install aborted: {setAmbiguity}");
                return;
            }

            var themeSet = AssetDatabase.LoadAssetAtPath<ColorThemeSet>(themeSetPath);
            if (themeSet == null)
            {
                // Generating it here rather than failing: an installer that tells the author to go run
                // another menu item first is just a worse installer.
                ColorThemeSetBootstrap.CreateOrUpdate();
                themeSetPath = ColorThemeSetBootstrap.ResolveAssetPath(out _);
                themeSet = themeSetPath == null
                    ? null
                    : AssetDatabase.LoadAssetAtPath<ColorThemeSet>(themeSetPath);
            }

            if (themeSet == null)
            {
                Debug.LogError("[ColorTheme] Install aborted: no theme set could be found or created.");
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
            if (settings == null) return; // Ambiguous project layout; the locator already said which.

            bool added = AddToModules(globalSettings, settings);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ColorTheme] V2 installed. '{settings.name}' "
                      + (added ? "added to" : "already present in") + $" '{globalSettings.name}'.\n"
                      + $"  Theme set: {themeSet.DisplayName} ({themeSet.StableSetId})\n"
                      + $"  Default variant: {settings.DefaultVariantId}\n"
                      + $"  Variants: {string.Join(", ", themeSet.GetVariantIds())}\n"
                      + $"  Legacy aliases: {themeSet.LegacyAliases.Count}\n"
                      + "  ColorSchemeManager now resolves through this theme set.");
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

            if (installed == null)
            {
                Debug.LogError($"[ColorTheme] '{globalSettings.name}' has no ColorThemeSettings module. "
                               + "Install a colour theme before releasing with Core 2.0.");
                return;
            }

            Debug.Log($"[ColorTheme] '{globalSettings.name}' is on the V2 path.\n"
                      + $"  Theme set: {(installed.ThemeSet != null ? installed.ThemeSet.StableSetId : "<none — runtime theme unavailable>")}\n"
                      + $"  Default variant: {installed.DefaultVariantId ?? "<none>"}\n"
                      + $"  Runtime switching: {installed.AllowRuntimeSwitching}\n"
                      + $"  Persistence: {installed.PersistencePolicy}");
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
            // Located by type, so the branded asset a consumer imported with the Starter Project Content
            // sample is configured rather than shadowed by a fresh blank one at a path only this
            // repository uses.
            var settingsPath = ColorThemeAssetLocator.ResolveOrDefault<ColorThemeSettings>(
                SettingsAssetFileName, out var ambiguity);
            if (settingsPath == null)
            {
                Debug.LogError($"[ColorTheme] {ambiguity}");
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));

            var settings = AssetDatabase.LoadAssetAtPath<ColorThemeSettings>(settingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<ColorThemeSettings>();
                AssetDatabase.CreateAsset(settings, settingsPath);
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

    }
}
#endif
