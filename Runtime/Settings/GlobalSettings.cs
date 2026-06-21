using Molca.Attributes;
using Molca.Settings;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Global Settings", menuName = "Molca/Core/Global Settings", order = 0)]
    public class GlobalSettings : ScriptableObject
    {
        public static GlobalSettings main => MolcaProjectSettings.Instance.GlobalSettings;

        public SettingModule[] modules;

        private Dictionary<Type, SettingModule> _moduleCache;
        
        public Action<int> onQualityChanged;
        private const string PREF_QUALITY = "QUALITY";

        public void Initialize()
        {
            _moduleCache = new Dictionary<Type, SettingModule>();

            // Initialize all modules
            foreach (var module in modules)
            {
                if (module == null) continue;

                module.Initialize();
                module.State = module.CreateState();
                _moduleCache[module.GetType()] = module;
            }

            // Deferred a frame (historical behavior); explicit fire-and-forget per
            // the async contract — the callee owns its exceptions.
            _ = ApplyPersistedQualityAsync();
        }

        private async Awaitable ApplyPersistedQualityAsync()
        {
            try
            {
                await Awaitable.NextFrameAsync();
                if (PlayerPrefs.HasKey(PREF_QUALITY))
                    QualitySettings.SetQualityLevel(PlayerPrefs.GetInt(PREF_QUALITY, 2));
            }
            catch (Exception e)
            {
                Debug.LogError($"[GlobalSettings] Failed to apply persisted quality level: {e}");
            }
        }

        public void DeInitialize()
        {
            _moduleCache.Clear();
            _moduleCache = null;
        }

        public static T GetModule<T>() where T : SettingModule
        {
            // Guard against an unconfigured project: main (GlobalSettings.main) is null when no
            // GlobalSettings is assigned, and modules is null before Initialize() runs.
            if (main == null)
                return null;

            if (main._moduleCache != null)
            {
                if (main._moduleCache.TryGetValue(typeof(T), out var module))
                    return (T)module;
            }
            else if (main.modules != null)
            {
                foreach (var module in main.modules)
                {
                    if (module is T typedModule)
                        return typedModule;
                }
            }
            return null;
        }

        public void SaveAllSettings()
        {
            Debug.Log("Saving all settings");
            foreach (var module in modules)
            {
                if (module != null)
                    module.SaveSettings();
            }
            PlayerPrefs.Save();
            Debug.Log("Settings saved");
        }

        public void LoadAllSettings()
        {
            foreach (var module in modules)
            {
                if (module != null)
                    module.LoadSettings();
            }
        }

        public static int Quality => QualitySettings.GetQualityLevel();
        
        public static async Awaitable SetQuality(int value)
        {
            main.onQualityChanged?.Invoke(value);
            await Awaitable.WaitForSecondsAsync(.1f);
            QualitySettings.SetQualityLevel(value);
            PlayerPrefs.SetInt(PREF_QUALITY, value);
        }

        #if UNITY_EDITOR
        public static GlobalSettings GetOrCreateSettings()
        {
            var settings = MolcaProjectSettings.Instance.GlobalSettings;
            if (settings == null)
            {
                settings = CreateInstance<GlobalSettings>();
                if (!System.IO.Directory.Exists("Assets/_Molca/Resources"))
                    System.IO.Directory.CreateDirectory("Assets/_Molca/Resources");
                UnityEditor.AssetDatabase.CreateAsset(settings, "Assets/_Molca/Resources/GlobalSettings.asset");
                UnityEditor.AssetDatabase.SaveAssets();
                MolcaProjectSettings.Instance.GlobalSettings = settings;
            }
            return settings;
        }
        #endif
    }
}