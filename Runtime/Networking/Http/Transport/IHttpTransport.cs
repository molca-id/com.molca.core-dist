using System;
using System.Threading;
using UnityEngine;
using Molca.Networking.Http.Models;

namespace Molca.Networking.Http
{
    /// <summary>
    /// Low-level transport used by <see cref="HttpClient"/> to execute a single
    /// HTTP request. The default implementation is <see cref="UnityWebRequestTransport"/>;
    /// tests substitute a mock via <see cref="HttpClient.SetTransport"/>.
    /// </summary>
    /// <remarks>
    /// The request passed in is fully prepared (default headers merged, timeout
    /// resolved) — implementations must not mutate it. Implementations must throw
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
    /// is cancelled mid-flight.
    /// </remarks>
    public interface IHttpTransport
    {
        /// <summary>
        /// Executes the request and returns the response.
        /// </summary>
        /// <param name="request">The prepared request to send. Treated as read-only.</param>
        /// <param name="onProgress">Optional per-frame download progress callback (0..1).</param>
        /// <param name="cancellationToken">Aborts the in-flight request when cancelled.</param>
        /// <returns>The response; <c>isSuccess</c> is <c>false</c> for HTTP/connection failures.</returns>
        Awaitable<HttpResponse> SendAsync(HttpRequest request, Action<float> onProgress, CancellationToken cancellationToken);
    }
}
