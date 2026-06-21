using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Molca.Networking.Http.Models;

namespace Molca.Networking.Http
{
    /// <summary>
    /// Instance API of the HTTP subsystem. Resolve via
    /// <c>RuntimeManager.GetService&lt;IHttpClient&gt;()</c> or inject with
    /// <c>[Inject] IHttpClient</c>. Replaces the legacy static surface on
    /// <see cref="HttpClient"/>, which remains as <see cref="ObsoleteAttribute"/> shims.
    /// </summary>
    public interface IHttpClient
    {
        /// <summary>Base URL from the configured <see cref="HttpModule"/>, or empty.</summary>
        string BaseUrl { get; }

        /// <summary>Maximum number of requests allowed in flight simultaneously.</summary>
        int MaxConcurrentRequests { get; }

        /// <summary>Completed-request history (when enabled in <see cref="HttpModule"/>).</summary>
        IReadOnlyList<HttpRequestContext> RequestHistory { get; }

        /// <summary>Number of requests currently in flight.</summary>
        int ActiveRequestCount { get; }

        /// <summary>Raised when a request is dequeued and handed to the transport.</summary>
        event Action<HttpRequestContext> RequestStarted;

        /// <summary>Raised when a request completes successfully.</summary>
        event Action<HttpRequestContext> RequestCompleted;

        /// <summary>Raised when a request fails, is cancelled, or fails validation.</summary>
        event Action<HttpRequestContext> RequestFailed;

        /// <summary>Raised with the error message on connection-level failures.</summary>
        event Action<string> ConnectionError;

        /// <summary>
        /// Sends an HTTP request asynchronously. Cancelling the token aborts the
        /// in-flight request (or removes it from the pending queue) and the returned
        /// awaitable completes as cancelled.
        /// </summary>
        /// <param name="request">The request to send.</param>
        /// <param name="cancellationToken">Aborts the request when cancelled.</param>
        /// <returns>The response; never null.</returns>
        /// <exception cref="OperationCanceledException">The token was cancelled before completion.</exception>
        Awaitable<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends an HTTP request with callbacks. Cancelling the token aborts the
        /// in-flight request; <paramref name="onError"/> receives the cancellation reason.
        /// </summary>
        void Send(HttpRequest request,
            CancellationToken cancellationToken = default,
            Action<HttpResponse> onSuccess = null,
            Action<string> onError = null,
            Action<float> onProgress = null);

        /// <summary>Creates a fluent builder for a new HTTP request.</summary>
        HttpRequestBuilder CreateRequest();

        /// <summary>Adds a default header included in all requests (request headers win).</summary>
        void AddDefaultHeader(string key, string value);

        /// <summary>Removes a default header. No-op if absent.</summary>
        void RemoveDefaultHeader(string key);

        /// <summary>Clears all default headers.</summary>
        void ClearDefaultHeaders();

        /// <summary>Cancels all active requests and clears the pending queue.</summary>
        void CancelAllRequests();

        /// <summary>
        /// Registers an interceptor invoked on every outgoing request's prepared
        /// clone, just before transport send. Registration order = invocation order.
        /// </summary>
        void AddInterceptor(IHttpRequestInterceptor interceptor);

        /// <summary>Removes a previously registered interceptor. No-op if absent.</summary>
        void RemoveInterceptor(IHttpRequestInterceptor interceptor);

        /// <summary>
        /// Overrides the retry policy normally sourced from <see cref="HttpModule"/>.
        /// Pass <c>null</c> to restore module-driven configuration.
        /// </summary>
        void SetRetryPolicy(HttpRetryPolicy policy);

        /// <summary>
        /// Replaces the transport used to execute requests. Test seam — production
        /// code should keep the default <see cref="UnityWebRequestTransport"/>.
        /// </summary>
        /// <param name="transport">The transport to use; <c>null</c> restores the default.</param>
        void SetTransport(IHttpTransport transport);

        /// <summary>Clears the request history.</summary>
        void ClearHistory();
    }
}
