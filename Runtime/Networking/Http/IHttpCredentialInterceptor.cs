namespace Molca.Networking.Http
{
    /// <summary>
    /// An <see cref="IHttpRequestInterceptor"/> that attaches a credential, declaring which header it
    /// writes so the client can withhold it from destinations the project has not authorized.
    /// </summary>
    /// <remarks>
    /// Interceptors are process-wide: one registration applies to every outgoing request, including a
    /// <c>useFullUrl</c> request to a third-party host. That is the credential-leak boundary plan §2.1
    /// item 2 identifies — a global token injector cannot tell "our backend" from "some vendor's API".
    /// <para>
    /// Implementing this interface lets <see cref="HttpClient"/> skip the interceptor for a request whose
    /// host no catalog service claims, instead of injecting a credential and hoping. An interceptor that
    /// does not implement it is always run, which preserves existing behaviour for everything that is not
    /// about credentials.
    /// </para>
    /// <para>
    /// Additive — introduced with the routed networking catalog. Existing interceptors need no change.
    /// </para>
    /// </remarks>
    public interface IHttpCredentialInterceptor : IHttpRequestInterceptor
    {
        /// <summary>
        /// The header this interceptor may populate with a credential, e.g. <c>Authorization</c>.
        /// </summary>
        /// <remarks>
        /// Reported for diagnostics and warnings. The client's decision to skip the interceptor is based
        /// on the destination, not on this value, so returning <c>null</c> suppresses the interceptor on
        /// unauthorized destinations just the same — it only makes the warning less specific.
        /// </remarks>
        string CredentialHeaderName { get; }
    }
}
