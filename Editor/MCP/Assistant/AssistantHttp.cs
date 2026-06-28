using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>Creates an HTTP result.</summary>
        public AssistantHttpResult(int statusCode, bool isSuccess, string body)
        {
            StatusCode = statusCode;
            IsSuccess = isSuccess;
            Body = body ?? string.Empty;
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
    /// <remarks>Editor-only. The returned awaitable resumes on the main thread; <paramref name="onSseLine"/>
    /// is always invoked on the main thread, so providers can update UI/streaming state directly.</remarks>
    public static class AssistantHttp
    {
        /// <summary>POSTs <paramref name="jsonBody"/> and returns the response.</summary>
        /// <param name="url">Target endpoint.</param>
        /// <param name="headers">Request headers (content-type is set from the JSON body and ignored here).</param>
        /// <param name="jsonBody">UTF-8 JSON request body.</param>
        /// <param name="streaming">When true, the body is read as SSE and each line is delivered to <paramref name="onSseLine"/>.</param>
        /// <param name="onSseLine">Per-line callback for a successful streamed response (main thread); ignored otherwise.</param>
        /// <param name="timeoutSeconds">Per-request timeout.</param>
        /// <param name="cancellationToken">Cancels the request and the pump (surfaces as <see cref="OperationCanceledException"/>).</param>
        public static async Awaitable<AssistantHttpResult> PostAsync(
            string url, IReadOnlyDictionary<string, string> headers, string jsonBody,
            bool streaming, Action<string> onSseLine, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var lines = new ConcurrentQueue<string>();

            var task = Task.Run(async () =>
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
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
                using var resp = await client.SendAsync(req, completion, cancellationToken).ConfigureAwait(false);
                var status = (int)resp.StatusCode;

                // Stream SSE lines only on success; an error response (even when streaming was requested) is
                // read whole so the provider can extract the error message from the body.
                if (streaming && resp.IsSuccessStatusCode)
                {
                    using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        lines.Enqueue(line);
                    }
                    return new AssistantHttpResult(status, true, string.Empty);
                }

                var bodyText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new AssistantHttpResult(status, resp.IsSuccessStatusCode, bodyText);
            }, cancellationToken);

            // Pump on the editor-update loop (fires while Play mode is paused), draining streamed lines onto
            // the main thread so providers surface deltas without touching Unity state off-thread.
            while (!task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (streaming && onSseLine != null)
                    while (lines.TryDequeue(out var queued)) onSseLine(queued);
                await EditorUpdateAwaiter.NextAsync(cancellationToken);
            }

            if (streaming && onSseLine != null)
                while (lines.TryDequeue(out var queued)) onSseLine(queued);

            if (task.IsFaulted)
                throw task.Exception?.GetBaseException() ?? new Exception("HTTP request failed.");

            return task.Result;
        }
    }
}
