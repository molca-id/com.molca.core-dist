using Molca.Settings;
using UnityEngine;
using UnityEngine.Serialization;
using System.Collections.Generic;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Configuration for the unified data management system
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    [CreateAssetMenu(fileName = "DataConfig", menuName = "Molca/Networking/DataConfig", order = 20)]
    public class DataConfig : SettingModule
    {
        [Header("Cache Settings")]
        [SerializeField, FormerlySerializedAs("defaultCacheDuration")] private float _defaultCacheDuration = 300f; // 5 minutes
        [SerializeField, FormerlySerializedAs("defaultMaxCacheSize")] private int _defaultMaxCacheSize = 1024 * 1024 * 50; // 50MB
        
        [Header("Logging")]
        [SerializeField, FormerlySerializedAs("logDataOperations")] private bool _logDataOperations = true;
        [SerializeField, FormerlySerializedAs("enableDataMappingDebugLogs")] private bool _enableDataMappingDebugLogs = false;
        
        [Header("Auto-Registered Providers")]
        [Tooltip("Drag DataProvider assets here to auto-register them when DataManager initializes")]
        [SerializeField, FormerlySerializedAs("autoRegisterProviders")] private List<DataProvider> _autoRegisterProviders = new List<DataProvider>();
        
        // Properties
        public float DefaultCacheDuration => _defaultCacheDuration;
        public int DefaultMaxCacheSize => _defaultMaxCacheSize;
        public bool LogDataOperations => _logDataOperations;
        public bool EnableDataMappingDebugLogs => _enableDataMappingDebugLogs;
        public IReadOnlyList<DataProvider> AlwaysIncludedProviders => _autoRegisterProviders.AsReadOnly();
        
        /// <summary>
        /// Checks if a provider is in the always-included list
        /// </summary>
        public bool IsAlwaysIncludedProvider(DataProvider provider)
        {
            return provider != null && _autoRegisterProviders.Contains(provider);
        }

        /// <summary>
        /// Applies the current configuration settings to the system
        /// </summary>
        public void ApplySettings()
        {
            // Apply DataMappingParser debug logging setting
            DataMappingParser.EnableDebugLogging = _enableDataMappingDebugLogs;
        }

        /// <summary>
        /// Called when the asset is loaded to apply initial settings
        /// </summary>
        private void OnEnable()
        {
            // Apply settings when the asset is enabled
            ApplySettings();
        }

        public override void SaveSettings() {}

        public override void LoadSettings() {}
    }
}
