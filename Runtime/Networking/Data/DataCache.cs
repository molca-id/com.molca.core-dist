using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Attributes;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca.Networking.Data
{
    [Serializable]
    public class DataCache
    {
        [SerializeField, FormerlySerializedAs("model"), ReadOnly] private DataModel _model;
        [SerializeField, FormerlySerializedAs("cacheDuration")] private float _cacheDuration = 10f;
        [Tooltip("The maximum cache size in bytes")]
        [SerializeField, FormerlySerializedAs("maxCacheSize")] private int _maxCacheSize = 1024 * 1024 * 10; // 10MB

        private List<ImmutableData> _datas = new List<ImmutableData>();
        private int _currentCacheSize = 0;

        // Event triggered when new data is added to the cache
        public event Action<ImmutableData> OnDataAdded;

        public DataModel Model => _model;
        public IReadOnlyList<ImmutableData> Data => _datas;
        public DateTime LastUpdated => _datas.Count > 0 ? _datas.Max(d => d.createdAt) : DateTime.MinValue;

        public void Initialize(DataModel _model, float? customCacheDuration = null, int? customMaxCacheSize = null)
        {
            this._model = _model;
            if (customCacheDuration.HasValue)
                _cacheDuration = customCacheDuration.Value;
            if (customMaxCacheSize.HasValue)
                _maxCacheSize = customMaxCacheSize.Value;
                
            if (_cacheDuration > 0)
            {
                UpdateCache();
            }
        }

        public void AddData(ImmutableData data)
        {
            if (!data.IsValid) return;
            
            this._datas.Add(data);
            _currentCacheSize += data.GetSize();
            
            // Trigger the OnDataAdded event
            OnDataAdded?.Invoke(data);
            
            // Manage cache size
            if (_currentCacheSize > _maxCacheSize)
            {
                var oldestData = _datas.First();
                _currentCacheSize -= oldestData.GetSize();
                _datas.RemoveAt(0);
            }
        }
        
        /// <summary>
        /// Searches for data containing a specific value in any field
        /// </summary>
        public List<ImmutableData> SearchData(string searchTerm, bool caseSensitive = false)
        {
            var results = new List<ImmutableData>();
            var term = caseSensitive ? searchTerm : searchTerm.ToLower();
            
            foreach (var data in _datas)
            {
                if (data.data.ContainsKey(term))
                {
                    results.Add(data);
                }
                else if (data.data.Values.Any(v => v.ToString().Contains(term)))
                {
                    results.Add(data);
                }
            }
            
            return results;
        }
        
        /// <summary>
        /// Gets data within a specific time range
        /// </summary>
        public List<ImmutableData> GetDataInTimeRange(DateTime startTime, DateTime endTime)
        {
            return _datas.Where(d => d.createdAt >= startTime && d.createdAt <= endTime).ToList();
        }
        
        /// <summary>
        /// Gets the most recent data entries
        /// </summary>
        public List<ImmutableData> GetRecentData(int count)
        {
            return _datas.OrderByDescending(d => d.createdAt).Take(count).ToList();
        }

        private void UpdateCache()
        {
            // Remove expired data
            var now = DateTime.UtcNow;
            var expiredData = _datas.Where(d => d.createdAt < now - TimeSpan.FromSeconds(_cacheDuration)).ToList();
            
            foreach (var data in expiredData)
            {
                _currentCacheSize -= data.GetSize();
                _datas.Remove(data);
            }
        }

        public void Clear() 
        { 
            _datas.Clear(); 
            _currentCacheSize = 0;
        }
        
        /// <summary>
        /// Gets cache statistics
        /// </summary>
        public Dictionary<string, object> GetStats()
        {
            return new Dictionary<string, object>
            {
                ["DataCount"] = _datas.Count,
                ["CacheSize"] = _currentCacheSize,
                ["MaxCacheSize"] = _maxCacheSize,
                ["CacheDuration"] = _cacheDuration,
                ["LastUpdated"] = LastUpdated,
                ["IsExpired"] = _datas.Count > 0 && LastUpdated < DateTime.UtcNow - TimeSpan.FromSeconds(_cacheDuration)
            };
        }
    }
}
