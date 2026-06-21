using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Owns DataModel subscriptions and fan-out notification, extracted from
    /// <see cref="DataManager"/> (Sprint 40 decomposition). Keyed by model id.
    /// </summary>
    /// <remarks>
    /// Thread-safe: the subscription map is guarded by an internal lock (preserving the
    /// Phase-1 lock discipline). <see cref="Notify"/> snapshots subscribers under the
    /// lock and invokes them <i>outside</i> it, so user callbacks never run while the
    /// lock is held. Internal collaborator — not part of the public API; <c>DataManager</c>
    /// delegates to it.
    /// </remarks>
    internal sealed class DataSubscriptionHub
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, List<Action<ImmutableData[]>>> _subscriptions = new Dictionary<string, List<Action<ImmutableData[]>>>();
        private readonly Func<bool> _logEnabled;

        public DataSubscriptionHub(Func<bool> logEnabled)
        {
            _logEnabled = logEnabled;
        }

        public void Subscribe(string modelId, string modelName, Action<ImmutableData[]> callback)
        {
            if (string.IsNullOrEmpty(modelId) || callback == null)
            {
                Debug.LogWarning("[DataManager] SubscribeToDataModel: modelId or callback is null");
                return;
            }

            lock (_lock)
            {
                if (!_subscriptions.ContainsKey(modelId))
                    _subscriptions[modelId] = new List<Action<ImmutableData[]>>();

                if (!_subscriptions[modelId].Contains(callback))
                {
                    _subscriptions[modelId].Add(callback);
                    if (_logEnabled())
                        Debug.Log($"[DataManager] Subscribed to DataModel: {modelName} (ID: {modelId})");
                }
            }
        }

        public void Unsubscribe(string modelId, Action<ImmutableData[]> callback)
        {
            if (string.IsNullOrEmpty(modelId) || callback == null)
                return;

            lock (_lock)
            {
                if (_subscriptions.TryGetValue(modelId, out var list))
                {
                    list.Remove(callback);
                    if (list.Count == 0)
                        _subscriptions.Remove(modelId);
                }
            }
        }

        /// <summary>Notifies subscribers of a model with a batch; callbacks run outside the lock.</summary>
        public void Notify(string modelId, ImmutableData[] batch)
        {
            List<Action<ImmutableData[]>> callbacks;
            lock (_lock)
            {
                callbacks = _subscriptions.TryGetValue(modelId, out var subs)
                    ? new List<Action<ImmutableData[]>>(subs)
                    : null;
            }

            if (callbacks == null)
                return;

            foreach (var callback in callbacks)
                callback?.Invoke(batch);
        }

        public bool HasSubscribers(string modelId)
        {
            if (string.IsNullOrEmpty(modelId))
                return false;
            lock (_lock)
                return _subscriptions.TryGetValue(modelId, out var list) && list.Count > 0;
        }

        public Dictionary<string, int> GetStats()
        {
            var stats = new Dictionary<string, int>();
            lock (_lock)
            {
                foreach (var kvp in _subscriptions)
                    stats[$"ID:{kvp.Key}"] = kvp.Value.Count;
            }
            return stats;
        }

        public void Clear()
        {
            lock (_lock)
                _subscriptions.Clear();
        }
    }
}
