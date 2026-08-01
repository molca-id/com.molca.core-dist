using System.Threading;
using UnityEngine;
using Molca.Networking.Http.Models;
using Molca.Networking.Pipeline;
using Molca.Networking.Routing;

namespace Molca.Networking.Compatibility
{
    /// <summary>
    /// Executes a legacy <see cref="HttpRequest"/> through the routed pipeline and returns the
    /// <see cref="HttpResponse"/> the legacy caller expects.
    /// </summary>
    /// <remarks>
    /// The seam that lets <c>HttpClient</c> gain per-route policy, credential scoping, and typed
    /// diagnostics without any call site changing. It replaces only the transport-and-retry middle of a
    /// legacy send: the surrounding <see cref="HttpRequestContext"/>, events, request history, and
    /// interceptor invocation stay where they were, so nothing observable about the legacy API moves.
    /// <para>
    /// Failures are translated back into the legacy shape — a non-2xx exchange returns its response
    /// verbatim, and a pipeline refusal before the wire becomes a synthesized failed response, matching
    /// what the legacy client produced when the transport could not run. Cancellation still propagates as
    /// an <see cref="System.OperationCanceledException"/>, which is what the legacy path did too.
    /// </para>
    /// </remarks>
    public sealed class RoutedLegacyHttpAdapter
    {
        private readonly IRoutedHttpClient _client;

        /// <summary>Creates an adapter over a routed client.</summary>
        /// <param name="client">The routed client to send through.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
        public RoutedLegacyHttpAdapter(IRoutedHttpClient client)
        {
            _client = client ?? throw new System.ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// Sends a prepared legacy request on a route.
        /// </summary>
        /// <param name="route">The route the request maps to.</param>
        /// <param name="relativePath">Path relative to the service origin.</param>
        /// <param name="request">
        /// The prepared request — already cloned and interceptor-processed by the legacy client. Its
        /// headers and body travel; its <c>url</c> is replaced by the resolved route URI.
        /// </param>
        /// <param name="cancellationToken">Cancels the send.</param>
        /// <returns>The response, never <c>null</c>.</returns>
        /// <exception cref="System.OperationCanceledException">The token was cancelled.</exception>
        public async Awaitable<HttpResponse> SendAsync(
            NetworkRouteKey route,
            string relativePath,
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            // The legacy request's own query parameters are folded into the path here: the routed
            // pipeline clears queryParams on the transport request so FullUrl never re-reads global
            // settings, so a caller's AddParam would otherwise be dropped.
            string path = AppendQuery(relativePath, request);

            var outcome = await _client.SendAsync(
                route,
                request,
                NetworkRouteQuery.ForPath(path),
                cancellationToken);

            return Translate(outcome, request);
        }

        /// <summary>
        /// Folds a request's enabled query parameters onto a relative path.
        /// </summary>
        /// <returns>The path with a query string, or the path unchanged when there are no parameters.</returns>
        private static string AppendQuery(string relativePath, HttpRequest request)
        {
            string path = relativePath ?? string.Empty;
            if (request?.queryParams == null || request.queryParams.Count == 0)
                return path;

            var builder = new System.Text.StringBuilder();
            foreach (var parameter in request.queryParams)
            {
                if (parameter == null || !parameter.isEnabled || string.IsNullOrEmpty(parameter.key))
                    continue;

                builder.Append(builder.Length == 0 ? string.Empty : "&")
                    .Append(System.Uri.EscapeDataString(parameter.key))
                    .Append('=')
                    .Append(System.Uri.EscapeDataString(parameter.value ?? string.Empty));
            }

            if (builder.Length == 0)
                return path;

            // A path may already carry a query — the legacy FullUrl behaviour appends with '&' then.
            char separator = path.Contains("?") ? '&' : '?';
            return path + separator + builder;
        }

        /// <summary>
        /// Converts a routed outcome into the legacy response shape.
        /// </summary>
        /// <param name="outcome">The routed outcome.</param>
        /// <param name="request">The request that was sent, for the synthesized status message.</param>
        /// <returns>The response; never <c>null</c>.</returns>
        /// <remarks>
        /// When the pipeline completed an exchange, its own <see cref="HttpResponse"/> is returned
        /// untouched — same status, headers, and body the legacy transport would have produced, because
        /// it is the same transport. Only a refusal before the wire (route resolution, security policy,
        /// a tripped circuit, a full queue) has no response to return, and is synthesized so callers
        /// that read <c>errorMessage</c> still see why.
        /// </remarks>
        public static HttpResponse Translate(RoutedHttpOutcome outcome, HttpRequest request)
        {
            if (outcome == null)
            {
                return new HttpResponse
                {
                    isSuccess = false,
                    errorMessage = "The routed pipeline returned no outcome.",
                    statusMessage = "No outcome"
                };
            }

            if (outcome.Response != null)
                return outcome.Response;

            return new HttpResponse
            {
                isSuccess = false,
                statusCode = outcome.StatusCode,
                statusMessage = outcome.Category.ToString(),
                errorMessage = string.IsNullOrEmpty(outcome.Message)
                    ? $"The routed pipeline refused {request?.method} {outcome.Route} ({outcome.Category})."
                    : outcome.Message,
                exception = outcome.Cause
            };
        }
    }
}
