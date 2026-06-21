using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Events;
using UnityEngine;

namespace Molca.ContentPackage.Services
{
    /// <summary>Lifecycle status of a single item in the <see cref="PackageDownloadQueue"/>.</summary>
    public enum DownloadQueueItemStatus
    {
        /// <summary>Waiting for a concurrency slot.</summary>
        Queued,
        /// <summary>Currently installing.</summary>
        Active,
        /// <summary>Finished successfully.</summary>
        Completed,
        /// <summary>Finished with an error.</summary>
        Failed,
        /// <summary>Cancelled before or during installation.</summary>
        Cancelled,
    }

    /// <summary>A queued package install and its live progress. Mutated by the queue on the main thread.</summary>
    public class DownloadQueueItem
    {
        /// <summary>The package being installed.</summary>
        public string PackageId;
        /// <summary>Current lifecycle status.</summary>
        public DownloadQueueItemStatus Status;
        /// <summary>Per-package download progress, 0..1.</summary>
        public float Progress;
        /// <summary>Error message when <see cref="Status"/> is <see cref="DownloadQueueItemStatus.Failed"/>.</summary>
        public string Error;
    }

    /// <summary>Immutable snapshot of the queue, dispatched on every change for UI/analytics.</summary>
    public class DownloadQueueSnapshot
    {
        /// <summary>All items currently tracked (queued, active, and terminal until cleared).</summary>
        public IReadOnlyList<DownloadQueueItem> Items;
        /// <summary>Number of items waiting for a slot.</summary>
        public int QueuedCount;
        /// <summary>Number of items currently installing.</summary>
        public int ActiveCount;
        /// <summary>Number of items that completed successfully.</summary>
        public int CompletedCount;
        /// <summary>Number of items that failed.</summary>
        public int FailedCount;
        /// <summary>Whether the queue is paused (no new items start).</summary>
        public bool IsPaused;
        /// <summary>Global progress across non-cancelled items, 0..1.</summary>
        public float AggregateProgress;
    }

    /// <summary>Event-name constants for download-queue events dispatched via <see cref="EventDispatcher"/>.</summary>
    public static class ContentPackageEvents
    {
        /// <summary>Dispatched on any queue change. Data: <see cref="DownloadQueueSnapshot"/>.</summary>
        public const string QueueChanged = "ContentPackage.QueueChanged";
        /// <summary>Dispatched when aggregate progress changes. Data: <see cref="float"/> (0..1).</summary>
        public const string QueueProgress = "ContentPackage.QueueProgress";
    }

    /// <summary>
    /// Schedules package installs with bounded concurrency, exposing pause/resume, cancellation,
    /// queue introspection, and a global aggregate progress. Status changes are surfaced through
    /// the <see cref="EventDispatcher"/> (<see cref="ContentPackageEvents"/>) so UI and analytics can
    /// react without subscribing to <see cref="PackageService"/>'s raw events.
    /// </summary>
    /// <remarks>
    /// Single-threaded by design: all mutation happens on the Unity main thread (every await in the
    /// install path resumes there), so no locking is required. Dispose to detach from
    /// <see cref="PackageService.OnDownloadProgress"/>.
    /// </remarks>
    public class PackageDownloadQueue : IDisposable
    {
        private readonly PackageService _service;
        private readonly EventDispatcher _events;
        private readonly int _maxConcurrency;
        private readonly List<DownloadQueueItem> _items = new List<DownloadQueueItem>();
        private readonly Dictionary<string, CancellationTokenSource> _activeCts = new Dictionary<string, CancellationTokenSource>();

        private bool _paused;
        private bool _disposed;

        /// <summary>Maximum number of items installing at once.</summary>
        public int MaxConcurrency => _maxConcurrency;

        /// <summary>Whether the queue is paused (no new items will start; in-flight items continue).</summary>
        public bool IsPaused => _paused;

        /// <summary>
        /// Creates a download queue.
        /// </summary>
        /// <param name="service">The package service used to perform installs. Required.</param>
        /// <param name="maxConcurrency">Maximum concurrent installs (clamped to >= 1).</param>
        /// <param name="events">Optional event dispatcher for queue status events.</param>
        public PackageDownloadQueue(PackageService service, int maxConcurrency, EventDispatcher events = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _maxConcurrency = Mathf.Max(1, maxConcurrency);
            _events = events;
            _service.OnDownloadProgress += OnServiceDownloadProgress;
        }

        /// <summary>
        /// Adds a package to the queue (or returns the existing item if it is already queued/active).
        /// Starts it immediately if a slot is free and the queue is not paused.
        /// </summary>
        public DownloadQueueItem Enqueue(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return null;

            var existing = _items.FirstOrDefault(i => i.PackageId == packageId
                && (i.Status == DownloadQueueItemStatus.Queued || i.Status == DownloadQueueItemStatus.Active));
            if (existing != null) return existing;

            var item = new DownloadQueueItem { PackageId = packageId, Status = DownloadQueueItemStatus.Queued };
            _items.Add(item);
            RaiseChanged();
            Pump();
            return item;
        }

        /// <summary>Stops starting new items. In-flight installs continue.</summary>
        public void Pause()
        {
            if (_paused) return;
            _paused = true;
            RaiseChanged();
        }

        /// <summary>Resumes starting queued items.</summary>
        public void Resume()
        {
            if (!_paused) return;
            _paused = false;
            RaiseChanged();
            Pump();
        }

        /// <summary>
        /// Cancels a queued or active item. A queued item is marked cancelled; an active item's
        /// install is cancelled via its token.
        /// </summary>
        /// <returns><c>true</c> if a matching item was found.</returns>
        public bool Cancel(string packageId)
        {
            var item = _items.FirstOrDefault(i => i.PackageId == packageId
                && (i.Status == DownloadQueueItemStatus.Queued || i.Status == DownloadQueueItemStatus.Active));
            if (item == null) return false;

            if (item.Status == DownloadQueueItemStatus.Active && _activeCts.TryGetValue(packageId, out var cts))
            {
                cts.Cancel();
            }
            else
            {
                item.Status = DownloadQueueItemStatus.Cancelled;
                RaiseChanged();
                RaiseProgress();
            }
            return true;
        }

        /// <summary>Cancels every queued and active item.</summary>
        public void CancelAll()
        {
            foreach (var item in _items.ToList())
            {
                if (item.Status == DownloadQueueItemStatus.Queued)
                    item.Status = DownloadQueueItemStatus.Cancelled;
                else if (item.Status == DownloadQueueItemStatus.Active && _activeCts.TryGetValue(item.PackageId, out var cts))
                    cts.Cancel();
            }
            RaiseChanged();
            RaiseProgress();
        }

        /// <summary>Removes terminal (completed/failed/cancelled) items from the tracked list.</summary>
        public void ClearCompleted()
        {
            _items.RemoveAll(i => i.Status == DownloadQueueItemStatus.Completed
                || i.Status == DownloadQueueItemStatus.Failed
                || i.Status == DownloadQueueItemStatus.Cancelled);
            RaiseChanged();
        }

        /// <summary>Builds an immutable snapshot of the current queue state.</summary>
        public DownloadQueueSnapshot GetSnapshot()
        {
            return new DownloadQueueSnapshot
            {
                Items = _items.Select(i => new DownloadQueueItem
                {
                    PackageId = i.PackageId,
                    Status = i.Status,
                    Progress = i.Progress,
                    Error = i.Error,
                }).ToList(),
                QueuedCount = _items.Count(i => i.Status == DownloadQueueItemStatus.Queued),
                ActiveCount = _items.Count(i => i.Status == DownloadQueueItemStatus.Active),
                CompletedCount = _items.Count(i => i.Status == DownloadQueueItemStatus.Completed),
                FailedCount = _items.Count(i => i.Status == DownloadQueueItemStatus.Failed),
                IsPaused = _paused,
                AggregateProgress = ComputeAggregateProgress(),
            };
        }

        /// <summary>
        /// Global progress across all non-cancelled items: terminal items count as 1, active items
        /// contribute their fraction, queued items contribute 0. Returns 0 for an empty queue.
        /// </summary>
        public float ComputeAggregateProgress()
        {
            float sum = 0f;
            int denom = 0;
            foreach (var item in _items)
            {
                switch (item.Status)
                {
                    case DownloadQueueItemStatus.Cancelled:
                        continue; // excluded from the denominator
                    case DownloadQueueItemStatus.Completed:
                    case DownloadQueueItemStatus.Failed:
                        sum += 1f;
                        break;
                    case DownloadQueueItemStatus.Active:
                        sum += Mathf.Clamp01(item.Progress);
                        break;
                    // Queued contributes 0.
                }
                denom++;
            }
            return denom == 0 ? 0f : sum / denom;
        }

        private void Pump()
        {
            if (_paused || _disposed) return;
            while (_activeCts.Count < _maxConcurrency)
            {
                var next = _items.FirstOrDefault(i => i.Status == DownloadQueueItemStatus.Queued);
                if (next == null) break;
                _ = RunItemAsync(next);
            }
        }

        // Fire-and-forget worker (async-contract rule 5): owns its exceptions and honours its token.
        private async Awaitable RunItemAsync(DownloadQueueItem item)
        {
            var cts = new CancellationTokenSource();
            _activeCts[item.PackageId] = cts;
            item.Status = DownloadQueueItemStatus.Active;
            RaiseChanged();

            try
            {
                var result = await _service.InstallPackageAsync(item.PackageId, null, cts.Token);
                if (result.Success)
                {
                    item.Status = DownloadQueueItemStatus.Completed;
                    item.Progress = 1f;
                }
                else if (result.WasCancelled)
                {
                    item.Status = DownloadQueueItemStatus.Cancelled;
                }
                else
                {
                    item.Status = DownloadQueueItemStatus.Failed;
                    item.Error = result.ErrorMessage;
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = DownloadQueueItemStatus.Cancelled;
            }
            catch (Exception ex)
            {
                item.Status = DownloadQueueItemStatus.Failed;
                item.Error = ex.Message;
                Debug.LogError($"[PackageDownloadQueue] '{item.PackageId}' install threw: {ex.Message}");
            }
            finally
            {
                _activeCts.Remove(item.PackageId);
                cts.Dispose();
                RaiseChanged();
                RaiseProgress();
                Pump(); // fill the freed slot
            }
        }

        private void OnServiceDownloadProgress(string packageId, float progress)
        {
            var item = _items.FirstOrDefault(i => i.PackageId == packageId && i.Status == DownloadQueueItemStatus.Active);
            if (item == null) return;
            item.Progress = progress;
            RaiseProgress();
        }

        private void RaiseChanged() => _events?.DispatchEvent(ContentPackageEvents.QueueChanged, GetSnapshot());
        private void RaiseProgress() => _events?.DispatchEvent(ContentPackageEvents.QueueProgress, ComputeAggregateProgress());

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _service.OnDownloadProgress -= OnServiceDownloadProgress;
            foreach (var cts in _activeCts.Values)
            {
                try { cts.Cancel(); cts.Dispose(); } catch { /* best effort */ }
            }
            _activeCts.Clear();
        }
    }
}
