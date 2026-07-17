using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Telemetry
{
    /// <summary>
    /// Buffers telemetry events and POSTs them to an HTTP endpoint as a single JSON batch on
    /// <see cref="FlushAsync"/>. The payload is <c>{ "events": [ ... ] }</c> with one object per event.
    /// </summary>
    /// <remarks>
    /// Undelivered batches are not lost: a failed POST — and whatever is still buffered when
    /// the sink is disposed (including <c>session_ended</c>) — is spooled to disk under
    /// <c>{persistentDataPath}/Molca/telemetry/spool/</c> and replayed on the next
    /// <see cref="FlushAsync"/> (typically the next session). The spool is capped at
    /// <see cref="MaxSpoolFiles"/> files, oldest evicted first, so a permanently unreachable
    /// endpoint cannot grow disk without bound. The request runs on the main thread via
    /// <see cref="UnityWebRequest"/>.
    /// </remarks>
    public sealed class HttpBatchTelemetrySink : ITelemetrySink
    {
        /// <summary>Maximum spooled batch files kept on disk; oldest evicted beyond this.</summary>
        internal const int MaxSpoolFiles = 20;

        private readonly string _endpointUrl;
        private readonly int _timeoutSeconds;
        private readonly string _spoolDirectory;
        private readonly object _gate = new object();
        private readonly List<TelemetryEvent> _buffer = new List<TelemetryEvent>();
        private bool _spoolReplayAttempted;
        // Disambiguates spool file names beyond DateTime.UtcNow.Ticks resolution: on
        // Windows the wall clock advances in ~15ms steps, so a tight loop of failed
        // POSTs/rotations can produce identical ticks and collide onto the same file.
        private static int _spoolSequence;

        /// <inheritdoc/>
        public string Name => "HttpBatch";

        /// <summary>
        /// Creates an HTTP batch sink.
        /// </summary>
        /// <param name="endpointUrl">Absolute URL events are POSTed to.</param>
        /// <param name="timeoutSeconds">Per-request timeout (default 30s).</param>
        /// <param name="spoolDirectory">
        /// Directory undelivered batches are spooled to; defaults to
        /// <c>{persistentDataPath}/Molca/telemetry/spool</c>. Test seam.
        /// </param>
        public HttpBatchTelemetrySink(string endpointUrl, int timeoutSeconds = 30, string spoolDirectory = null)
        {
            _endpointUrl = endpointUrl;
            _timeoutSeconds = Mathf.Max(1, timeoutSeconds);
            _spoolDirectory = string.IsNullOrEmpty(spoolDirectory)
                ? System.IO.Path.Combine(Application.persistentDataPath, "Molca", "telemetry", "spool")
                : spoolDirectory;
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
            if (string.IsNullOrEmpty(_endpointUrl)) return;

            // First flush of the session: replay batches spooled by earlier sessions
            // (failed POSTs, tail batches from Dispose) before this session's events.
            if (!_spoolReplayAttempted)
            {
                _spoolReplayAttempted = true;
                await ReplaySpoolAsync(cancellationToken);
            }

            var batch = TelemetryBuffer.Drain(_gate, _buffer);
            if (batch == null) return;

            var payload = BuildPayload(batch);
            bool delivered = await PostAsync(payload, cancellationToken);
            if (!delivered)
            {
                SpoolPayload(payload);
                Debug.LogWarning($"[Telemetry] HTTP batch POST failed; spooled {batch.Count} event(s) for replay next session.");
            }
        }

        /// <summary>POSTs one payload; <c>false</c> on any non-success outcome except cancellation (which rethrows).</summary>
        private async Awaitable<bool> PostAsync(string payload, CancellationToken cancellationToken)
        {
            using var request = new UnityWebRequest(_endpointUrl, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = _timeoutSeconds;

            try
            {
                var op = request.SendWebRequest();
                while (!op.isDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Awaitable.NextFrameAsync(cancellationToken);
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[Telemetry] HTTP batch POST failed ({request.responseCode}): {request.error}.");
                    return false;
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Telemetry] HTTP batch POST error: {ex.Message}.");
                return false;
            }
        }

        /// <summary>
        /// Attempts to deliver every spooled batch, oldest first; a delivered file is
        /// deleted, an undeliverable one stays for the next session. Stops at the first
        /// failure (the endpoint is evidently unreachable).
        /// </summary>
        private async Awaitable ReplaySpoolAsync(CancellationToken cancellationToken)
        {
            string[] files = ListSpoolFiles();
            if (files.Length == 0) return;

            Debug.Log($"[Telemetry] Replaying {files.Length} spooled batch(es) from a previous session.");
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string payload;
                try
                {
                    payload = System.IO.File.ReadAllText(file, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Telemetry] Unreadable spool file '{file}' removed: {ex.Message}");
                    TryDeleteFile(file);
                    continue;
                }

                if (string.IsNullOrEmpty(payload))
                {
                    TryDeleteFile(file);
                    continue;
                }

                if (await PostAsync(payload, cancellationToken))
                    TryDeleteFile(file);
                else
                    break; // endpoint unreachable — retry the rest next session
            }
        }

        // Spool file names sort chronologically (ticks), so ListSpoolFiles is oldest-first.
        internal void SpoolPayload(string payload)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_spoolDirectory);
                int seq = Interlocked.Increment(ref _spoolSequence);
                string file = System.IO.Path.Combine(
                    _spoolDirectory, $"batch-{DateTime.UtcNow.Ticks:D19}-{seq:D10}.json");
                System.IO.File.WriteAllText(file, payload, Encoding.UTF8);

                // Bound the spool: evict the oldest files beyond the cap.
                string[] files = ListSpoolFiles();
                for (int i = 0; i < files.Length - MaxSpoolFiles; i++)
                    TryDeleteFile(files[i]);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Telemetry] Failed to spool batch: {ex.Message}. Batch dropped.");
            }
        }

        internal string[] ListSpoolFiles()
        {
            try
            {
                if (!System.IO.Directory.Exists(_spoolDirectory))
                    return Array.Empty<string>();
                string[] files = System.IO.Directory.GetFiles(_spoolDirectory, "batch-*.json");
                Array.Sort(files, StringComparer.Ordinal);
                return files;
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { System.IO.File.Delete(path); }
            catch (Exception) { /* best effort */ }
        }

        private static string BuildPayload(List<TelemetryEvent> batch)
        {
            var sb = new StringBuilder();
            sb.Append("{\"events\":[");
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(batch[i].ToJson());
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Pending events at teardown cannot be reliably POSTed synchronously —
            // spool them (this is how session_ended survives) for replay next session.
            var batch = TelemetryBuffer.Drain(_gate, _buffer);
            if (batch == null) return;

            SpoolPayload(BuildPayload(batch));
            Debug.Log($"[Telemetry] HTTP batch sink disposed with {batch.Count} unsent event(s); spooled for replay next session.");
        }
    }
}
