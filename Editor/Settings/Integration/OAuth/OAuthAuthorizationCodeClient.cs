using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Molca.Settings.Integration.OAuth
{
    /// <summary>
    /// Drives a provider-agnostic OAuth 2.0 authorization-code flow <b>with PKCE</b> over a loopback
    /// redirect — no embedded <c>client_secret</c>, no hosted callback.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/OAuth/</c>.
    /// Registration: instantiated by an <see cref="OAuthIntegrationProvider"/> (e.g. Figma); not an asset.
    /// <para>
    /// Flow: generate <see cref="PkceCodes"/> → start an <see cref="OAuthLoopbackListener"/> →
    /// <c>Application.OpenURL</c> the authorize URL (carrying the PKCE challenge + state + the listener's
    /// redirect URI) → await the redirect → exchange the returned <c>code</c> (plus the PKCE verifier) at
    /// the token endpoint. The HTTP exchange and browser opener are injectable seams so the flow's pieces
    /// are testable; the end-to-end browser leg is verified manually (no injectable live transport).
    /// </para>
    /// </remarks>
    public sealed class OAuthAuthorizationCodeClient
    {
        private readonly OAuthFormPoster _poster;
        private readonly Action<string> _openBrowser;
        private readonly Func<DateTime> _clock;

        /// <summary>Creates a client.</summary>
        /// <param name="poster">The token-endpoint POST seam; defaults to <see cref="OAuthHttp.DefaultPoster"/>.</param>
        /// <param name="openBrowser">Opens the authorize URL; defaults to <c>Application.OpenURL</c>.</param>
        /// <param name="clock">Supplies "now" for expiry math; defaults to <c>DateTime.UtcNow</c>.</param>
        public OAuthAuthorizationCodeClient(
            OAuthFormPoster poster = null, Action<string> openBrowser = null, Func<DateTime> clock = null)
        {
            _poster = poster ?? OAuthHttp.DefaultPoster;
            _openBrowser = openBrowser ?? Application.OpenURL;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>How long to wait for the user to complete the browser leg before giving up.</summary>
        public TimeSpan BrowserTimeout { get; set; } = TimeSpan.FromMinutes(3);

        /// <summary>
        /// Runs the full interactive authorization-code + PKCE flow and returns the resulting tokens.
        /// </summary>
        /// <param name="descriptor">The provider endpoints/client-id/scope (no secret).</param>
        /// <param name="cancellationToken">Cancels the flow; cancellation surfaces as <see cref="OAuthResult.Canceled"/>.</param>
        /// <returns>A success/failure/cancel result.</returns>
        public async Awaitable<OAuthResult> AuthorizeAsync(
            OAuthEndpointDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            descriptor.ValidateForAuthorizationCode();
            var pkce = PkceCodes.Generate();

            using var listener = new OAuthLoopbackListener();
            try
            {
                listener.Start();
            }
            catch (Exception e)
            {
                return OAuthResult.Fail($"Could not start the local callback listener: {e.Message}");
            }

            var authorizeUrl = BuildAuthorizeUrl(descriptor, pkce, listener.RedirectUri);
            _openBrowser(authorizeUrl);

            LoopbackResult redirect;
            try
            {
                redirect = await listener.WaitForCodeAsync(pkce.State, BrowserTimeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return OAuthResult.Cancel();
            }

            if (!redirect.Success)
                return OAuthResult.Fail(redirect.Error);

            // Exchange the code for tokens (PKCE verifier proves we started this flow).
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = redirect.Code,
                ["redirect_uri"] = listener.RedirectUri,
                ["client_id"] = descriptor.ClientId,
                ["code_verifier"] = pkce.CodeVerifier
            };

            return await ExchangeAsync(descriptor.TokenUrl, form, cancellationToken);
        }

        /// <summary>
        /// Exchanges a refresh token for a fresh access token.
        /// </summary>
        /// <param name="descriptor">The provider endpoints/client-id (no secret).</param>
        /// <param name="refreshToken">The refresh token from a prior bundle.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>A new token bundle, or a failure result.</returns>
        public async Awaitable<OAuthResult> RefreshAsync(
            OAuthEndpointDescriptor descriptor, string refreshToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(refreshToken))
                return OAuthResult.Fail("No refresh token available.");

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = descriptor.ClientId
            };

            var result = await ExchangeAsync(descriptor.TokenUrl, form, cancellationToken);
            // Providers that don't rotate the refresh token omit it from the refresh response — carry the
            // prior one forward so the bundle stays refreshable.
            if (result.Success && string.IsNullOrEmpty(result.Tokens.refreshToken))
                result.Tokens.refreshToken = refreshToken;
            return result;
        }

        private async Awaitable<OAuthResult> ExchangeAsync(
            string tokenUrl, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
        {
            OAuthHttpResult response;
            try
            {
                response = await _poster(tokenUrl, form, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return OAuthResult.Cancel();
            }

            if (string.IsNullOrEmpty(response.Body))
                return OAuthResult.Fail($"Token endpoint returned no body (HTTP {response.StatusCode}).");

            if (!OAuthHttp.TryParseTokenResponse(response.Body, _clock(), out var bundle, out var error))
                return OAuthResult.Fail(error);

            return OAuthResult.Ok(bundle);
        }

        /// <summary>
        /// Builds the authorize URL carrying client id, redirect uri, scope, state, and the PKCE challenge.
        /// </summary>
        /// <param name="descriptor">The provider configuration.</param>
        /// <param name="pkce">The generated PKCE/state values.</param>
        /// <param name="redirectUri">The loopback redirect URI.</param>
        /// <returns>The fully-qualified authorize URL.</returns>
        public static string BuildAuthorizeUrl(OAuthEndpointDescriptor descriptor, PkceCodes pkce, string redirectUri)
        {
            var query = new Dictionary<string, string>
            {
                ["client_id"] = descriptor.ClientId,
                ["redirect_uri"] = redirectUri,
                ["scope"] = descriptor.Scope ?? string.Empty,
                ["state"] = pkce.State,
                ["response_type"] = "code",
                ["code_challenge"] = pkce.CodeChallenge,
                ["code_challenge_method"] = pkce.CodeChallengeMethod
            };

            var separator = descriptor.AuthorizeUrl.Contains("?") ? "&" : "?";
            return descriptor.AuthorizeUrl + separator + OAuthHttp.EncodeForm(query);
        }
    }
}
