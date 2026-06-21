using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Molca.Events;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Central data management system that acts as a unified gateway for data from different sources.
    /// External classes access data through this manager, which handles caching, validation, and provider coordination.
    /// </summary>
    /// <remarks>
    /// Sprint 40: the implementation is decomposed into internal collaborators —
    /// <see cref="DataProviderRegistry"/> (provider lifecycle + caches),
    /// <see cref="DataSubscriptionHub"/> (subscribe/notify), and
    /// <see cref="DataPoolFlusher"/> (batching + the async flush loop). This type stays the
    /// public singleton facade and delegates to them; every existing member/behavior is
    /// preserved, and the Phase-1 thread-safety locks live inside the collaborators.
    /// </remarks>
    public class DataManager : RuntimeSubsystem
    {
        public static DataManager Instance => RuntimeManager.GetSubsystem<DataManager>();

        // Events for data changes
        public static event Action<string, ImmutableData> OnDataUpdated;
        public static event Action<string> OnDataProviderRegistered;
        public static event Action<string> OnDataProviderUnregistered;

        [SerializeField] private float _poolFlushInterval = 0.1f; // Flush pool every 100ms

        // Configuration
        private DataConfig _config;
        public DataConfig Config => _config;

        // Internal collaborators (Sprint 40 decomposition).
        private DataProviderRegistry _registry;
        private DataSubscriptionHub _hub;
        private DataPoolFlusher _flusher;

        public override void Initialize(Action<IRuntimeSubsystem> callback)
        {
            _config = GlobalSettings.GetModule<DataConfig>();

            _hub = new DataSubscriptionHub(() => _config != null && _config.LogDataOperations);
            _flusher = new DataPoolFlusher(_poolFlushInterval, _hub, () => _config != null && _config.LogDataOperations);
            _registry = new DataProviderRegistry(_config, OnCacheDataAdded);

            // Relay collaborator lifecycle onto the public static events.
            _registry.ProviderRegistered += id =>
            {
                OnDataProviderRegistered?.Invoke(id);
                if (_config != null && _config.LogDataOperations)
                    Debug.Log($"[DataManager] Registered data provider: {id}");
            };
            _registry.ProviderUnregistered += id => OnDataProviderUnregistered?.Invoke(id);

            // Auto-register always-included providers.
            AutoRegisterAlwaysIncludedProviders();

            // Periodic flush as an Awaitable loop on the subsystem shutdown token
            // (replaces the old coroutine). Explicit fire-and-forget; it owns its
            // exceptions and unwinds when ShutdownToken is cancelled.
            _ = _flusher.RunPeriodicAsync(ShutdownToken);

            callback(this);
        }

        private void AutoRegisterAlwaysIncludedProviders()
        {
            var providers = _config?.AlwaysIncludedProviders;
            if (providers == null) return;

            foreach (var provider in providers)
            {
                if (provider != null)
                {
                    provider.Activate();
                    if (_config.LogDataOperations)
                        Debug.Log($"[DataManager] Auto-registered provider: {provider.ProviderId}");
                }
            }
        }

        #region Data Provider Management

        /// <summary>Registers a data provider with the manager.</summary>
        public void RegisterDataProvider(DataProvider provider) => _registry.RegisterProvider(provider);

        /// <summary>Unregisters a data provider from the manager.</summary>
        public void UnregisterDataProvider(DataProvider provider) => _registry.UnregisterProvider(provider);

        #endregion

        #region DataModel Subscription System

        /// <summary>
        /// Subscribe to notifications when new data is added to any provider using a specific DataModel.
        /// </summary>
        public void SubscribeToDataModel(DataModel dataModel, Action<ImmutableData[]> callback)
        {
            if (dataModel == null || callback == null)
            {
                Debug.LogWarning("[DataManager] SubscribeToDataModel: dataModel or callback is null");
                return;
            }
            _hub.Subscribe(dataModel.ModelId, dataModel.ModelName, callback);
        }

        /// <summary>Unsubscribe from DataModel notifications.</summary>
        public void UnsubscribeFromDataModel(DataModel dataModel, Action<ImmutableData[]> callback)
        {
            if (dataModel == null || callback == null) return;
            _hub.Unsubscribe(dataModel.ModelId, callback);
        }

        // Cache → pool. New cache data is batched by the flusher and fanned out to subscribers.
        private void OnCacheDataAdded(ImmutableData newData) => _flusher.Add(newData);

        /// <summary>
        /// Manually flush all data pools (useful for testing or when immediate processing is needed).
        /// </summary>
        public void FlushAllDataPools() => _flusher.FlushAll();

        /// <summary>Manually flush a specific data pool.</summary>
        public void FlushDataPoolManually(string modelId) => _flusher.Flush(modelId);

        /// <summary>Manually triggers the periodic stale-pool flush check.</summary>
        public void TriggerPeriodicFlushCheck() => _flusher.FlushStale();

        /// <summary>Gets the current pool status for monitoring.</summary>
        public Dictionary<string, object> GetDataPoolStatus() => _flusher.GetStatus();

        #endregion

        #region Event Management

        /// <summary>
        /// Allows external classes to trigger the OnDataUpdated event.
        /// </summary>
        public static void TriggerDataUpdated(string providerId, ImmutableData data)
        {
            OnDataUpdated?.Invoke(providerId, data);
        }

        #endregion

        #region Data Access Methods

        /// <summary>Gets all data from a specific provider.</summary>
        public IReadOnlyList<ImmutableData> GetAllData(string providerId)
        {
            var cache = _registry.GetCache(providerId);
            return cache != null ? cache.Data : new List<ImmutableData>().AsReadOnly();
        }

        #endregion

        #region Data Fetching and Refresh

        /// <summary>Manually triggers data fetch for a specific provider.</summary>
        public bool FetchData(string providerId)
        {
            var provider = _registry.GetProvider(providerId);
            if (provider == null)
            {
                Debug.LogWarning($"[DataManager] Provider {providerId} not found");
                return false;
            }

            if (!provider.IsActive)
                return false;

            try
            {
                provider.FetchData();
                _registry.RecordFetch(providerId);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DataManager] Error fetching data from {providerId}: {e.Message}");
                return false;
            }
        }

        /// <summary>Refreshes all active providers.</summary>
        public void RefreshAllData()
        {
            foreach (var provider in _registry.ActiveProviders.ToList())
                FetchData(provider.ProviderId);
        }

        /// <summary>Checks if data from a provider is stale and needs refresh.</summary>
        public bool IsDataStale(string providerId) => _registry.IsDataStale(providerId);

        #endregion

        #region Cache Management

        /// <summary>Clears cache for a specific provider.</summary>
        public void ClearCache(string providerId)
        {
            var cache = _registry.GetCache(providerId);
            if (cache == null) return;
            cache.Clear();
            if (_config.LogDataOperations)
                Debug.Log($"[DataManager] Cleared cache for provider: {providerId}");
        }

        /// <summary>Clears all caches.</summary>
        public void ClearAllCaches()
        {
            foreach (var cache in _registry.Caches.Values)
                cache.Clear();
            if (_config.LogDataOperations)
                Debug.Log("[DataManager] Cleared all caches");
        }

        /// <summary>Gets cache statistics for monitoring.</summary>
        public Dictionary<string, object> GetCacheStats()
        {
            var stats = new Dictionary<string, object>();
            foreach (var kvp in _registry.Caches)
            {
                var cache = kvp.Value;
                stats[kvp.Key] = new
                {
                    DataCount = cache.Data.Count,
                    LastUpdated = cache.LastUpdated,
                    IsStale = IsDataStale(kvp.Key)
                };
            }
            return stats;
        }

        #endregion

        #region Utility Methods

        /// <summary>Gets all registered provider IDs.</summary>
        public List<string> GetProviderIds() => _registry.GetProviderIds();

        /// <summary>Checks if a provider is registered and active.</summary>
        public bool IsProviderActive(string providerId) => _registry.IsProviderActive(providerId);

        /// <summary>Gets the data model for a specific provider.</summary>
        public DataModel GetProviderModel(string providerId) => _registry.GetProvider(providerId)?.Mapping?.Model;

        /// <summary>Gets a specific provider by ID.</summary>
        public DataProvider GetProvider(string providerId) => _registry.GetProvider(providerId);

        /// <summary>Gets the centralized cache for a specific provider.</summary>
        public DataCache GetProviderCache(string providerId) => _registry.GetCache(providerId);

        /// <summary>
        /// Registers or gets an additional cache for a provider (useful for multi-mapping scenarios).
        /// </summary>
        public DataCache GetOrCreateCache(string cacheKey, DataModel model) => _registry.GetOrCreateCache(cacheKey, model);

        /// <summary>Gets all provider caches for external access.</summary>
        public IReadOnlyDictionary<string, DataCache> GetAllProviderCaches() => _registry.Caches;

        /// <summary>Manually adds test data to a provider's cache.</summary>
        public bool AddTestDataToProvider(string providerId, ImmutableData testData)
        {
            var cache = _registry.GetCache(providerId);
            if (cache == null)
            {
                Debug.LogWarning($"[DataManager] Provider {providerId} not found - cannot add test data");
                return false;
            }

            cache.AddData(testData);
            if (_config.LogDataOperations)
                Debug.Log($"[DataManager] Added test data to provider {providerId}: {testData.modelId}");
            return true;
        }

        /// <summary>Gets cache statistics for a specific provider.</summary>
        public object GetProviderCacheStats(string providerId)
        {
            var cache = _registry.GetCache(providerId);
            if (cache == null) return null;
            return new
            {
                DataCount = cache.Data.Count,
                LastUpdated = cache.LastUpdated,
                IsStale = IsDataStale(providerId)
            };
        }

        /// <summary>Gets information about always-included providers and their registration status.</summary>
        public Dictionary<string, bool> GetAlwaysIncludedProvidersStatus()
        {
            var status = new Dictionary<string, bool>();
            if (_config?.AlwaysIncludedProviders != null)
            {
                foreach (var provider in _config.AlwaysIncludedProviders)
                {
                    if (provider != null)
                        status[provider.ProviderId] = _registry.IsProviderActive(provider.ProviderId);
                }
            }
            return status;
        }

        /// <summary>Gets information about active DataModel subscriptions.</summary>
        public Dictionary<string, int> GetDataModelSubscriptionStats() => _hub.GetStats();

        /// <summary>Checks if there are any active subscriptions for a specific DataModel.</summary>
        public bool HasActiveSubscriptions(DataModel dataModel) =>
            dataModel != null && _hub.HasSubscribers(dataModel.ModelId);

        #endregion

        public override void Shutdown()
        {
            // Flush any remaining batches, then clear collaborators. The flush loop is
            // keyed on ShutdownToken and unwinds when base.Shutdown() cancels it.
            _flusher?.FlushAll();
            _registry?.Clear();
            _hub?.Clear();
            _flusher?.Clear();

            base.Shutdown();
        }
    }
}
