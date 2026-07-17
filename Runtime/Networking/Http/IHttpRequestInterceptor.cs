using Molca.Networking.Http.Models;

namespace Molca.Networking.Http
{
    /// <summary>
    /// Hook invoked by <see cref="HttpClient"/> on every outgoing request just before
    /// it is handed to the transport. The canonical use is auth-header injection
    /// (see <see cref="Molca.Networking.Auth.AuthTokenInterceptor"/>), replacing the
    /// legacy <c>AuthManager.TryApplyToken</c> mutation of caller-owned requests.
    /// </summary>
    /// <remarks>
    /// Interceptors receive the prepared per-send clone — mutations never reach the
    /// caller's request instance or any <see cref="HttpRequestAsset"/>. They run in
    /// registration order; an exception in one interceptor is logged and the rest
    /// still run. Register via <see cref="HttpClient.AddInterceptor"/>.
    /// </remarks>
    public interface IHttpRequestInterceptor
    {
        /// <summary>
        /// Inspects or mutates the prepared request before it is sent.
        /// </summary>
        /// <param name="request">The per-send request clone (default headers already merged).</param>
        void OnRequestPrepared(HttpRequest request);
    }

    /// <summary>
    /// Optional extension of <see cref="IHttpRequestInterceptor"/> for interceptors
    /// that need the <see cref="HttpRequestContext"/> alongside the prepared clone —
    /// e.g. to stash per-request state read back in a response interceptor
    /// (<see cref="Molca.Networking.Auth.AuthTokenInterceptor"/> records which token
    /// was actually sent, so a 401 can tell "stale token" from "dead session").
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient"/> prefers this overload when the registered interceptor
    /// implements it; plain <see cref="IHttpRequestInterceptor"/> implementations are
    /// unaffected. Additive — introduced by Sprint 83.
    /// </remarks>
    public interface IHttpContextAwareRequestInterceptor : IHttpRequestInterceptor
    {
        /// <summary>
        /// Inspects or mutates the prepared request before it is sent, with access to
        /// the request's context.
        /// </summary>
        /// <param name="context">The context tracking this request across its lifetime (including retries).</param>
        /// <param name="request">The per-send request clone (default headers already merged).</param>
        void OnRequestPrepared(HttpRequestContext context, HttpRequest request);
    }
}
