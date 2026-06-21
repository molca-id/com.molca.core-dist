using UnityEngine;
using System;
using System.Collections.Generic;
using Molca.Settings.Notification;

namespace Molca.Settings
{
    /// <summary>
    /// Central notification system manager.
    /// Similar to GlobalSettings pattern - holds all notification providers and manages them.
    /// Add custom notification providers to the providers array to extend the system.
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Notification Settings", menuName = "Molca/Editor/Notification Settings", order = 110)]
    public class NotificationSettings : ScriptableObject
    {
        [SerializeField] private NotificationProvider[] providers;

        private Dictionary<Type, NotificationProvider> _providerCache;

        /// <summary>
        /// Initialize all notification providers
        /// </summary>
        public void Initialize()
        {
            _providerCache = new Dictionary<Type, NotificationProvider>();

            if (providers == null) return;

            foreach (var provider in providers)
            {
                if (provider == null) continue;

                provider.Initialize();
                _providerCache[provider.GetType()] = provider;
            }

            Debug.Log($"Notification System: Initialized {_providerCache.Count} notification providers");
        }

        /// <summary>
        /// Get a specific notification provider by type
        /// </summary>
        public T GetProvider<T>() where T : NotificationProvider
        {
            if (_providerCache != null)
            {
                if (_providerCache.TryGetValue(typeof(T), out var provider))
                    return (T)provider;
            }
            else if (providers != null)
            {
                foreach (var provider in providers)
                {
                    if (provider is T)
                        return (T)provider;
                }
            }
            return null;
        }

        /// <summary>
        /// Get all enabled notification providers
        /// </summary>
        public List<NotificationProvider> GetEnabledProviders()
        {
            var enabledProviders = new List<NotificationProvider>();

            if (providers == null) return enabledProviders;

            foreach (var provider in providers)
            {
                if (provider != null && provider.IsEnabled)
                {
                    enabledProviders.Add(provider);
                }
            }

            return enabledProviders;
        }

        /// <summary>
        /// Send a notification through all enabled providers
        /// </summary>
        public void BroadcastNotification(NotificationData notification, Action<bool> onCompleted = null)
        {
            var enabledProviders = GetEnabledProviders();

            if (enabledProviders.Count == 0)
            {
                Debug.LogWarning("No enabled notification providers found");
                onCompleted?.Invoke(false);
                return;
            }

            int completedCount = 0;
            bool anySuccess = false;

            foreach (var provider in enabledProviders)
            {
                provider.SendNotification(notification, (success) =>
                {
                    completedCount++;
                    if (success) anySuccess = true;

                    if (completedCount == enabledProviders.Count)
                    {
                        onCompleted?.Invoke(anySuccess);
                    }
                });
            }
        }

        /// <summary>
        /// Send a notification through a specific provider type
        /// </summary>
        public void SendNotification<T>(NotificationData notification, Action<bool> onCompleted = null) where T : NotificationProvider
        {
            var provider = GetProvider<T>();
            if (provider == null)
            {
                Debug.LogWarning($"Notification provider of type {typeof(T).Name} not found");
                onCompleted?.Invoke(false);
                return;
            }

            if (!provider.IsEnabled)
            {
                Debug.LogWarning($"Notification provider {typeof(T).Name} is not enabled");
                onCompleted?.Invoke(false);
                return;
            }

            provider.SendNotification(notification, onCompleted);
        }

        /// <summary>
        /// Validate all provider configurations
        /// </summary>
        public bool ValidateAllProviders()
        {
            if (providers == null) return false;

            bool allValid = true;
            foreach (var provider in providers)
            {
                if (provider != null && !provider.ValidateConfiguration())
                {
                    Debug.LogWarning($"Provider {provider.GetType().Name} validation failed: {provider.GetStatusMessage()}");
                    allValid = false;
                }
            }

            return allValid;
        }

        /// <summary>
        /// Get or create notification settings instance
        /// </summary>
        public static NotificationSettings GetOrCreateSettings()
            => Molca.Editor.MolcaEditorSettingsAsset.GetOrCreate<NotificationSettings>("NotificationSettings.asset");
    }
}
