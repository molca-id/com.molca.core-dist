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
    /// A failed POST drops that batch (it is logged) rather than retrying indefinitely or growing
    /// the buffer without bound. The request runs on the main thread via <see cref="UnityWebRequest"/>.
    /// </remarks>
    public sealed class HttpBatchTelemetrySink : ITelemetrySink
    {
        private readonly string _endpointUrl;
        private readonly int _timeoutSeconds;
        private readonly object _gate = new object();
        private readonly List<TelemetryEvent> _buffer = new List<TelemetryEvent>();

        /// <inheritdoc/>
        public string Name => "HttpBatch";

        /// <summary>
        /// Creates an HTTP batch sink.
        /// </summary>
        /// <param name="endpointUrl">Absolute URL events are POSTed to.</param>
        /// <param name="timeoutSeconds">Per-request timeout (default 30s).</param>
        public HttpBatchTelemetrySink(string endpointUrl, int timeoutSeconds = 30)
        {
            _endpointUrl = endpointUrl;
            _timeoutSeconds = Mathf.Max(1, timeoutSeconds);
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

            var batch = TelemetryBuffer.Drain(_gate, _buffer);
            if (batch == null) return;

            var payload = BuildPayload(batch);

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
                    Debug.LogWarning($"[Telemetry] HTTP batch POST failed ({request.responseCode}): {request.error}. Dropped {batch.Count} event(s).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Telemetry] HTTP batch POST error: {ex.Message}. Dropped {batch.Count} event(s).");
            }
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
            // Pending events at teardown cannot be reliably POSTed synchronously and are dropped.
            lock (_gate)
            {
                if (_buffer.Count > 0)
                {
                    Debug.LogWarning($"[Telemetry] HTTP batch sink disposed with {_buffer.Count} unsent event(s); dropped.");
                    _buffer.Clear();
                }
            }
        }
    }
}
