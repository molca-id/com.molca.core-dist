using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Molca.Telemetry
{
    /// <summary>
    /// Appends telemetry events as JSON lines (newline-delimited JSON) to a file under
    /// <see cref="Application.persistentDataPath"/>. Events are buffered in memory and written
    /// to disk on a background thread during <see cref="FlushAsync"/>.
    /// </summary>
    public sealed class FileTelemetrySink : ITelemetrySink
    {
        private readonly string _filePath;
        private readonly object _gate = new object();
        private readonly List<TelemetryEvent> _buffer = new List<TelemetryEvent>();

        /// <inheritdoc/>
        public string Name => "File";

        /// <summary>The absolute path events are appended to.</summary>
        public string FilePath => _filePath;

        /// <summary>
        /// Creates a file sink writing to <c>{persistentDataPath}/Molca/telemetry/{fileName}</c>.
        /// </summary>
        /// <param name="fileName">File name (default <c>telemetry.ndjson</c>).</param>
        public FileTelemetrySink(string fileName = "telemetry.ndjson")
        {
            var dir = Path.Combine(Application.persistentDataPath, "Molca", "telemetry");
            _filePath = Path.Combine(dir, string.IsNullOrEmpty(fileName) ? "telemetry.ndjson" : fileName);
        }

        /// <inheritdoc/>
        public void Write(TelemetryEvent telemetryEvent)
        {
            if (telemetryEvent == null) return;
            lock (_gate) _buffer.Add(telemetryEvent);
        }

        /// <inheritdoc/>
        public async Awaitable FlushAsync(CancellationToken cancellationToken)
        {
            var batch = TelemetryBuffer.Drain(_gate, _buffer);
            if (batch == null) return;

            var text = BuildText(batch);

            // Disk IO off the main thread; the write itself does not need the Unity context.
            await Awaitable.BackgroundThreadAsync();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
                File.AppendAllText(_filePath, text, Encoding.UTF8);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Re-buffer is not attempted (could grow unbounded on a persistent IO fault);
                // a dropped batch is logged instead.
                Debug.LogError($"[Telemetry] File sink write failed ({_filePath}): {ex.Message}");
            }
            finally
            {
                await Awaitable.MainThreadAsync();
            }
        }

        private static string BuildText(List<TelemetryEvent> batch)
        {
            var sb = new StringBuilder();
            foreach (var e in batch)
                sb.Append(e.ToJson()).Append('\n');
            return sb.ToString();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Best-effort synchronous flush of anything still buffered at teardown.
            var batch = TelemetryBuffer.Drain(_gate, _buffer);
            if (batch == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
                File.AppendAllText(_filePath, BuildText(batch), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Telemetry] File sink final flush failed ({_filePath}): {ex.Message}");
            }
        }
    }
}
