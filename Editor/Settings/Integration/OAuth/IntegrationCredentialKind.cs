namespace Molca.Settings.Integration.OAuth
{
    /// <summary>
    /// Distinguishes the kind of credential an API client was handed, so it can send the correct auth
    /// header. Some providers (notably Figma) authorize a personal access token and an OAuth token through
    /// <b>different</b> headers, so the client cannot infer the scheme from the token string alone.
    /// </summary>
    public enum IntegrationCredentialKind
    {
        /// <summary>A user-supplied personal access token (the pre-OAuth path).</summary>
        PersonalAccessToken,

        /// <summary>An OAuth access token obtained via a device or authorization-code flow.</summary>
        OAuth
    }
}
