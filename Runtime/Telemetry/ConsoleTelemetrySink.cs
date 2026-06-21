using System.Threading;
using UnityEngine;

namespace Molca.Telemetry
{
    /// <summary>
    /// Logs each telemetry event to the Unity console. Useful in the editor and for debugging;
    /// <see cref="FlushAsync"/> is a no-op because logging is immediate.
    /// </summary>
    public sealed class ConsoleTelemetrySink : ITelemetrySink
    {
        /// <inheritdoc/>
        public string Name => "Console";

        /// <inheritdoc/>
        public void Write(TelemetryEvent telemetryEvent)
        {
            if (telemetryEvent == null) return;
            Debug.Log($"[Telemetry] {telemetryEvent.ToJson()}");
        }

        /// <inheritdoc/>
        public Awaitable FlushAsync(CancellationToken cancellationToken) => TelemetryAwaitables.Completed;

        /// <inheritdoc/>
        public void Dispose() { }
    }
}
