using System.Threading;
using UnityEngine;

namespace Molca.Settings.Integration.OAuth
{
    /// <summary>
    /// Base class for integrations that authenticate via OAuth, layering token-bundle storage, silent
    /// refresh, and an interactive authorization hook on top of <see cref="IntegrationProvider"/>.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/OAuth/</c>.
    /// Base class: <see cref="IntegrationProvider"/> (additive subclass — the Core base is untouched).
    /// Registration: concrete subclasses (e.g. <c>GitHubIntegrationProvider</c>,
    /// <c>FigmaIntegrationProvider</c>) are added to <see cref="IntegrationSettings"/> like any provider.
    /// <para>
    /// Tokens live in <see cref="OAuthCredentialStore"/> (access + refresh + expiry), never on this
    /// ScriptableObject. A subclass supplies its endpoint descriptor via <see cref="BuildDescriptor"/> and
    /// implements <see cref="BeginAuthorizationAsync"/> with the flow that fits the provider (device flow
    /// for GitHub, loopback+PKCE for Figma). <see cref="GetFreshAccessTokenAsync"/> returns a usable token,
    /// transparently refreshing one that is near expiry. The existing PAT fallback in each subclass is
    /// preserved — OAuth is additive.
    /// </para>
    /// </remarks>
    public abstract class OAuthIntegrationProvider : IntegrationProvider
    {
        /// <summary>True when an OAuth token bundle is stored for this provider (regardless of expiry).</summary>
        public bool HasOAuthTokens => OAuthCredentialStore.HasTokens(ProviderKey);

        /// <summary>True when a stored OAuth access token exists and is not past its expiry.</summary>
        public bool HasValidOAuthToken => OAuthCredentialStore.HasValidAccessToken(ProviderKey);

        /// <summary>
        /// Builds the provider's OAuth endpoint/client-id/scope descriptor (no secret).
        /// </summary>
        /// <returns>A fresh descriptor describing this provider's OAuth endpoints.</returns>
        protected abstract OAuthEndpointDescriptor BuildDescriptor();

        /// <summary>
        /// Runs the provider's interactive authorization flow and persists the resulting tokens on success.
        /// </summary>
        /// <param name="cancellationToken">Cancels the flow; cancellation is not an error.</param>
        /// <returns>The flow result (success/failure/cancel).</returns>
        public abstract Awaitable<OAuthResult> BeginAuthorizationAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns a usable OAuth access token, silently refreshing a near-expiry one, or <c>null</c> when
        /// no OAuth token is stored.
        /// </summary>
        /// <param name="cancellationToken">Cancels a refresh request.</param>
        /// <returns>A bearer access token, or <c>null</c> if none is available.</returns>
        /// <remarks>
        /// If a refresh fails (e.g. the refresh token was revoked) the prior access token is returned so
        /// the caller's API call surfaces the real auth failure; the connect path then falls back to
        /// re-authorization. A provider whose tokens never expire (no <c>expires_in</c>) is returned as-is.
        /// </remarks>
        protected async Awaitable<string> GetFreshAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            var bundle = OAuthCredentialStore.GetTokens(ProviderKey);
            if (bundle == null || !bundle.HasAccessToken)
                return null;

            if (!OAuthCredentialStore.IsExpiringSoon(ProviderKey) || string.IsNullOrEmpty(bundle.refreshToken))
                return bundle.accessToken;

            var client = new OAuthAuthorizationCodeClient();
            var refreshed = await client.RefreshAsync(BuildDescriptor(), bundle.refreshToken, cancellationToken);
            if (refreshed.Success)
            {
                OAuthCredentialStore.SetTokens(ProviderKey, refreshed.Tokens);
                return refreshed.Tokens.accessToken;
            }

            // Refresh failed — keep the stale token so the API call reports the real failure and the UI
            // can offer re-authorization.
            return bundle.accessToken;
        }

        /// <summary>Persists a freshly-obtained token bundle and refreshes derived state.</summary>
        /// <param name="bundle">The bundle to store.</param>
        protected void StoreTokens(OAuthTokenBundle bundle)
        {
            OAuthCredentialStore.SetTokens(ProviderKey, bundle);
            OnCredentialsChanged();
        }

        /// <summary>
        /// Clears stored OAuth tokens and any cached session state for this provider.
        /// </summary>
        public virtual void SignOut()
        {
            OAuthCredentialStore.Clear(ProviderKey);
            OnCredentialsChanged();
        }

        /// <summary>
        /// Hook invoked whenever stored credentials change (store/sign-out), so a subclass can reset its
        /// session-scoped connection cache. Default is a no-op.
        /// </summary>
        protected virtual void OnCredentialsChanged() { }
    }
}
