using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Molca.Telemetry
{
    /// <summary>
    /// Application-wide telemetry service. Collects named events with properties, tags them with a
    /// per-run session id, and forwards them to one or more pluggable <see cref="ITelemetrySink"/>s
    /// (console / file / HTTP batch). Events are flushed periodically and when a batch threshold is
    /// reached. Configured by <see cref="TelemetrySettings"/>; inactive unless telemetry is enabled.
    /// </summary>
    /// <remarks>
    /// Add this as a child component of the RuntimeManager prefab. Recommended
    /// <see cref="RuntimeSubsystem.InitializationPriority"/> is low (initialize late) so other
    /// systems can emit during their own startup once it is ready. Inject it with
    /// <c>[Inject(false)] TelemetrySubsystem</c> so consumers degrade gracefully when telemetry is
    /// not present.
    /// </remarks>
    public class TelemetrySubsystem : RuntimeSubsystem
    {
        private TelemetrySettings _settings;
        private readonly List<ITelemetrySink> _sinks = new List<ITelemetrySink>();
        private string _sessionId;
        private int _pendingSinceFlush;
        // The in-flight flush, if any. Overlapping FlushAsync callers await this
        // (Task, not Awaitable — many callers may await it) so "flushed" means
        // flushed, not "someone else was flushing".
        private System.Threading.Tasks.Task _flushTask;
        private bool _enabled;

        /// <summary>Unique id for the current app run, attached to every event. Always set.</summary>
        public string SessionId => _sessionId;

        /// <summary>True when telemetry is enabled and at least one sink is active.</summary>
        public bool IsEnabled => _enabled;

        /// <inheritdoc/>
        public override async Awaitable InitializeAsync(CancellationToken cancellationToken)
        {
            _sessionId = Guid.NewGuid().ToString("N");

            _settings = GlobalSettings.GetModule<TelemetrySettings>();
            if (_settings == null || !_settings.EnableTelemetry)
            {
                Debug.Log("[TelemetrySubsystem] Telemetry disabled (no settings module or EnableTelemetry is false).");
                return;
            }

            BuildSinks();
            if (_sinks.Count == 0)
            {
                Debug.LogWarning("[TelemetrySubsystem] Telemetry enabled but no usable sinks configured.");
                return;
            }

            _enabled = true;

            // Periodic flush loop, cancelled on Teardown via ShutdownToken.
            _ = FlushLoopAsync(ShutdownToken);

            Track("telemetry.session_started");
            Debug.Log($"[TelemetrySubsystem] Telemetry active (session {_sessionId}, {_sinks.Count} sink(s)).");

            await TelemetryAwaitables.Completed;
        }

        private void BuildSinks()
        {
            if (_settings.EnableConsoleSink)
                _sinks.Add(new ConsoleTelemetrySink());

            if (_settings.EnableFileSink)
                _sinks.Add(new FileTelemetrySink());

            if (_settings.EnableHttpSink)
            {
                if (string.IsNullOrEmpty(_settings.HttpEndpointUrl))
                    Debug.LogWarning("[TelemetrySubsystem] HTTP sink enabled but no endpoint URL configured; skipping it.");
                else
                    _sinks.Add(new HttpBatchTelemetrySink(_settings.HttpEndpointUrl));
            }
        }

        /// <summary>
        /// Records a telemetry event. No-op when telemetry is disabled. Safe to call from the main
        /// thread at any time after initialization.
        /// </summary>
        /// <param name="name">Event name, e.g. <c>"sequence.step_started"</c>. Required.</param>
        /// <param name="properties">Optional event properties.</param>
        public void Track(string name, IReadOnlyDictionary<string, object> properties = null)
        {
            if (!_enabled || string.IsNullOrEmpty(name)) return;

            var telemetryEvent = new TelemetryEvent(name, _sessionId, properties);
            foreach (var sink in _sinks)
            {
                try { sink.Write(telemetryEvent); }
                catch (Exception ex) { Debug.LogError($"[TelemetrySubsystem] Sink '{sink.Name}' Write failed: {ex.Message}"); }
            }

            if (++_pendingSinceFlush >= _settings.BatchSize)
                _ = FlushAsync(ShutdownToken);
        }

        /// <summary>
        /// Flushes all sinks. Overlapping calls chain onto the in-flight flush: a
        /// second caller awaits the flush already running instead of returning
        /// immediately while its events are still buffered.
        /// </summary>
        /// <param name="cancellationToken">Cancelled on teardown.</param>
        public async Awaitable FlushAsync(CancellationToken cancellationToken)
        {
            // Main-thread only (like Track), so the check-then-set is race-free.
            if (_flushTask != null && !_flushTask.IsCompleted)
            {
                await _flushTask;
                return;
            }

            var task = FlushCoreAsync(cancellationToken);
            _flushTask = task;
            await task;
        }

        private async System.Threading.Tasks.Task FlushCoreAsync(CancellationToken cancellationToken)
        {
            _pendingSinceFlush = 0;

            try
            {
                foreach (var sink in _sinks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { await sink.FlushAsync(cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Debug.LogError($"[TelemetrySubsystem] Sink '{sink.Name}' flush failed: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException)
            {
                // Teardown — stop quietly.
            }
        }

        private async Awaitable FlushLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Awaitable.WaitForSecondsAsync(_settings.FlushIntervalSeconds, cancellationToken);
                    await FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on teardown.
            }
        }

        /// <inheritdoc/>
        public override void Teardown()
        {
            if (_enabled)
            {
                Track("telemetry.session_ended");

                // ShutdownToken is already cancelled here, so a final async flush would no-op.
                // Each sink's Dispose performs a best-effort synchronous flush of its buffer.
                foreach (var sink in _sinks)
                {
                    try { sink.Dispose(); }
                    catch (Exception ex) { Debug.LogError($"[TelemetrySubsystem] Sink '{sink.Name}' dispose failed: {ex.Message}"); }
                }

                _sinks.Clear();
                _enabled = false;
            }

            base.Teardown();
        }
    }
}
