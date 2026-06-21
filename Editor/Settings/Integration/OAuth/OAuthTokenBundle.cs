using System;

namespace Molca.Settings.Integration.OAuth
{
    /// <summary>
    /// The persisted result of an OAuth flow: access token plus the refresh/expiry metadata a PAT
    /// never carried.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/OAuth/</c>.
    /// Persisted as a small JSON blob by <see cref="OAuthCredentialStore"/> (backed by
    /// <c>EditorUserSettings</c>) — <b>never</b> on a ScriptableObject (Sprints 4.5 / 14.5 secret rule).
    /// <para>
    /// <see cref="ExpiresAtUtc"/> is stored as a round-trippable ISO-8601 (<c>"o"</c>) string so the
    /// bundle is self-describing across editor sessions. An empty/absent expiry means "never expires"
    /// (e.g. a non-expiring access token), and <see cref="HasExpiry"/> reflects that.
    /// </para>
    /// </remarks>
    [Serializable]
    public class OAuthTokenBundle
    {
        /// <summary>The bearer access token used to authorize API calls.</summary>
        public string accessToken;

        /// <summary>The refresh token used to mint a fresh access token, when the provider issues one.</summary>
        public string refreshToken;

        /// <summary>Absolute UTC expiry as an ISO-8601 round-trip (<c>"o"</c>) string; empty when non-expiring.</summary>
        public string expiresAtUtc;

        /// <summary>The space-delimited scope string the provider granted.</summary>
        public string scope;

        /// <summary>The token type the provider returned (typically <c>"bearer"</c>).</summary>
        public string tokenType;

        /// <summary>True when a non-empty access token is present.</summary>
        public bool HasAccessToken => !string.IsNullOrEmpty(accessToken);

        /// <summary>True when the bundle carries an expiry timestamp (some access tokens never expire).</summary>
        public bool HasExpiry => TryGetExpiry(out _);

        /// <summary>
        /// Parses <see cref="expiresAtUtc"/> into a <see cref="DateTime"/>.
        /// </summary>
        /// <param name="value">The parsed UTC expiry when this returns <c>true</c>.</param>
        /// <returns><c>true</c> if an expiry is present and parseable; otherwise <c>false</c>.</returns>
        public bool TryGetExpiry(out DateTime value)
        {
            value = default;
            if (string.IsNullOrEmpty(expiresAtUtc))
                return false;
            return DateTime.TryParse(
                expiresAtUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out value);
        }

        /// <summary>
        /// Builds a bundle from a token-endpoint response, computing the absolute expiry from
        /// <paramref name="expiresInSeconds"/> relative to <paramref name="nowUtc"/>.
        /// </summary>
        /// <param name="accessToken">The granted access token.</param>
        /// <param name="refreshToken">The granted refresh token, if any.</param>
        /// <param name="expiresInSeconds">Lifetime in seconds; <c>0</c> or negative means non-expiring.</param>
        /// <param name="scope">The granted scope string.</param>
        /// <param name="tokenType">The token type the provider returned.</param>
        /// <param name="nowUtc">The reference "now" (injected so callers/tests are deterministic).</param>
        /// <returns>A populated bundle.</returns>
        public static OAuthTokenBundle FromResponse(
            string accessToken, string refreshToken, long expiresInSeconds,
            string scope, string tokenType, DateTime nowUtc)
        {
            return new OAuthTokenBundle
            {
                accessToken = accessToken,
                refreshToken = refreshToken,
                expiresAtUtc = expiresInSeconds > 0
                    ? nowUtc.AddSeconds(expiresInSeconds).ToUniversalTime().ToString("o")
                    : string.Empty,
                scope = scope,
                tokenType = tokenType
            };
        }
    }
}
