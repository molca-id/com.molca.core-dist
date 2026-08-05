using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HttpErrorKind = Molca.Networking.Http.Models.HttpErrorKind;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>Outcome of an <see cref="AssistantHttp"/> POST: HTTP status, success flag, and response body.</summary>
    public sealed class AssistantHttpResult
    {
        /// <summary>HTTP status code.</summary>
        public int StatusCode { get; }
        /// <summary>True for a 2xx response.</summary>
        public bool IsSuccess { get; }
        /// <summary>Full body for non-streaming or error responses; empty for a successful streamed response.</summary>
        public string Body { get; }
        /// <summary>
        /// Server-advised retry delay parsed from a <c>Retry-After</c> header (seconds), or <c>null</c> when the
        /// header is absent/unparseable. Honored by the retry backoff on a 429/503 (Sprint 68).
        /// </summary>
        public double? RetryAfterSeconds { get; }

        /// <summary>Creates an HTTP result.</summary>
        public AssistantHttpResult(int statusCode, bool isSuccess, string body, double? retryAfterSeconds = null)
        {
            StatusCode = statusCode;
            IsSuccess = isSuccess;
            Body = body ?? string.Empty;
            RetryAfterSeconds = retryAfterSeconds;
        }
    }

    /// <summary>
    /// HTTP POST for the assistant's LLM calls, built so a turn can complete <b>while Unity Play mode is
    /// paused</b>. The request runs on a background <see cref="Task"/> via <see cref="HttpClient"/> (threadpool,
    /// independent of the player loop), while completion and streamed SSE lines are pumped onto the main
    /// thread through <see cref="EditorUpdateAwaiter"/> (driven by <c>EditorApplication.update</c>, which keeps
    /// firing during pause). This replaces <c>UnityWebRequest</c> + <c>Awaitable.NextFrameAsync</c>, both of
    /// which the player-loop pause freezes (Sprint 65).
    /// </summary>
    /// <remarks>
    /// Editor-only. The returned awaitable resumes on the main thread; <paramref name="onSseLine"/> is always
    /// invoked on the main thread, so providers can update UI/streaming state directly.
    /// <para>
    /// Sprint 68: the POST is wrapped in a bounded retry with full-jitter exponential backoff. Failures are
    /// classified with the same <see cref="HttpErrorKind"/> taxonomy the Networking layer ships
    /// (Sprint 39): a 429 (honoring <c>Retry-After</c>), 5xx, connection, or timeout failure is retried up to
    /// <c>maxAttempts</c> total tries; a 4xx auth/validation error or a real cancellation is never retried.
    /// On a streaming retry the provider's <c>onStreamRestart</c> callback resets its SSE accumulator so a
    /// partially-streamed attempt can't corrupt the reassembled response.
    /// </para>
    /// </remarks>
    public static class AssistantHttp
    {
        /// <summary>First-attempt backoff base (seconds); doubles each attempt before full jitter.</summary>
        private const double BaseBackoffSeconds = 0.5;
        /// <summary>Upper bound on a single computed backoff delay (seconds), Retry-After included.</summary>
        private const double MaxBackoffSeconds = 30.0;

        /// <summary>POSTs <paramref name="jsonBody"/> and returns the response, retrying transient failures.</summary>
        /// <param name="url">Target endpoint.</param>
        /// <param name="headers">Request headers (content-type is set from the JSON body and ignored here).</param>
        /// <param name="jsonBody">UTF-8 JSON request body.</param>
        /// <param name="streaming">When true, the body is read as SSE and each line is delivered to <paramref name="onSseLine"/>.</param>
        /// <param name="onSseLine">Per-line callback for a successful streamed response (main thread); ignored otherwise.</param>
        /// <param name="timeoutSeconds">
        /// Deadline for the model to <em>start</em> responding. For a streaming call this bounds the wait for
        /// the first SSE line only; for a non-streaming call it bounds the whole exchange, because there is no
        /// progress signal to measure instead.
        /// </param>
        /// <param name="cancellationToken">Cancels the request and the pump (surfaces as <see cref="OperationCanceledException"/>).</param>
        /// <param name="maxAttempts">Maximum total attempts including the first (Sprint 68); <c>1</c> disables retry.</param>
        /// <param name="onStreamRestart">
        /// Invoked before a streaming retry so the provider can reset its SSE accumulator to a clean state
        /// (Sprint 68); ignored for non-streaming calls.
        /// </param>
        /// <param name="stallTimeoutSeconds">
        /// Streaming only: the longest gap tolerated <em>between</em> SSE lines before the attempt is treated
        /// as stalled. A response that keeps producing tokens is never timed out, however long it runs — which
        /// is the point: a reasoning model on a large context can legitimately stream for many minutes, and a
        /// total deadline would kill it mid-answer. <c>&lt;= 0</c> disables stall detection.
        /// </param>
        public static async Awaitable<AssistantHttpResult> PostAsync(
            string url, IReadOnlyDictionary<string, string> headers, string jsonBody,
            bool streaming, Action<string> onSseLine, int timeoutSeconds, CancellationToken cancellationToken,
            int maxAttempts = 1, Action onStreamRestart = null, int stallTimeoutSeconds = 0)
        {
            return await RunWithRetryAsync(
                ct => AttemptOnceAsync(url, headers, jsonBody, streaming, onSseLine, timeoutSeconds, stallTimeoutSeconds, ct),
                streaming, onStreamRestart, maxAttempts, cancellationToken, DelayAsync);
        }

        /// <summary>
        /// Performs a single GET and returns the response (Sprint 71). Used for model discovery
        /// (Ollama <c>/api/tags</c>, OpenAI-compatible <c>/models</c>): a short, non-streaming, non-retried
        /// request that resolves on the main thread like <see cref="PostAsync"/>. A transport fault (endpoint
        /// down) is caught and surfaced as a non-success <see cref="AssistantHttpResult"/> rather than thrown,
        /// so callers can degrade to free-text without a try/catch.
        /// </summary>
        /// <param name="url">Target endpoint.</param>
        /// <param name="headers">Optional request headers (e.g. an Authorization bearer for a secured endpoint).</param>
        /// <param name="timeoutSeconds">Per-request timeout; kept short so an unreachable endpoint fails fast.</param>
        /// <param name="cancellationToken">Cancels the request (surfaces as <see cref="OperationCanceledException"/>).</param>
        public static async Awaitable<AssistantHttpResult> GetAsync(
            string url, IReadOnlyDictionary<string, string> headers, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var task = Task.Run(async () =>
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers != null)
                    foreach (var kv in headers)
                        req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new AssistantHttpResult((int)resp.StatusCode, resp.IsSuccessStatusCode, body);
            }, cancellationToken);

            while (!task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EditorUpdateAwaiter.NextAsync(cancellationToken);
            }

            // A caller-cancel rethrows; any other transport fault degrades to a synthetic non-success result so
            // discovery can fall back to free-text instead of surfacing an exception to the UI.
            if (task.IsFaulted)
            {
                var ex = task.Exception?.GetBaseException();
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw ex;
                return new AssistantHttpResult(0, false, ex?.Message ?? "GET request failed.");
            }
            return task.Result;
        }

        /// <summary>
        /// Drives the bounded retry policy around a single-attempt delegate (Sprint 68). Extracted from
        /// <see cref="PostAsync"/> so tests can inject a scripted <paramref name="attempt"/> and a no-op
        /// <paramref name="delay"/> to exercise retry/backoff/give-up/cancellation deterministically without a
        /// live network. Returns the final result, or rethrows the terminal fault after the cap.
        /// </summary>
        internal static async Awaitable<AssistantHttpResult> RunWithRetryAsync(
            Func<CancellationToken, Awaitable<AssistantHttpResult>> attempt,
            bool streaming, Action onStreamRestart, int maxAttempts, CancellationToken cancellationToken,
            Func<double, CancellationToken, Awaitable> delay)
        {
            if (maxAttempts < 1) maxAttempts = 1;
            // De-correlate retry backoff across editors so a provider-wide outage doesn't make every client
            // retry in lockstep. Not cryptographic; only needs to spread the delay window.
            var rng = new System.Random();

            for (var attemptNo = 1; ; attemptNo++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AssistantHttpResult result;
                double? retryAfter;
                try
                {
                    result = await attempt(cancellationToken);
                    retryAfter = result.RetryAfterSeconds;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // A real turn cancellation is never retried and stays quiet (async contract rule 6).
                    throw;
                }
                catch (Exception ex)
                {
                    // Transport-level fault: connection or timeout. HttpClient's own timeout surfaces as a
                    // TaskCanceledException whose token is NOT the caller's, so it reaches here (the filtered
                    // catch above only swallows a caller-requested cancel).
                    var kind = ClassifyException(ex);
                    var retryable = kind == HttpErrorKind.Network || kind == HttpErrorKind.Timeout;
                    if (!retryable || attemptNo >= maxAttempts)
                        throw;
                    if (streaming) onStreamRestart?.Invoke();
                    await delay(ComputeBackoffSeconds(attemptNo, null, rng), cancellationToken);
                    continue;
                }

                if (result.IsSuccess || attemptNo >= maxAttempts || !IsRetryableStatus(result.StatusCode))
                    return result;

                // Retryable HTTP error (429/5xx/408): reset any partial stream, back off, and try again.
                if (streaming) onStreamRestart?.Invoke();
                await delay(ComputeBackoffSeconds(attemptNo, retryAfter, rng), cancellationToken);
            }
        }

        /// <summary>
        /// Performs a single POST attempt: runs the request on a background task and pumps completion/SSE lines
        /// onto the main thread via the editor update loop. Throws the transport exception on a faulted task.
        /// </summary>
        private static async Awaitable<AssistantHttpResult> AttemptOnceAsync(
            string url, IReadOnlyDictionary<string, string> headers, string jsonBody,
            bool streaming, Action<string> onSseLine, int timeoutSeconds, int stallTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            var lines = new ConcurrentQueue<string>();

            // Deadlines are enforced here rather than by HttpClient.Timeout, which is a *total* budget and so
            // cannot express "still streaming, therefore still healthy". The clock is a Stopwatch timestamp
            // (safe off the main thread, unlike Time.*) written by the request task and read by the pump.
            var activity = new ActivityClock();
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var requestToken = deadlineCts.Token;
            string timeoutReason = null;

            var task = Task.Run(async () =>
            {
                using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(jsonBody ?? string.Empty, Encoding.UTF8, "application/json")
                };
                if (headers != null)
                {
                    foreach (var kv in headers)
                    {
                        if (string.Equals(kv.Key, "content-type", StringComparison.OrdinalIgnoreCase)) continue;
                        req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                }

                var completion = streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
                using var resp = await client.SendAsync(req, completion, requestToken).ConfigureAwait(false);
                var status = (int)resp.StatusCode;
                activity.Touch(); // headers arrived — the server is alive

                // Stream SSE lines only on success; an error response (even when streaming was requested) is
                // read whole so the provider can extract the error message from the body.
                if (streaming && resp.IsSuccessStatusCode)
                {
                    using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        requestToken.ThrowIfCancellationRequested();
                        activity.Touch(); // every line is proof of progress; the stall clock restarts
                        lines.Enqueue(line);
                    }
                    return new AssistantHttpResult(status, true, string.Empty);
                }

                var bodyText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new AssistantHttpResult(status, resp.IsSuccessStatusCode, bodyText, ParseRetryAfter(resp));
            }, requestToken);

            // Pump on the editor-update loop (fires while Play mode is paused), draining streamed lines onto
            // the main thread so providers surface deltas without touching Unity state off-thread. The same
            // loop is the deadline watchdog.
            while (!task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested(); // a real user cancel always wins

                var drained = false;
                if (streaming && onSseLine != null)
                    while (lines.TryDequeue(out var queued)) { onSseLine(queued); drained = true; }
                if (drained) activity.Touch();

                timeoutReason = DescribeExpiredDeadline(activity, streaming, timeoutSeconds, stallTimeoutSeconds);
                if (timeoutReason != null)
                {
                    deadlineCts.Cancel();
                    break;
                }

                await EditorUpdateAwaiter.NextAsync(cancellationToken);
            }

            if (streaming && onSseLine != null)
                while (lines.TryDequeue(out var queued)) onSseLine(queued);

            // A deadline we imposed surfaces as a Timeout so the retry policy engages, and carries a message
            // that says which deadline expired — "the operation has timed out" told the user nothing.
            if (timeoutReason != null) throw new TimeoutException(timeoutReason);

            if (task.IsFaulted)
                throw task.Exception?.GetBaseException() ?? new Exception("HTTP request failed.");

            return task.Result;
        }

        /// <summary>Reads the clock and applies <see cref="DescribeExpiredDeadline(bool,bool,double,double,int,int)"/>.</summary>
        internal static string DescribeExpiredDeadline(
            ActivityClock activity, bool streaming, int timeoutSeconds, int stallTimeoutSeconds)
            => DescribeExpiredDeadline(streaming, activity.SawProgress, activity.SecondsSinceStart,
                activity.SecondsSinceLastTouch, timeoutSeconds, stallTimeoutSeconds);

        /// <summary>
        /// The deadline decision, as a pure function of elapsed times — no clock, so it is testable with plain
        /// numbers instead of sleeping.
        /// </summary>
        /// <remarks>
        /// Streaming is judged on <b>progress</b>: once any chunk has arrived, only the gap since the last one
        /// matters, so a response that keeps producing output never expires however long it runs. Before the
        /// first chunk, and for a non-streaming call (which has no progress signal at all),
        /// <paramref name="timeoutSeconds"/> applies. A non-positive budget disables that check rather than
        /// expiring instantly — callers use <see cref="LlmTimeouts"/>, which never produces a non-positive
        /// first-response budget, and <c>0</c> is the documented way to switch stall detection off.
        /// </remarks>
        /// <param name="streaming">Whether the attempt is streaming.</param>
        /// <param name="sawProgress">Whether anything has been received yet.</param>
        /// <param name="sinceStartSeconds">Seconds since the attempt began.</param>
        /// <param name="sinceLastTouchSeconds">Seconds since the last received chunk.</param>
        /// <param name="timeoutSeconds">First-response budget (total budget when not streaming); &lt;= 0 disables.</param>
        /// <param name="stallTimeoutSeconds">Inter-chunk budget; &lt;= 0 disables.</param>
        /// <returns>A reason string when a deadline has expired, else <c>null</c>.</returns>
        internal static string DescribeExpiredDeadline(
            bool streaming, bool sawProgress, double sinceStartSeconds, double sinceLastTouchSeconds,
            int timeoutSeconds, int stallTimeoutSeconds)
        {
            if (!streaming || !sawProgress)
                return timeoutSeconds > 0 && sinceStartSeconds > timeoutSeconds
                    ? (streaming
                        ? $"The model did not start responding within {timeoutSeconds}s. Raise 'Request Timeout' in the assistant's Resilience settings if this model needs longer to begin."
                        : $"The model did not respond within {timeoutSeconds}s. Raise 'Request Timeout' in the assistant's Resilience settings, or enable Stream Responses so a long answer is bounded by stall detection instead of a total deadline.")
                    : null;

            return stallTimeoutSeconds > 0 && sinceLastTouchSeconds > stallTimeoutSeconds
                ? $"The response stalled — no data for {stallTimeoutSeconds}s after streaming for {sinceStartSeconds:0}s. Raise 'Stream Stall Timeout' in the assistant's Resilience settings if the model pauses this long mid-answer."
                : null;
        }

        /// <summary>
        /// A monotonic progress clock shared between the request task and the pump. Uses
        /// <see cref="Stopwatch"/> timestamps because the request side runs off the main thread, where Unity's
        /// time APIs throw (the same rule the log pipeline follows).
        /// </summary>
        internal sealed class ActivityClock
        {
            private readonly long _startedAt = Stopwatch.GetTimestamp();
            private long _lastTouch = Stopwatch.GetTimestamp();
            private int _sawProgress;

            /// <summary>Records that the peer produced something (headers, or a streamed line).</summary>
            public void Touch()
            {
                Interlocked.Exchange(ref _lastTouch, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _sawProgress, 1);
            }

            /// <summary>Whether anything has been received yet.</summary>
            public bool SawProgress => Interlocked.CompareExchange(ref _sawProgress, 0, 0) == 1;

            /// <summary>Seconds since the attempt began.</summary>
            public double SecondsSinceStart => (Stopwatch.GetTimestamp() - _startedAt) / (double)Stopwatch.Frequency;

            /// <summary>Seconds since the last <see cref="Touch"/>.</summary>
            public double SecondsSinceLastTouch =>
                (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastTouch)) / (double)Stopwatch.Frequency;
        }

        /// <summary>
        /// Classifies a transport exception into the Networking <see cref="HttpErrorKind"/> taxonomy
        /// (Sprint 39): connection faults are <see cref="HttpErrorKind.Network"/>, an <see cref="HttpClient"/>
        /// timeout (a <see cref="TaskCanceledException"/> with no caller cancel) is
        /// <see cref="HttpErrorKind.Timeout"/>; anything else is left un-retryable.
        /// </summary>
        internal static HttpErrorKind ClassifyException(Exception ex)
        {
            // Our own deadline (first-byte or stall) throws TimeoutException. It must classify as Timeout or
            // the retry policy would treat a stalled stream as fatal and burn the turn on one bad attempt.
            if (ex is TimeoutException)
                return HttpErrorKind.Timeout;
            // HttpClient timeout: TaskCanceledException is a subclass of OperationCanceledException. The
            // caller-cancel case was already filtered out before we reach here, so treat it as a timeout.
            if (ex is TaskCanceledException || ex is OperationCanceledException)
                return HttpErrorKind.Timeout;
            if (ex is HttpRequestException || ex is System.Net.Sockets.SocketException || ex is IOException)
                return HttpErrorKind.Network;
            return HttpErrorKind.None;
        }

        /// <summary>
        /// Whether an HTTP status code is a transient failure worth retrying: any 5xx, or the transient 4xx
        /// codes 408 (request timeout) and 429 (rate limit). Mirrors the Networking retry policy.
        /// </summary>
        internal static bool IsRetryableStatus(int status)
        {
            if (status is >= 500 and < 600) return true;     // Http5xx
            return status == 429 || status == 408;           // transient Http4xx
        }

        /// <summary>
        /// Full-jitter exponential backoff for a 1-based attempt, clamped to <see cref="MaxBackoffSeconds"/>.
        /// A server-advised <paramref name="retryAfterSeconds"/> (from a 429/503 <c>Retry-After</c>) takes
        /// precedence — honored as a floor, still capped — so we never hammer ahead of the server's advice.
        /// </summary>
        internal static double ComputeBackoffSeconds(int attempt, double? retryAfterSeconds, System.Random rng)
        {
            if (retryAfterSeconds is > 0)
                return Math.Min(retryAfterSeconds.Value, MaxBackoffSeconds);

            double cap = Math.Min(BaseBackoffSeconds * (1 << (attempt - 1)), MaxBackoffSeconds);
            double fraction = rng?.NextDouble() ?? 1.0;
            return fraction * cap;
        }

        /// <summary>
        /// Reads a <c>Retry-After</c> header as seconds: a delta value directly, or an HTTP-date converted to a
        /// delay from now. Returns <c>null</c> when the header is absent or unparseable.
        /// </summary>
        private static double? ParseRetryAfter(HttpResponseMessage resp)
        {
            var retryAfter = resp?.Headers?.RetryAfter;
            if (retryAfter == null) return null;
            if (retryAfter.Delta is { } delta) return delta.TotalSeconds;
            if (retryAfter.Date is { } date)
            {
                var seconds = (date - DateTimeOffset.UtcNow).TotalSeconds;
                return seconds > 0 ? seconds : 0;
            }
            return null;
        }

        /// <summary>
        /// Waits <paramref name="seconds"/> on the editor update loop (so backoff elapses even while Play mode
        /// is paused, consistent with the rest of this transport). A non-positive delay returns immediately.
        /// </summary>
        private static async Awaitable DelayAsync(double seconds, CancellationToken cancellationToken)
        {
            if (seconds <= 0) return;
            double end = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EditorUpdateAwaiter.NextAsync(cancellationToken);
            }
        }
    }
}
