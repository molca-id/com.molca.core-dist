using System.Threading;
using UnityEngine;
using Molca.Networking.Http.Models;

namespace Molca.Networking.Http
{
    /// <summary>
    /// What an <see cref="IHttpResponseInterceptor"/> asks <see cref="HttpClient"/> to do
    /// after inspecting a response.
    /// </summary>
    public enum ResponseAction
    {
        /// <summary>Accept the response as-is and return it to the caller.</summary>
        Continue,

        /// <summary>
        /// Re-send the original request exactly once (the request is re-prepared, so
        /// request interceptors run again — e.g. re-injecting a refreshed auth token).
        /// </summary>
        RetryOnce
    }

    /// <summary>
    /// Optional hook invoked by <see cref="HttpClient"/> after a response is received,
    /// letting a participant (e.g. the auth layer) react to it — the canonical use is
    /// transparent 401 recovery via <see cref="Molca.Networking.Auth.AuthTokenInterceptor"/>.
    /// </summary>
    /// <remarks>
    /// Added as a <b>separate</b> interface rather than a new member on the shipped
    /// <see cref="IHttpRequestInterceptor"/> so existing request-only implementors keep
    /// compiling (protected-zone rule). An interceptor that implements this interface is
    /// registered for response callbacks automatically by
    /// <see cref="HttpClient.AddInterceptor"/> when it is added. The client honors at most
    /// one <see cref="ResponseAction.RetryOnce"/> per request to prevent retry loops.
    /// Implementations must honor the <see cref="CancellationToken"/> and treat
    /// cancellation as non-error.
    /// </remarks>
    public interface IHttpResponseInterceptor
    {
        /// <summary>
        /// Inspects a received response and decides whether to accept it or retry once.
        /// </summary>
        /// <param name="context">The request context (the original request, timing, caller token).</param>
        /// <param name="response">The response received from the transport.</param>
        /// <param name="cancellationToken">Cancelled when the request or subsystem is torn down.</param>
        /// <returns><see cref="ResponseAction.Continue"/> or <see cref="ResponseAction.RetryOnce"/>.</returns>
        Awaitable<ResponseAction> OnResponseReceivedAsync(HttpRequestContext context, HttpResponse response, CancellationToken cancellationToken);
    }
}
