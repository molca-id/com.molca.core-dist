using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Molca.Telemetry
{
    /// <summary>
    /// A destination for telemetry events. Sinks are owned by <see cref="TelemetrySubsystem"/>,
    /// which calls <see cref="Write"/> for every tracked event and <see cref="FlushAsync"/>
    /// periodically (and on shutdown).
    /// </summary>
    /// <remarks>
    /// <see cref="Write"/> is called on the main thread and must be cheap — buffer, do not block.
    /// Heavy work (disk, network) belongs in <see cref="FlushAsync"/>. Implementations own their
    /// own exceptions where practical; the subsystem also guards each call.
    /// </remarks>
    public interface ITelemetrySink : IDisposable
    {
        /// <summary>Human-readable sink name for logging.</summary>
        string Name { get; }

        /// <summary>
        /// Records one event. Called on the main thread; should only buffer or do trivial work.
        /// </summary>
        void Write(TelemetryEvent telemetryEvent);

        /// <summary>
        /// Pushes any buffered events to the durable/remote destination.
        /// </summary>
        /// <param name="cancellationToken">Cancelled on subsystem teardown.</param>
        Awaitable FlushAsync(CancellationToken cancellationToken);
    }

    /// <summary>Small helpers for the telemetry sinks.</summary>
    internal static class TelemetryAwaitables
    {
        /// <summary>A pre-completed <see cref="Awaitable"/>, for sinks whose flush is a no-op.</summary>
        public static Awaitable Completed
        {
            get
            {
                var source = new AwaitableCompletionSource();
                source.SetResult();
                return source.Awaitable;
            }
        }
    }

    /// <summary>Shared snapshot helper for buffering sinks.</summary>
    internal static class TelemetryBuffer
    {
        /// <summary>
        /// Atomically swaps <paramref name="buffer"/> contents out under <paramref name="gate"/>
        /// and returns them, leaving the buffer empty. Returns null when nothing is buffered.
        /// </summary>
        public static List<TelemetryEvent> Drain(object gate, List<TelemetryEvent> buffer)
        {
            lock (gate)
            {
                if (buffer.Count == 0) return null;
                var snapshot = new List<TelemetryEvent>(buffer);
                buffer.Clear();
                return snapshot;
            }
        }
    }
}
