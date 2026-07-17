using System;
using System.Threading;
using UnityEngine;
using Molca.Networking.Http;
using Molca.Networking.Http.Models;

namespace Molca.Networking.Auth
{
    /// <summary>
    /// Fills a declared auth-token header on outgoing requests with the current
    /// token, and transparently recovers a single 401 by refreshing the token.
    /// Replaces the legacy <c>AuthManager.TryApplyToken</c> flow: instead of mutating
    /// caller-owned (often ScriptableObject-backed) requests, the token is injected
    /// into the per-send clone by <see cref="HttpClient"/>.
    /// </summary>
    /// <remarks>
    /// Opt-in per request: the header value is only set when the request already
    /// declares a header with the configured key (matching the old TryApplyToken
    /// semantics). Requests without the header are left untouched. As an
    /// <see cref="IHttpResponseInterceptor"/>, a 401 on such an auth-bearing request
    /// triggers a single-flight <see cref="AuthManager.RefreshAsync"/>; on success the
    /// request is retried once (re-injecting the refreshed token), otherwise an
    /// <c>AuthExpired</c> event is raised and the 401 surfaces. Auth-retry is capped at
    /// once per request. Registered by <see cref="AuthManager"/> during initialization.
    /// </remarks>
    public class AuthTokenInterceptor : IHttpContextAwareRequestInterceptor, IHttpResponseInterceptor
    {
        private readonly string _headerKey;
        private readonly Func<string> _tokenProvider;
        private readonly Func<CancellationToken, Awaitable<bool>> _refreshAsync;
        private readonly Action _onAuthExpired;

        /// <param name="headerKey">Header key that marks a request as wanting the token.</param>
        /// <param name="tokenProvider">Returns the current token, or <c>null</c> when unauthenticated.</param>
        /// <param name="refreshAsync">
        /// Single-flight token refresh invoked on a 401; <c>null</c> disables 401 recovery
        /// (the interceptor then behaves as a request-only token injector).
        /// </param>
        /// <param name="onAuthExpired">Invoked when a 401 could not be recovered by refresh.</param>
        public AuthTokenInterceptor(
            string headerKey,
            Func<string> tokenProvider,
            Func<CancellationToken, Awaitable<bool>> refreshAsync = null,
            Action onAuthExpired = null)
        {
            _headerKey = headerKey;
            _tokenProvider = tokenProvider;
            _refreshAsync = refreshAsync;
            _onAuthExpired = onAuthExpired;
        }

        public void OnRequestPrepared(HttpRequest request) => InjectToken(request);

        public void OnRequestPrepared(HttpRequestContext context, HttpRequest request)
        {
            // Record which token this request actually carries so the 401 handler can
            // tell a stale token (already refreshed elsewhere — just retry) from a
            // rejected current token (needs a refresh).
            context.AuthTokenSent = InjectToken(request);
        }

        /// <returns>The injected token, or <c>null</c> when nothing was injected.</returns>
        private string InjectToken(HttpRequest request)
        {
            if (string.IsNullOrEmpty(_headerKey))
                return null;

            // Opt-in: only requests that declare the header receive the token.
            if (request.GetHeaderValue(_headerKey) == null)
                return null;

            string token = _tokenProvider?.Invoke();
            if (string.IsNullOrEmpty(token))
                return null;

            request.AddHeader(_headerKey, token);
            return token;
        }

        public async Awaitable<ResponseAction> OnResponseReceivedAsync(HttpRequestContext context, HttpResponse response, CancellationToken cancellationToken)
        {
            // Only react to a 401 on a request that opted into auth (declared the header),
            // when refresh is wired and we haven't already retried this request.
            if (_refreshAsync == null || response == null || response.statusCode != 401)
                return ResponseAction.Continue;
            if (string.IsNullOrEmpty(_headerKey) || context.request.GetHeaderValue(_headerKey) == null)
                return ResponseAction.Continue;
            if (context.AuthRetryConsumed)
                return ResponseAction.Continue;

            context.AuthRetryConsumed = true;

            // Token-version check: if the token has already changed since this request
            // was sent (another 401 triggered the refresh, or a login happened), the
            // 401 only proves the OLD token is dead. Retry with the current token
            // instead of chaining another refresh — N staggered 401s must produce at
            // most one refresh, not N (which can kill rotating-refresh-token sessions).
            string currentToken = _tokenProvider?.Invoke();
            if (!string.IsNullOrEmpty(context.AuthTokenSent)
                && !string.IsNullOrEmpty(currentToken)
                && !string.Equals(currentToken, context.AuthTokenSent, StringComparison.Ordinal))
            {
                return ResponseAction.RetryOnce;
            }

            bool refreshed;
            try
            {
                refreshed = await _refreshAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation is not an error
            }
            catch (Exception e)
            {
                Debug.LogError($"[AuthTokenInterceptor] Token refresh threw: {e.Message}");
                refreshed = false;
            }

            if (refreshed)
                return ResponseAction.RetryOnce;

            // Refresh impossible/failed — the session is dead; surface the 401.
            _onAuthExpired?.Invoke();
            return ResponseAction.Continue;
        }
    }
}
