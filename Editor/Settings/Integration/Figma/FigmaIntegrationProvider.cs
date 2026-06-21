using System;
using System.Threading;
using Molca.Settings.Integration.OAuth;
using UnityEngine;

namespace Molca.Settings.Integration.Figma
{
    /// <summary>
    /// Figma integration: connects with a personal access token and ingests designs into the project.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/Figma/</c>.
    /// Base class: <see cref="IntegrationProvider"/>.
    /// Registration: add the asset to <see cref="IntegrationSettings"/>' provider list (or use the Hub
    /// "+ Add integration" affordance, which discovers this type automatically). The secret token is stored in
    /// <see cref="IntegrationCredentialStore"/> (per-machine, never committed); only non-secret config (default
    /// file key, team id, output folder) is serialized on the asset.
    /// <para>
    /// Figma is a <b>read-only ingest</b> integration: it lists files/frames and scaffolds UI Toolkit assets,
    /// and never pushes build/release activity — so the routing-seam push overrides
    /// (<see cref="IntegrationProvider.PushBuildActivityAsync"/> /
    /// <see cref="IntegrationProvider.PushReleaseActivityAsync"/>) keep their no-op defaults.
    /// Connection state (<see cref="IsConnected"/>) is session-scoped: it reflects a token validated via
    /// <see cref="ConnectAsync"/> during this editor session and resets on domain reload — it never makes a
    /// network call on the render path.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Figma Integration", menuName = "Molca/Editor/Integrations/Figma", order = 110)]
    public class FigmaIntegrationProvider : OAuthIntegrationProvider
    {
        /// <summary>Default folder generated UI Toolkit assets are written to when none is specified.</summary>
        public const string DefaultOutputFolderConst = "Assets/FigmaGenerated";

        // Figma's OAuth endpoints. Figma supports PKCE, so the authorization-code exchange needs no
        // client_secret — shippable in a distributable editor tool via a loopback redirect (Sprint 32).
        private const string AuthorizeUrl = "https://www.figma.com/oauth";
        private const string TokenUrl = "https://api.figma.com/v1/oauth/token";

        [Header("OAuth (loopback + PKCE)")]
        [Tooltip("Public client id of a Figma OAuth app used for browser sign-in. Not a secret. " +
                 "Leave empty to use only a personal access token.")]
        [SerializeField] private string oauthClientId;

        [Tooltip("Space-delimited OAuth scopes to request (e.g. \"file_read\").")]
        [SerializeField] private string oauthScope = "file_read";

        [Header("Defaults")]
        [Tooltip("Figma file key the MCP tools default to when none is supplied.")]
        [SerializeField] private string defaultFileKey;

        [Tooltip("Figma team id used to list projects/files. Found in the team URL: figma.com/files/team/<id>/...")]
        [SerializeField] private string teamId;

        [Tooltip("Project-relative folder generated UXML/USS/sprites are written to.")]
        [SerializeField] private string outputFolder = DefaultOutputFolderConst;

        // Session-scoped cache; not serialized (resets on domain reload, as ConnectAsync repopulates it).
        [NonSerialized] private bool _connected;
        [NonSerialized] private string _connectedName;

        /// <inheritdoc/>
        public override string DisplayName => "Figma";

        /// <inheritdoc/>
        public override string Description => "Import designs to UI Toolkit";

        /// <inheritdoc/>
        public override string Glyph => "F";

        /// <inheritdoc/>
        public override string GlyphColor => "rgb(162, 89, 255)";

        /// <summary>The Figma file key the MCP tools fall back to when none is supplied.</summary>
        public string DefaultFileKey => defaultFileKey;

        /// <summary>The Figma team id used to list projects and files.</summary>
        public string TeamId => teamId;

        /// <summary>The project-relative folder generated assets are written to (never empty).</summary>
        public string OutputFolder =>
            string.IsNullOrWhiteSpace(outputFolder) ? DefaultOutputFolderConst : outputFolder;

        /// <inheritdoc/>
        public override bool IsConnected => _connected;

        /// <inheritdoc/>
        public override string StatusMessage
        {
            get
            {
                if (_connected)
                    return string.IsNullOrEmpty(_connectedName) ? "Connected" : $"Connected as {_connectedName}";
                if (HasOAuthTokens)
                    return "Signed in via OAuth — not verified";
                if (HasPersonalAccessToken)
                    return "Token saved — not verified";
                return "Not configured";
            }
        }

        /// <summary>Whether a personal access token is stored (the pre-OAuth path).</summary>
        public bool HasPersonalAccessToken => IntegrationCredentialStore.HasToken(ProviderKey);

        /// <summary>Whether any usable credential (OAuth tokens or a PAT) is stored.</summary>
        public bool HasToken => HasOAuthTokens || HasPersonalAccessToken;

        /// <summary>The credential kind preferred for API calls: OAuth when present, else PAT.</summary>
        public IntegrationCredentialKind CredentialKind =>
            HasOAuthTokens ? IntegrationCredentialKind.OAuth : IntegrationCredentialKind.PersonalAccessToken;

        /// <summary>Whether a Figma OAuth app client id is configured (browser sign-in is available).</summary>
        public bool SupportsOAuth => !string.IsNullOrEmpty(oauthClientId);

        /// <summary>Stores the personal access token. Pass null/empty to clear it; does not validate.</summary>
        public void SetToken(string token)
        {
            IntegrationCredentialStore.SetToken(ProviderKey, token);
            // A changed token invalidates the previously verified session state.
            _connected = false;
            _connectedName = null;
        }

        /// <summary>
        /// Creates an API client bound to the preferred stored credential (OAuth access token as a
        /// <c>Bearer</c>, otherwise the PAT in <c>X-Figma-Token</c>), or <c>null</c> when none is set.
        /// Does not refresh — use <see cref="ConnectAsync"/> for the refresh-aware path.
        /// </summary>
        public FigmaApiClient CreateClient()
        {
            var oauth = OAuthCredentialStore.GetTokens(ProviderKey);
            if (oauth != null && oauth.HasAccessToken)
                return new FigmaApiClient(oauth.accessToken, IntegrationCredentialKind.OAuth);

            var pat = IntegrationCredentialStore.GetToken(ProviderKey);
            return string.IsNullOrEmpty(pat) ? null : new FigmaApiClient(pat, IntegrationCredentialKind.PersonalAccessToken);
        }

        /// <inheritdoc/>
        protected override OAuthEndpointDescriptor BuildDescriptor() => new OAuthEndpointDescriptor
        {
            ClientId = oauthClientId,
            Scope = oauthScope,
            AuthorizeUrl = AuthorizeUrl,
            TokenUrl = TokenUrl
        };

        /// <inheritdoc/>
        /// <remarks>Opens the browser for Figma's authorization-code + PKCE flow over a loopback redirect.</remarks>
        public override async Awaitable<OAuthResult> BeginAuthorizationAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(oauthClientId))
                return OAuthResult.Fail("No Figma OAuth app client id configured. Set one in the provider " +
                                        "inspector, or use a personal access token.");

            var client = new OAuthAuthorizationCodeClient();
            var result = await client.AuthorizeAsync(BuildDescriptor(), cancellationToken);
            if (result.Success)
                StoreTokens(result.Tokens);
            return result;
        }

        /// <inheritdoc/>
        protected override void OnCredentialsChanged()
        {
            _connected = false;
            _connectedName = null;
        }

        /// <inheritdoc/>
        public override async Awaitable<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;
            _connectedName = null;

            // Refreshing only matters for OAuth; CreateClient picks the right credential + header.
            if (HasOAuthTokens)
                await GetFreshAccessTokenAsync(cancellationToken);

            var client = CreateClient();
            if (client == null)
            {
                Debug.LogWarning("[Figma] No credential set; cannot connect.");
                return false;
            }

            var user = await client.GetMeAsync(cancellationToken);
            if (user == null)
                return false;

            _connected = true;
            _connectedName = !string.IsNullOrEmpty(user.handle) ? user.handle : user.email;
            return true;
        }

        /// <inheritdoc/>
        /// <remarks>Clears both the PAT and any OAuth token bundle, and resets session state.</remarks>
        public override void Disconnect()
        {
            IntegrationCredentialStore.ClearToken(ProviderKey);
            OAuthCredentialStore.Clear(ProviderKey);
            _connected = false;
            _connectedName = null;
        }
    }
}
