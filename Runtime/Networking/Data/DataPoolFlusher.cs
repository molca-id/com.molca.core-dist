using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Batches incoming data per model and flushes it to subscribers, extracted from
    /// <see cref="DataManager"/> (Sprint 40 decomposition). The periodic flush is an
    /// <see cref="Awaitable"/> loop keyed on a shutdown token (replacing the previous
    /// coroutine, per the async contract).
    /// </summary>
    /// <remarks>
    /// Thread-safe: pool maps are guarded by an internal lock (preserving the Phase-1
    /// lock discipline). Subscriber callbacks run outside the lock via
    /// <see cref="DataSubscriptionHub.Notify"/>. Internal collaborator — <c>DataManager</c>
    /// delegates to it.
    /// </remarks>
    internal sealed class DataPoolFlusher
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, List<ImmutableData>> _pool = new Dictionary<string, List<ImmutableData>>();
        private readonly Dictionary<string, float> _lastFlushTime = new Dictionary<string, float>();
        private readonly float _flushInterval;
        private readonly DataSubscriptionHub _hub;
        private readonly Func<bool> _logEnabled;

        public DataPoolFlusher(float flushInterval, DataSubscriptionHub hub, Func<bool> logEnabled)
        {
            _flushInterval = flushInterval;
            _hub = hub;
            _logEnabled = logEnabled;
        }

        /// <summary>
        /// Adds a datum to its model's pool, flushing immediately if the interval
        /// elapsed. Main-thread only: every provider marshals its callback onto the
        /// main thread at the boundary (SocketIO via <c>OnUnityThread</c>, WebSocket via
        /// <c>DispatchMessageQueue</c> from the pump loop, HTTP polling already runs on
        /// an <c>Awaitable</c> continuation) — see the async-contract addendum. <c>Time.time</c>
        /// is read once here, before the lock, rather than inside it: it is a Unity API and
        /// must never be called while any lock is held that a background caller could contend.
        /// </summary>
        public void Add(ImmutableData data)
        {
            string modelId = data.modelId;
            float now = Time.time;

            bool shouldFlush;
            lock (_lock)
            {
                if (!_pool.ContainsKey(modelId))
                {
                    _pool[modelId] = new List<ImmutableData>();
                    _lastFlushTime[modelId] = now;
                }

                _pool[modelId].Add(data);
                shouldFlush = now - _lastFlushTime[modelId] >= _flushInterval;
            }

            if (shouldFlush)
                Flush(modelId);
        }

        /// <summary>Drains one model's pool and notifies its subscribers (outside the lock). Main-thread only (see <see cref="Add"/>).</summary>
        public void Flush(string modelId)
        {
            float now = Time.time;
            ImmutableData[] pooled;
            lock (_lock)
            {
                if (!_pool.TryGetValue(modelId, out var list) || list.Count == 0)
                    return;

                pooled = list.ToArray();
                list.Clear();
                _lastFlushTime[modelId] = now;
            }

            if (_logEnabled())
                Debug.Log($"[DataManager] Flushing data pool for ModelID: {modelId} with {pooled.Length} items");

            _hub.Notify(modelId, pooled);
        }

        public void FlushAll()
        {
            List<string> modelIds;
            lock (_lock)
                modelIds = _pool.Keys.ToList();
            foreach (var modelId in modelIds)
                Flush(modelId);
        }

        /// <summary>Flushes only pools that have buffered data past the flush interval.</summary>
        public void FlushStale()
        {
            foreach (var modelId in CollectStale())
                Flush(modelId);
        }

        private List<string> CollectStale()
        {
            float now = Time.time;
            var stale = new List<string>();
            lock (_lock)
            {
                foreach (var kvp in _pool)
                {
                    if (kvp.Value.Count > 0 && now - _lastFlushTime[kvp.Key] >= _flushInterval)
                        stale.Add(kvp.Key);
                }
            }
            return stale;
        }

        public Dictionary<string, object> GetStatus()
        {
            float now = Time.time;
            var status = new Dictionary<string, object>();
            lock (_lock)
            {
                foreach (var kvp in _pool)
                {
                    float since = now - _lastFlushTime[kvp.Key];
                    status[kvp.Key] = new
                    {
                        PooledDataCount = kvp.Value.Count,
                        TimeSinceLastFlush = since,
                        ShouldFlush = since >= _flushInterval
                    };
                }
            }
            return status;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _pool.Clear();
                _lastFlushTime.Clear();
            }
        }

        /// <summary>
        /// Periodic stale-pool flush loop. Replaces the old <c>PeriodicPoolFlushing</c>
        /// coroutine; runs on the main thread (Awaitable continuation) and unwinds when
        /// <paramref name="token"/> is cancelled (subsystem shutdown).
        /// </summary>
        public async Awaitable RunPeriodicAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Check twice as often as the flush interval, matching prior cadence.
                    await Awaitable.WaitForSecondsAsync(_flushInterval * 0.5f, token);
                    FlushStale();
                }
            }
            catch (OperationCanceledException)
            {
                // Subsystem shut down — exit quietly.
            }
        }
    }
}
