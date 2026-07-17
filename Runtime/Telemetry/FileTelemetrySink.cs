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
    /// <remarks>
    /// The file is size-capped: when an append would push it past
    /// <see cref="MaxFileSizeBytes"/>, the current file rotates to <c>{name}.1</c>
    /// (replacing any previous rotation) and a fresh file starts. Worst-case disk use
    /// is therefore ~2× the cap instead of unbounded growth on kiosk/VR devices.
    /// </remarks>
    public sealed class FileTelemetrySink : ITelemetrySink
    {
        /// <summary>Default rotation threshold (5 MB).</summary>
        public const long DefaultMaxFileSizeBytes = 5L * 1024 * 1024;

        private readonly string _filePath;
        private readonly object _gate = new object();
        private readonly List<TelemetryEvent> _buffer = new List<TelemetryEvent>();

        /// <inheritdoc/>
        public string Name => "File";

        /// <summary>The absolute path events are appended to.</summary>
        public string FilePath => _filePath;

        /// <summary>Rotation threshold in bytes; appends that would exceed it rotate the file first.</summary>
        public long MaxFileSizeBytes { get; }

        /// <summary>
        /// Creates a file sink writing to <c>{persistentDataPath}/Molca/telemetry/{fileName}</c>.
        /// </summary>
        /// <param name="fileName">File name (default <c>telemetry.ndjson</c>).</param>
        /// <param name="maxFileSizeBytes">
        /// Rotation threshold; values &lt; 1 fall back to <see cref="DefaultMaxFileSizeBytes"/>.
        /// </param>
        public FileTelemetrySink(string fileName = "telemetry.ndjson", long maxFileSizeBytes = DefaultMaxFileSizeBytes)
        {
            var dir = Path.Combine(Application.persistentDataPath, "Molca", "telemetry");
            _filePath = Path.Combine(dir, string.IsNullOrEmpty(fileName) ? "telemetry.ndjson" : fileName);
            MaxFileSizeBytes = maxFileSizeBytes < 1 ? DefaultMaxFileSizeBytes : maxFileSizeBytes;
        }

        /// <summary>Test seam: creates a sink writing to an explicit absolute path.</summary>
        internal FileTelemetrySink(string absolutePath, long maxFileSizeBytes, bool isAbsolute)
        {
            _filePath = absolutePath;
            MaxFileSizeBytes = maxFileSizeBytes < 1 ? DefaultMaxFileSizeBytes : maxFileSizeBytes;
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
                RotateIfNeeded(text.Length);
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

        /// <summary>
        /// Rotates <c>{file}</c> → <c>{file}.1</c> (replacing any previous rotation)
        /// when appending <paramref name="incomingBytes"/> would push the current file
        /// past <see cref="MaxFileSizeBytes"/>.
        /// </summary>
        private void RotateIfNeeded(long incomingBytes)
        {
            try
            {
                var info = new FileInfo(_filePath);
                if (!info.Exists || info.Length + incomingBytes <= MaxFileSizeBytes)
                    return;

                string rotated = _filePath + ".1";
                if (File.Exists(rotated))
                    File.Delete(rotated);
                File.Move(_filePath, rotated);
            }
            catch (Exception ex)
            {
                // Rotation failure must not lose the append — worst case the file
                // keeps growing until the next successful rotation.
                Debug.LogWarning($"[Telemetry] File sink rotation failed ({_filePath}): {ex.Message}");
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
                string text = BuildText(batch);
                RotateIfNeeded(text.Length);
                File.AppendAllText(_filePath, text, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Telemetry] File sink final flush failed ({_filePath}): {ex.Message}");
            }
        }
    }
}
