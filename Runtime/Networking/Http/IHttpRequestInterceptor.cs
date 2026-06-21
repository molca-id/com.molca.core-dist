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
}
