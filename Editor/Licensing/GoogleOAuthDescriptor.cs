using Molca.Settings.Integration.OAuth;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Builds the <see cref="OAuthEndpointDescriptor"/> for Google's OAuth 2.0 endpoints, reusing
    /// Core's provider-agnostic OAuth clients (<see cref="OAuthAuthorizationCodeClient"/> for the
    /// loopback+PKCE flow, <see cref="OAuthDeviceFlowClient"/> as a fallback). The descriptor contains
    /// only public browser-leg configuration; the control plane supplies the client secret when it
    /// exchanges a licensing authorization code.
    /// </summary>
    internal static class GoogleOAuthDescriptor
    {
        private const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string TokenUrl = "https://oauth2.googleapis.com/token";
        private const string DeviceCodeUrl = "https://oauth2.googleapis.com/device/code";

        /// <summary>Creates a descriptor from the distribution's <see cref="DevLicenseConfig"/>.</summary>
        public static OAuthEndpointDescriptor Create() => new OAuthEndpointDescriptor
        {
            ClientId = DevLicenseConfig.GoogleClientId,
            Scope = DevLicenseConfig.GoogleScope,
            AuthorizeUrl = AuthorizeUrl,
            TokenUrl = TokenUrl,
            DeviceCodeUrl = DeviceCodeUrl,
        };
    }
}
