using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Molca.Licensing;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Telemetry
{
    /// <summary>
    /// Ships player-side telemetry to the Molca control plane, authenticated by the signed build token
    /// baked into the build. This is what gives Molca visibility into framework usage in shipped
    /// applications rather than only add-on installs in the editor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inert unless the build carries a stamp with a build token, so editor play mode, unlicensed
    /// builds, and builds produced on an offline machine report nothing. It is added automatically by
    /// <see cref="TelemetrySubsystem"/> when <see cref="TelemetrySettings.EnableControlPlaneSink"/> is
    /// on; a project can also disable it and keep its own sinks.
    /// </para>
    /// <para>
    /// Reports carry the licensee (from the stamp), an opaque per-install id, the framework and app
    /// versions, and the event name and properties the project chose to track. No account, device, or
    /// file-system identifier is sent; the install id is hashed server-side before storage.
    /// </para>
    /// </remarks>
    public sealed class ControlPlaneTelemetrySink : ITelemetrySink
    {
        private const int MaxBatch = 100;
        private const int MaxBufferedEvents = 1000;
        private const int RequestTimeoutSeconds = 20;
        private const string InstallIdKey = "Molca.Telemetry.InstallId";

        private readonly object _gate = new object();
        private readonly List<TelemetryEvent> _buffer = new List<TelemetryEvent>();
        private readonly string _endpoint;
        private readonly string _buildToken;
        private readonly string _installId;
        private bool _disabled;

        /// <inheritdoc/>
        public string Name => "control-plane";

        /// <summary>
        /// Creates the sink from the build stamp.
        /// </summary>
        /// <param name="serverBaseUrl">Control-plane base URL; must be HTTPS.</param>
        /// <param name="stamp">The build stamp, or null when the player has none.</param>
        public ControlPlaneTelemetrySink(string serverBaseUrl, LicenseStampData stamp)
        {
            _buildToken = stamp?.buildToken ?? string.Empty;
            _endpoint = (serverBaseUrl ?? string.Empty).TrimEnd('/') + "/telemetry/runtime";
            _disabled = string.IsNullOrEmpty(_buildToken) ||
                        !Uri.TryCreate(_endpoint, UriKind.Absolute, out var uri) ||
                        !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

            // Stable across runs on one installation, random per installation: enough to count
            // installs and sessions without being a device identifier.
            _installId = PlayerPrefs.GetString(InstallIdKey, string.Empty);
            if (string.IsNullOrEmpty(_installId))
            {
                _installId = Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(InstallIdKey, _installId);
                PlayerPrefs.Save();
            }
        }

        /// <summary>True when this build can report to the control plane.</summary>
        public bool IsActive => !_disabled;

        /// <inheritdoc/>
        public void Write(TelemetryEvent telemetryEvent)
        {
            if (_disabled || telemetryEvent == null) return;
            lock (_gate)
            {
                // A long offline session must not grow without bound; drop oldest first so the most
                // recent picture survives.
                if (_buffer.Count >= MaxBufferedEvents) _buffer.RemoveAt(0);
                _buffer.Add(telemetryEvent);
            }
        }

        /// <inheritdoc/>
        public async Awaitable FlushAsync(CancellationToken cancellationToken)
        {
            if (_disabled) return;
            List<TelemetryEvent> pending = TelemetryBuffer.Drain(_gate, _buffer);
            if (pending == null) return;

            for (int offset = 0; offset < pending.Count; offset += MaxBatch)
            {
                int count = Math.Min(MaxBatch, pending.Count - offset);
                if (!await SendAsync(pending.GetRange(offset, count), cancellationToken))
                {
                    // Put the unsent remainder back so the next flush retries it.
                    lock (_gate) _buffer.InsertRange(0, pending.GetRange(offset, pending.Count - offset));
                    return;
                }
            }
        }

        private async Awaitable<bool> SendAsync(List<TelemetryEvent> events, CancellationToken cancellationToken)
        {
            var payload = new StringBuilder();
            payload.Append("{\"buildToken\":").Append(Quote(_buildToken))
                   .Append(",\"installId\":").Append(Quote(_installId))
                   .Append(",\"events\":[");
            for (int index = 0; index < events.Count; index++)
            {
                if (index > 0) payload.Append(',');
                payload.Append(events[index].ToJson());
            }
            payload.Append("]}");

            using var request = new UnityWebRequest(_endpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload.ToString())),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = RequestTimeoutSeconds,
            };
            request.SetRequestHeader("Content-Type", "application/json");

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(cancellationToken);
            }

            // 401/403 mean this build's token was rejected or revoked: stop trying for the rest of the
            // run rather than retrying a decision that will not change.
            if (request.responseCode == 401 || request.responseCode == 403)
            {
                _disabled = true;
                lock (_gate) _buffer.Clear();
                return true;
            }
            if (request.responseCode == 400) return true; // Malformed for this server; discard.
            return request.result == UnityWebRequest.Result.Success;
        }

        private static string Quote(string value) =>
            "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <inheritdoc/>
        public void Dispose()
        {
            // Teardown has already cancelled the shutdown token, so there is no opportunity for a final
            // network round trip. Buffered events are intentionally dropped rather than blocking quit.
            lock (_gate) _buffer.Clear();
        }
    }
}
