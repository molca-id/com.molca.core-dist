using System.Collections.Generic;
using Molca.Settings;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Settings
{
    /// <summary>
    /// Editor-time validator for bootstrap-critical assets (<see cref="MolcaProjectSettings"/>
    /// and <see cref="GlobalSettings"/>).
    /// </summary>
    /// <remarks>
    /// Runs once per domain reload. Surfaces misconfigurations as <see cref="Debug.LogError"/>
    /// entries in the console immediately, rather than as a <see cref="System.NullReferenceException"/>
    /// during play-mode bootstrap.
    /// <para>
    /// Validations performed:
    /// <list type="bullet">
    /// <item><see cref="MolcaProjectSettings"/> asset is loadable.</item>
    /// <item><see cref="MolcaProjectSettings.RuntimeManager"/> prefab reference is non-null.</item>
    /// <item><see cref="MolcaProjectSettings.GlobalSettings"/> reference is non-null.</item>
    /// <item>Every entry in <see cref="GlobalSettings.modules"/> is non-null.</item>
    /// <item>No duplicate <see cref="SettingModule"/> concrete types in <see cref="GlobalSettings.modules"/>.</item>
    /// <item>Every entry in <see cref="MolcaProjectSettings.BootstrapExtensions"/> is non-null.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static class BootstrapAssetValidator
    {
        private const string LogPrefix = "[BootstrapAssetValidator]";

        [InitializeOnLoadMethod]
        private static void RegisterValidator()
        {
            // Defer until after the AssetDatabase is fully ready post-reload.
            EditorApplication.delayCall += Validate;
        }

        private static void Validate()
        {
            var projectSettings = MolcaProjectSettings.Instance;
            if (projectSettings == null)
            {
                Debug.LogError($"{LogPrefix} MolcaProjectSettings asset could not be loaded. " +
                               "Bootstrap will fail at runtime.");
                return;
            }

            if (projectSettings.RuntimeManager == null)
            {
                Debug.LogError($"{LogPrefix} MolcaProjectSettings.RuntimeManager prefab reference is null. " +
                               "Assign a RuntimeManager prefab on the project settings asset. If you're on an SDK " +
                               "layer (e.g. com.molca.sdk), run Molca > SDK > Quick Setup > Install Starter Settings " +
                               "first — it seeds this along with the rest of the starter config.", projectSettings);
            }

            var globalSettings = projectSettings.GlobalSettings;
            if (globalSettings == null)
            {
                Debug.LogError($"{LogPrefix} MolcaProjectSettings.GlobalSettings reference is null. " +
                               "Assign a GlobalSettings asset on the project settings asset. If you're on an SDK " +
                               "layer (e.g. com.molca.sdk), run Molca > SDK > Quick Setup > Install Starter Settings " +
                               "to seed a starter GlobalSettings under Assets/_MolcaSDK/Settings/ — the shipped " +
                               "project-settings template already points at it once that command has run.", projectSettings);
            }
            else
            {
                ValidateModules(globalSettings);
            }

            ValidateBootstrapExtensions(projectSettings);
        }

        private static void ValidateModules(GlobalSettings globalSettings)
        {
            var modules = globalSettings.modules;
            if (modules == null || modules.Length == 0) return;

            var seenTypes = new HashSet<System.Type>();
            for (int i = 0; i < modules.Length; i++)
            {
                var module = modules[i];
                if (module == null)
                {
                    Debug.LogError($"{LogPrefix} GlobalSettings.modules[{i}] is null. Remove or assign the entry.",
                                   globalSettings);
                    continue;
                }

                var moduleType = module.GetType();
                if (!seenTypes.Add(moduleType))
                {
                    Debug.LogError($"{LogPrefix} Duplicate SettingModule type '{moduleType.Name}' in " +
                                   $"GlobalSettings.modules at index {i}. Each SettingModule subtype must appear at most once.",
                                   globalSettings);
                }
            }
        }

        private static void ValidateBootstrapExtensions(MolcaProjectSettings projectSettings)
        {
            var extensions = projectSettings.BootstrapExtensions;
            if (extensions == null || extensions.Count == 0) return;

            for (int i = 0; i < extensions.Count; i++)
            {
                if (extensions[i] == null)
                {
                    Debug.LogError($"{LogPrefix} MolcaProjectSettings.BootstrapExtensions[{i}] is null. " +
                                   "Remove or assign the entry.", projectSettings);
                }
            }
        }
    }
}
