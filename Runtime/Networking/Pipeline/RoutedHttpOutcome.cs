using System;
using System.Collections.Generic;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;
using Molca.Networking.Routing;

namespace Molca.Networking.Pipeline
{
    /// <summary>One transport attempt within a send.</summary>
    public sealed class NetworkAttemptRecord
    {
        /// <summary>1-based attempt number.</summary>
        public int Attempt { get; internal set; }

        /// <summary>The URI this attempt targeted. Differs from earlier attempts after a redirect.</summary>
        public string Uri { get; internal set; } = string.Empty;

        /// <summary>HTTP status, or 0 when no exchange completed.</summary>
        public int StatusCode { get; internal set; }

        /// <summary>How this attempt was classified.</summary>
        public NetworkErrorCategory Category { get; internal set; }

        /// <summary>Wire time for this attempt.</summary>
        public TimeSpan Duration { get; internal set; }

        /// <summary>Delay waited before this attempt, from backoff or <c>Retry-After</c>.</summary>
        public TimeSpan DelayBefore { get; internal set; }

        /// <summary>Whether a credential was attached to this attempt.</summary>
        public bool Authenticated { get; internal set; }

        /// <summary>A redacted failure message, or empty on success.</summary>
        public string Message { get; internal set; } = string.Empty;

        /// <summary>Whether this attempt succeeded.</summary>
        public bool IsSuccess => Category == NetworkErrorCategory.None;

        /// <summary>Renders the attempt for diagnostics.</summary>
        public override string ToString() =>
            $"#{Attempt} {StatusCode} {Category} {Duration.TotalMilliseconds:F0}ms" +
            (DelayBefore > TimeSpan.Zero ? $" (waited {DelayBefore.TotalMilliseconds:F0}ms)" : string.Empty);
    }

    /// <summary>Where the wall-clock time of a send went.</summary>
    public readonly struct NetworkSendTimings
    {
        /// <summary>Time spent waiting for a concurrency slot.</summary>
        public readonly TimeSpan Queued;

        /// <summary>Time spent acquiring credentials.</summary>
        public readonly TimeSpan Authentication;

        /// <summary>Time spent waiting between attempts.</summary>
        public readonly TimeSpan RetryDelay;

        /// <summary>Time spent on the wire across all attempts.</summary>
        public readonly TimeSpan Wire;

        /// <summary>Total wall-clock time from resolution to completion.</summary>
        public readonly TimeSpan Total;

        /// <summary>Creates a timing record.</summary>
        /// <param name="queued">Time waiting for a concurrency slot.</param>
        /// <param name="authentication">Time acquiring credentials.</param>
        /// <param name="retryDelay">Time waiting between attempts.</param>
        /// <param name="wire">Time on the wire.</param>
        /// <param name="total">Total wall-clock time.</param>
        public NetworkSendTimings(
            TimeSpan queued,
            TimeSpan authentication,
            TimeSpan retryDelay,
            TimeSpan wire,
            TimeSpan total)
        {
            Queued = queued;
            Authentication = authentication;
            RetryDelay = retryDelay;
            Wire = wire;
            Total = total;
        }

        /// <summary>Renders the breakdown for diagnostics.</summary>
        public override string ToString() =>
            $"total={Total.TotalMilliseconds:F0}ms queue={Queued.TotalMilliseconds:F0}ms " +
            $"auth={Authentication.TotalMilliseconds:F0}ms retry={RetryDelay.TotalMilliseconds:F0}ms " +
            $"wire={Wire.TotalMilliseconds:F0}ms";
    }

    /// <summary>
    /// The typed result of a routed send: status, body, case-insensitive headers, attempts, timings,
    /// route, and error category — no string parsing required.
    /// </summary>
    /// <remarks>
    /// Returned instead of thrown. A routed caller branches on <see cref="Category"/>; only cancellation
    /// surfaces as an exception, because a cancelled send has no result to describe and callers already
    /// handle <see cref="OperationCanceledException"/> per the async contract.
    /// <para>
    /// <see cref="Response"/> exposes the legacy <see cref="HttpResponse"/> so existing helpers
    /// (<c>GetJsonData&lt;T&gt;</c>, texture and audio accessors) keep working — but read headers through
    /// <see cref="Headers"/>, which is case-insensitive.
    /// </para>
    /// </remarks>
    public sealed class RoutedHttpOutcome
    {
        /// <summary>The route this send targeted.</summary>
        public NetworkRouteKey Route { get; internal set; }

        /// <summary>The endpoint ID applied, or empty.</summary>
        public string EndpointId { get; internal set; } = string.Empty;

        /// <summary>The final URI, after any redirect.</summary>
        public string Uri { get; internal set; } = string.Empty;

        /// <summary>Whether the send produced a 2xx response.</summary>
        public bool IsSuccess { get; internal set; }

        /// <summary>HTTP status, or 0 when no exchange completed.</summary>
        public int StatusCode { get; internal set; }

        /// <summary>How the outcome was classified. <see cref="NetworkErrorCategory.None"/> on success.</summary>
        public NetworkErrorCategory Category { get; internal set; }

        /// <summary>A redacted failure message, or empty on success.</summary>
        public string Message { get; internal set; } = string.Empty;

        /// <summary>The underlying exception, when one caused the failure.</summary>
        public Exception Cause { get; internal set; }

        /// <summary>Response headers, compared case-insensitively. Never <c>null</c>.</summary>
        public NetworkHeaderCollection Headers { get; internal set; } = NetworkHeaderCollection.Empty;

        /// <summary>
        /// The legacy response object, or <c>null</c> when no exchange completed (a configuration,
        /// security, or connectivity failure before the wire).
        /// </summary>
        public HttpResponse Response { get; internal set; }

        /// <summary>Every attempt made, in order. Never <c>null</c>.</summary>
        public IReadOnlyList<NetworkAttemptRecord> Attempts { get; internal set; } =
            Array.Empty<NetworkAttemptRecord>();

        /// <summary>Where the time went.</summary>
        public NetworkSendTimings Timings { get; internal set; }

        /// <summary>Correlation ID tying this outcome to its diagnostics.</summary>
        public string CorrelationId { get; internal set; } = string.Empty;

        /// <summary>Whether the body was served from cache rather than the network.</summary>
        public bool ServedFromCache { get; internal set; }

        /// <summary>Redirects followed during this send.</summary>
        public int RedirectCount { get; internal set; }

        /// <summary>Whether a credential was attached to the final attempt.</summary>
        public bool WasAuthenticated { get; internal set; }

        /// <summary>
        /// Security rules that overruled a weaker authored or per-send value. Empty when nothing was
        /// clamped.
        /// </summary>
        public IReadOnlyList<string> SecurityClamps { get; internal set; } = Array.Empty<string>();

        /// <summary>Number of attempts made.</summary>
        public int AttemptCount => Attempts.Count;

        /// <summary>The legacy error shape, for code that already branches on <see cref="HttpErrorKind"/>.</summary>
        /// <remarks>
        /// Lossy for routed-only categories — see <see cref="NetworkErrorCategoryExtensions.ToLegacyKind"/>.
        /// Prefer <see cref="Category"/> in new code.
        /// </remarks>
        public HttpError LegacyError => IsSuccess
            ? HttpError.None
            : new HttpError(Category.ToLegacyKind(StatusCode), StatusCode, Message, Cause);

        /// <summary>The response body as text, or empty when there is none.</summary>
        public string Text => Response?.GetContentAsString() ?? string.Empty;

        /// <summary>
        /// Deserializes the response body as JSON.
        /// </summary>
        /// <typeparam name="T">The target type.</typeparam>
        /// <returns>The deserialized value, or <c>null</c> when there is no body or it did not parse.</returns>
        public T Json<T>() where T : class => Response?.GetJsonData<T>();

        /// <summary>A redacted single-line projection safe for logs and export.</summary>
        public string ToRedactedString()
        {
            string cache = ServedFromCache ? " cached" : string.Empty;
            string redirects = RedirectCount > 0 ? $" redirects={RedirectCount}" : string.Empty;
            string outcome = IsSuccess ? "ok" : $"{Category}: {Message}";

            return $"{Route} {StatusCode} {outcome} attempts={AttemptCount}{redirects}{cache} " +
                   $"{Timings} correlation={CorrelationId}";
        }

        /// <inheritdoc />
        public override string ToString() => ToRedactedString();
    }
}
