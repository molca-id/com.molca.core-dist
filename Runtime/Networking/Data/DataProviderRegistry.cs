using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Owns provider lifecycle and per-provider caches, extracted from
    /// <see cref="DataManager"/> (Sprint 40 decomposition). New cache data is forwarded
    /// to the pool flusher via the callback supplied at construction.
    /// </summary>
    /// <remarks>
    /// Internal collaborator — not part of the public API; <c>DataManager</c> owns one
    /// and delegates to it. Registration/unregistration raise
    /// <see cref="ProviderRegistered"/>/<see cref="ProviderUnregistered"/> which the
    /// manager relays onto its public static events.
    /// </remarks>
    internal sealed class DataProviderRegistry
    {
        private readonly List<DataProvider> _providers = new List<DataProvider>();
        private readonly Dictionary<string, DataProvider> _providerLookup = new Dictionary<string, DataProvider>();
        private readonly Dictionary<string, DataCache> _dataCaches = new Dictionary<string, DataCache>();
        private readonly Dictionary<string, DateTime> _lastFetchTimes = new Dictionary<string, DateTime>();

        private readonly DataConfig _config;
        private readonly Action<ImmutableData> _onCacheDataAdded;

        public event Action<string> ProviderRegistered;
        public event Action<string> ProviderUnregistered;

        public DataProviderRegistry(DataConfig config, Action<ImmutableData> onCacheDataAdded)
        {
            _config = config;
            _onCacheDataAdded = onCacheDataAdded;
        }

        public void RegisterProvider(DataProvider provider)
        {
            if (provider == null) return;

            string providerId = provider.ProviderId;
            if (_providers.Contains(provider))
                return;

            _providers.Add(provider);
            _providerLookup[providerId] = provider;

            if (provider.Mapping != null && provider.Mapping.Model != null)
            {
                var cache = new DataCache();
                cache.Initialize(provider.Mapping.Model, _config.DefaultCacheDuration, _config.DefaultMaxCacheSize);
                _dataCaches[providerId] = cache;
                cache.OnDataAdded += _onCacheDataAdded;

                if (_config.LogDataOperations)
                    Debug.Log($"[DataManager] Initialized cache for provider: {providerId}");
            }
            else
            {
                Debug.LogWarning($"[DataManager] Provider {providerId} has no mapping or model - cache not initialized");
            }

            ProviderRegistered?.Invoke(providerId);
        }

        public void UnregisterProvider(DataProvider provider)
        {
            if (provider == null) return;

            string providerId = provider.ProviderId;
            if (!_providers.Remove(provider))
                return;

            if (_dataCaches.TryGetValue(providerId, out var cache))
                cache.OnDataAdded -= _onCacheDataAdded;

            _providerLookup.Remove(providerId);
            _dataCaches.Remove(providerId);
            _lastFetchTimes.Remove(providerId);

            ProviderUnregistered?.Invoke(providerId);

            if (_config.LogDataOperations)
                Debug.Log($"[DataManager] Unregistered data provider: {providerId}");
        }

        public bool TryGetProvider(string providerId, out DataProvider provider) =>
            _providerLookup.TryGetValue(providerId, out provider);

        public DataProvider GetProvider(string providerId) =>
            _providerLookup.TryGetValue(providerId, out var p) ? p : null;

        public bool IsProviderActive(string providerId) => _providerLookup.ContainsKey(providerId);

        public List<string> GetProviderIds() => _providerLookup.Keys.ToList();

        public IEnumerable<DataProvider> ActiveProviders => _providers.Where(p => p.IsActive);

        public DataCache GetCache(string providerId) =>
            _dataCaches.TryGetValue(providerId, out var c) ? c : null;

        public IReadOnlyDictionary<string, DataCache> Caches => _dataCaches;

        public DataCache GetOrCreateCache(string cacheKey, DataModel model)
        {
            if (_dataCaches.TryGetValue(cacheKey, out var existing))
                return existing;

            var cache = new DataCache();
            cache.Initialize(model, _config.DefaultCacheDuration, _config.DefaultMaxCacheSize);
            _dataCaches[cacheKey] = cache;
            cache.OnDataAdded += _onCacheDataAdded;

            if (_config.LogDataOperations)
                Debug.Log($"[DataManager] Created new cache for key: {cacheKey} with Model: {model.ModelName} (ID: {model.ModelId})");

            return cache;
        }

        public void RecordFetch(string providerId) => _lastFetchTimes[providerId] = DateTime.UtcNow;

        public bool IsDataStale(string providerId)
        {
            if (!_lastFetchTimes.TryGetValue(providerId, out var last))
                return true;
            return (DateTime.UtcNow - last).TotalSeconds > _config.DefaultCacheDuration;
        }

        public void Clear()
        {
            foreach (var cache in _dataCaches.Values)
                cache.OnDataAdded -= _onCacheDataAdded;
            _providers.Clear();
            _providerLookup.Clear();
            _dataCaches.Clear();
            _lastFetchTimes.Clear();
        }
    }
}
