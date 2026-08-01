using System.Threading;
using UnityEngine;
using Molca.Networking.Http.Models;
using Molca.Networking.Pipeline;
using Molca.Networking.Routing;

namespace Molca.Networking
{
    /// <summary>
    /// Sends HTTP requests against a route rather than a process-wide base URL. Resolve via
    /// <c>RuntimeManager.GetService&lt;IRoutedHttpClient&gt;()</c> or inject with
    /// <c>[Inject] IRoutedHttpClient</c>.
    /// </summary>
    /// <remarks>
    /// Additive to <see cref="Http.IHttpClient"/>, which is unchanged and keeps working. Prefer this for
    /// new code: it reaches several environments concurrently, returns a typed outcome instead of
    /// requiring string inspection, and enforces credential scope per request.
    /// <para>
    /// Failures are <b>returned</b>, not thrown — branch on <see cref="RoutedHttpOutcome.Category"/>.
    /// Only cancellation surfaces as an <see cref="System.OperationCanceledException"/>, because a
    /// cancelled send has no outcome to describe.
    /// </para>
    /// </remarks>
    public interface IRoutedHttpClient
    {
        /// <summary>
        /// Sends a request to a route.
        /// </summary>
        /// <param name="route">The environment/service pair to target.</param>
        /// <param name="request">
        /// The request to send. Its <c>url</c> is treated as a path relative to the service origin unless
        /// an endpoint is named in <paramref name="query"/>. Never mutated — the pipeline works on a
        /// prepared copy.
        /// </param>
        /// <param name="query">Protocol, endpoint, path, and per-send policy override.</param>
        /// <param name="cancellationToken">
        /// Cancels the send. A queued request leaves the queue within a frame and is never sent.
        /// </param>
        /// <returns>The outcome; never <c>null</c>.</returns>
        /// <exception cref="System.OperationCanceledException">The token was cancelled before completion.</exception>
        Awaitable<RoutedHttpOutcome> SendAsync(
            NetworkRouteKey route,
            HttpRequest request,
            NetworkRouteQuery query = default,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a request to a service in the catalog's default environment.
        /// </summary>
        /// <param name="serviceId">The service to reach.</param>
        /// <param name="request">The request to send.</param>
        /// <param name="query">Protocol, endpoint, path, and per-send policy override.</param>
        /// <param name="cancellationToken">Cancels the send.</param>
        /// <returns>The outcome; never <c>null</c>.</returns>
        /// <remarks>
        /// A convenience for single-environment call sites. It reads the default from the immutable
        /// snapshot, so it does not introduce mutable global state — changing the catalog default does not
        /// re-target requests already in flight.
        /// </remarks>
        Awaitable<RoutedHttpOutcome> SendToServiceAsync(
            string serviceId,
            HttpRequest request,
            NetworkRouteQuery query = default,
            CancellationToken cancellationToken = default);

        /// <summary>The resolver this client uses. Reads an immutable catalog snapshot.</summary>
        INetworkRouteResolver Resolver { get; }

        /// <summary>Registers a send observer. Observer exceptions never affect a request.</summary>
        /// <param name="observer">The observer to add.</param>
        void AddObserver(IRoutedHttpObserver observer);

        /// <summary>Removes a send observer. No-op when absent.</summary>
        /// <param name="observer">The observer to remove.</param>
        void RemoveObserver(IRoutedHttpObserver observer);

        /// <summary>
        /// Replaces the transport used to execute attempts. Test seam — production code keeps the
        /// default <see cref="Http.UnityWebRequestTransport"/>.
        /// </summary>
        /// <param name="transport">The transport to use; <c>null</c> restores the default.</param>
        void SetTransport(Http.IHttpTransport transport);
    }
}
