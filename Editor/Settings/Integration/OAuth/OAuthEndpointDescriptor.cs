using System;

namespace Molca.Settings.Integration.OAuth
{
    /// <summary>
    /// The provider-specific, non-secret configuration that parameterizes an OAuth flow: endpoints,
    /// public <c>client_id</c>, and requested scopes. Carries <b>no</b> <c>client_secret</c> — every flow
    /// Sprint 32 ships (loopback+PKCE, device) is designed to need none.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/OAuth/</c>.
    /// One descriptor type serves both <see cref="OAuthAuthorizationCodeClient"/> (uses
    /// <see cref="AuthorizeUrl"/> + <see cref="TokenUrl"/>) and <see cref="OAuthDeviceFlowClient"/> (uses
    /// <see cref="DeviceCodeUrl"/> + <see cref="TokenUrl"/>). A provider builds one from its own constants.
    /// </remarks>
    public sealed class OAuthEndpointDescriptor
    {
        /// <summary>The public OAuth application client id (not a secret).</summary>
        public string ClientId { get; set; }

        /// <summary>The space-delimited scopes to request.</summary>
        public string Scope { get; set; }

        /// <summary>The authorization endpoint (authorization-code flow): where the browser is opened.</summary>
        public string AuthorizeUrl { get; set; }

        /// <summary>The token endpoint: exchanges a code/device-code/refresh-token for an access token.</summary>
        public string TokenUrl { get; set; }

        /// <summary>The device-authorization endpoint (device flow); null for authorization-code providers.</summary>
        public string DeviceCodeUrl { get; set; }

        /// <summary>Validates that the fields an authorization-code flow needs are present.</summary>
        /// <exception cref="InvalidOperationException">Thrown when a required field is missing.</exception>
        public void ValidateForAuthorizationCode()
        {
            if (string.IsNullOrEmpty(ClientId)) throw new InvalidOperationException("OAuth descriptor is missing ClientId.");
            if (string.IsNullOrEmpty(AuthorizeUrl)) throw new InvalidOperationException("OAuth descriptor is missing AuthorizeUrl.");
            if (string.IsNullOrEmpty(TokenUrl)) throw new InvalidOperationException("OAuth descriptor is missing TokenUrl.");
        }

        /// <summary>Validates that the fields a device flow needs are present.</summary>
        /// <exception cref="InvalidOperationException">Thrown when a required field is missing.</exception>
        public void ValidateForDeviceFlow()
        {
            if (string.IsNullOrEmpty(ClientId)) throw new InvalidOperationException("OAuth descriptor is missing ClientId.");
            if (string.IsNullOrEmpty(DeviceCodeUrl)) throw new InvalidOperationException("OAuth descriptor is missing DeviceCodeUrl.");
            if (string.IsNullOrEmpty(TokenUrl)) throw new InvalidOperationException("OAuth descriptor is missing TokenUrl.");
        }
    }

    /// <summary>The outcome of an interactive OAuth attempt: a token bundle or a failure/cancel reason.</summary>
    public readonly struct OAuthResult
    {
        private OAuthResult(bool success, bool canceled, OAuthTokenBundle tokens, string error)
        {
            Success = success;
            Canceled = canceled;
            Tokens = tokens;
            Error = error;
        }

        /// <summary>True when a token bundle was obtained.</summary>
        public bool Success { get; }

        /// <summary>True when the user/caller canceled (not an error condition).</summary>
        public bool Canceled { get; }

        /// <summary>The token bundle when <see cref="Success"/> is true.</summary>
        public OAuthTokenBundle Tokens { get; }

        /// <summary>The failure description when <see cref="Success"/> is false and not canceled.</summary>
        public string Error { get; }

        /// <summary>Creates a success result.</summary>
        public static OAuthResult Ok(OAuthTokenBundle tokens) => new OAuthResult(true, false, tokens, null);

        /// <summary>Creates a failure result.</summary>
        public static OAuthResult Fail(string error) => new OAuthResult(false, false, null, error);

        /// <summary>Creates a canceled result.</summary>
        public static OAuthResult Cancel() => new OAuthResult(false, true, null, "Canceled.");
    }
}
