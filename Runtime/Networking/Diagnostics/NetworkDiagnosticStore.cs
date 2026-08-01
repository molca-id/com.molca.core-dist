using System;
using System.Collections.Generic;
using UnityEngine;
using Molca.Networking.Configuration;
using Molca.Networking.Pipeline;
using Molca.Networking.Routing;
using Molca.Networking.Streaming;
using Molca.Networking.Utils;

namespace Molca.Networking.Diagnostics
{
    /// <summary>
    /// A redacted record of one completed send, safe to display, copy, and export.
    /// </summary>
    /// <remarks>
    /// Every field here is either non-secret by construction or redacted before it arrives. Request and
    /// response headers are deliberately absent — an <c>Authorization</c> header is the single likeliest
    /// place for a credential to leak into a diagnostic, and the credential profile <em>name</em> answers
    /// every question a reader actually has.
    /// </remarks>
    public sealed class NetworkRequestDiagnostic
    {
        /// <summary>When the send completed.</summary>
        public DateTime CompletedUtc { get; internal set; }

        /// <summary>The route targeted.</summary>
        public NetworkRouteKey Route { get; internal set; }

        /// <summary>The endpoint ID applied, or empty.</summary>
        public string EndpointId { get; internal set; } = string.Empty;

        /// <summary>The HTTP method.</summary>
        public string Method { get; internal set; } = string.Empty;

        /// <summary>
        /// The final URI, after any redirect, with every query value masked.
        /// </summary>
        /// <remarks>
        /// Masked rather than preserved. A query string is a routine place for a signed URL, a session
        /// token, or a one-time link to end up, and this record is displayed, copied, and exported —
        /// so "query values are not secrets" is a contract the caller cannot keep on the record's behalf.
        /// Read the unmasked URI from <see cref="RoutedHttpOutcome.Uri"/> when a caller genuinely needs it.
        /// </remarks>
        public string Uri { get; internal set; } = string.Empty;

        /// <summary>HTTP status, or 0 when no exchange completed.</summary>
        public int StatusCode { get; internal set; }

        /// <summary>How the outcome was classified.</summary>
        public NetworkErrorCategory Category { get; internal set; }

        /// <summary>A redacted failure message, or empty on success.</summary>
        public string Message { get; internal set; } = string.Empty;

        /// <summary>Whether the send succeeded.</summary>
        public bool IsSuccess { get; internal set; }

        /// <summary>Where the time went.</summary>
        public NetworkSendTimings Timings { get; internal set; }

        /// <summary>Attempts made, in order.</summary>
        public IReadOnlyList<NetworkAttemptRecord> Attempts { get; internal set; } =
            Array.Empty<NetworkAttemptRecord>();

        /// <summary>Correlation ID tying this record to the outcome and any logs.</summary>
        public string CorrelationId { get; internal set; } = string.Empty;

        /// <summary>The credential profile used, or empty for anonymous. Never a value.</summary>
        public string CredentialProfileId { get; internal set; } = string.Empty;

        /// <summary>Whether the body came from cache.</summary>
        public bool ServedFromCache { get; internal set; }

        /// <summary>Redirects followed.</summary>
        public int RedirectCount { get; internal set; }

        /// <summary>
        /// A redacted response body preview, or empty. Populated only when the policy opts into
        /// <see cref="NetworkEffectivePolicy.CaptureBodies"/>.
        /// </summary>
        public string BodyPreview { get; internal set; } = string.Empty;

        /// <summary>Security rules that overruled a weaker authored or per-send value.</summary>
        public IReadOnlyList<string> SecurityClamps { get; internal set; } = Array.Empty<string>();

        /// <summary>Renders one export line.</summary>
        public override string ToString()
        {
            string credential = string.IsNullOrEmpty(CredentialProfileId) ? "anonymous" : $"cred={CredentialProfileId}";
            string outcome = IsSuccess ? "ok" : $"{Category}: {Message}";

            return $"{CompletedUtc:O} {Method} {Uri} {StatusCode} {outcome} " +
                   $"route={Route} {credential} attempts={Attempts.Count} {Timings} correlation={CorrelationId}";
        }
    }

    /// <summary>Read-only access to network diagnostics, for the Hub, Doctor, and MCP.</summary>
    /// <remarks>
    /// A stable interface so telemetry surfaces stop discovering providers by reflection
    /// (plan §7.12). Consumers depend on this, not on the concrete store or the subsystem.
    /// </remarks>
    public interface INetworkDiagnostics
    {
        /// <summary>Records retained, newest last. A bounded snapshot safe to enumerate.</summary>
        IReadOnlyList<NetworkRequestDiagnostic> Snapshot();

        /// <summary>Records retained.</summary>
        int Count { get; }

        /// <summary>Maximum records retained before the oldest is dropped.</summary>
        int Capacity { get; }

        /// <summary>Sends completed since the store was created or cleared.</summary>
        int TotalCompleted { get; }

        /// <summary>Sends that failed since the store was created or cleared.</summary>
        int TotalFailed { get; }

        /// <summary>Observer callbacks that threw. Recorded separately; they never affect a request.</summary>
        int ObserverFailureCount { get; }

        /// <summary>Whether recording is paused.</summary>
        bool IsPaused { get; }

        /// <summary>Per-route live state, for the Hub's Live view.</summary>
        IReadOnlyList<RoutePipelineState> RouteStates();

        /// <summary>
        /// Live streaming sessions, for the Hub's Live and Providers views.
        /// </summary>
        /// <returns>The sessions; empty when nothing is streaming.</returns>
        /// <remarks>
        /// Defaulted rather than abstract so adding it did not break an existing implementer. A store
        /// that owns no session registry legitimately has nothing to report.
        /// </remarks>
        IReadOnlyList<NetworkStreamSession> StreamSessions() => Array.Empty<NetworkStreamSession>();

        /// <summary>Renders every retained record as redacted export text.</summary>
        string Export();
    }

    /// <summary>
    /// A bounded ring buffer of redacted request diagnostics.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose: diagnostics must cost a predictable amount of memory in a session that runs
    /// for days. Owned by the network subsystem, so teardown discards it rather than leaking history
    /// across a domain reload.
    /// </remarks>
    public sealed class NetworkDiagnosticStore : INetworkDiagnostics
    {
        /// <summary>Default number of records retained.</summary>
        public const int DefaultCapacity = 200;

        /// <summary>Maximum characters retained in a body preview.</summary>
        public const int BodyPreviewLimit = 2048;

        private readonly Queue<NetworkRequestDiagnostic> _records = new Queue<NetworkRequestDiagnostic>();
        private readonly NetworkRouteStateStore _routeStates;
        private readonly NetworkStreamSessionRegistry _streams;

        /// <inheritdoc />
        public int Capacity { get; }

        /// <inheritdoc />
        public int Count => _records.Count;

        /// <inheritdoc />
        public int TotalCompleted { get; private set; }

        /// <inheritdoc />
        public int TotalFailed { get; private set; }

        /// <inheritdoc />
        public int ObserverFailureCount { get; private set; }

        /// <inheritdoc />
        public bool IsPaused { get; private set; }

        /// <summary>Creates a store.</summary>
        /// <param name="routeStates">The route state store surfaced through <see cref="RouteStates"/>.</param>
        /// <param name="capacity">Records retained; values below 1 fall back to <see cref="DefaultCapacity"/>.</param>
        /// <param name="streams">The stream registry surfaced through <see cref="StreamSessions"/>, or <c>null</c>.</param>
        public NetworkDiagnosticStore(
            NetworkRouteStateStore routeStates,
            int capacity = DefaultCapacity,
            NetworkStreamSessionRegistry streams = null)
        {
            _routeStates = routeStates;
            _streams = streams;
            Capacity = capacity < 1 ? DefaultCapacity : capacity;
        }

        /// <summary>Stops and resumes recording. Counters keep accumulating either way.</summary>
        /// <param name="paused">Whether to pause.</param>
        public void SetPaused(bool paused) => IsPaused = paused;

        /// <summary>Drops every record and resets the counters.</summary>
        public void Clear()
        {
            _records.Clear();
            TotalCompleted = 0;
            TotalFailed = 0;
            ObserverFailureCount = 0;
        }

        /// <inheritdoc />
        public IReadOnlyList<NetworkRequestDiagnostic> Snapshot() =>
            new List<NetworkRequestDiagnostic>(_records);

        /// <inheritdoc />
        public IReadOnlyList<RoutePipelineState> RouteStates()
        {
            var states = new List<RoutePipelineState>();
            if (_routeStates == null) return states;

            foreach (var state in _routeStates.All)
                states.Add(state);
            return states;
        }

        /// <inheritdoc />
        public IReadOnlyList<NetworkStreamSession> StreamSessions()
        {
            var sessions = new List<NetworkStreamSession>();
            if (_streams == null) return sessions;

            foreach (var session in _streams.Sessions)
                sessions.Add(session);
            return sessions;
        }

        /// <inheritdoc />
        public string Export()
        {
            var builder = new System.Text.StringBuilder();
            builder.Append("# Molca network diagnostics — ").Append(Count).AppendLine(" record(s), redacted");

            foreach (var record in _records)
                builder.AppendLine(record.ToString());

            var sessions = StreamSessions();
            if (sessions.Count > 0)
            {
                builder.Append("# Streaming sessions — ").Append(sessions.Count).AppendLine(" live");
                foreach (var session in sessions)
                    builder.AppendLine(session.ToString());
            }

            return builder.ToString();
        }

        /// <summary>Records one observer callback failure.</summary>
        /// <remarks>
        /// Counted separately from request outcomes so a broken listener is visible without being
        /// mistaken for a network problem (plan §6.4).
        /// </remarks>
        public void RecordObserverFailure() => ObserverFailureCount++;

        /// <summary>
        /// Records a completed send.
        /// </summary>
        /// <param name="request">The resolved request.</param>
        /// <param name="outcome">The outcome.</param>
        /// <param name="bodyPreview">
        /// A redacted body preview, or <c>null</c>. Ignored unless the policy opts into body capture.
        /// </param>
        public void Record(ResolvedHttpRequest request, RoutedHttpOutcome outcome, string bodyPreview = null)
        {
            TotalCompleted++;
            if (!outcome.IsSuccess) TotalFailed++;

            // Counters still move while paused or when the policy disables logging — "how many failed?"
            // must stay answerable even when the detail is not being retained.
            if (IsPaused || !request.Policy.LogRequests.Value)
                return;

            var record = new NetworkRequestDiagnostic
            {
                CompletedUtc = DateTime.UtcNow,
                Route = outcome.Route,
                EndpointId = outcome.EndpointId,
                Method = request.Method.ToString(),
                Uri = LogRedaction.RedactUrl(outcome.Uri),
                StatusCode = outcome.StatusCode,
                Category = outcome.Category,
                Message = outcome.Message,
                IsSuccess = outcome.IsSuccess,
                Timings = outcome.Timings,
                Attempts = outcome.Attempts,
                CorrelationId = outcome.CorrelationId,
                CredentialProfileId = outcome.WasAuthenticated ? request.Credential?.Id ?? string.Empty : string.Empty,
                ServedFromCache = outcome.ServedFromCache,
                RedirectCount = outcome.RedirectCount,
                SecurityClamps = outcome.SecurityClamps,
                BodyPreview = request.Policy.CaptureBodies.Value ? Truncate(bodyPreview) : string.Empty
            };

            _records.Enqueue(record);
            while (_records.Count > Capacity)
                _records.Dequeue();

            if (!outcome.IsSuccess && request.Policy.LogRequests.Value)
                Debug.LogWarning($"[Network] {outcome.ToRedactedString()}");
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= BodyPreviewLimit ? value : value.Substring(0, BodyPreviewLimit) + "…";
        }
    }
}
