using System;
using System.Text;
using System.Threading;
using Molca.Settings.Integration.OAuth;
using UnityEngine;

namespace Molca.Settings.Integration.GitHub
{
    /// <summary>
    /// GitHub integration: connects with a personal access token and pushes build/release activity.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/GitHub/</c>.
    /// Base class: <see cref="IntegrationProvider"/>.
    /// Registration: add the asset to <see cref="IntegrationSettings"/>' provider list. The PAT is stored in
    /// <see cref="IntegrationCredentialStore"/> (per-machine, never committed); only non-secret config
    /// (owner, repo, push toggles) is serialized on the asset.
    /// <para>
    /// Build push opens an issue on a <b>failed</b> build only (successful builds are not noise-posted);
    /// release push publishes a GitHub release from the composed changelog entry. <see cref="IsConnected"/>
    /// is session-scoped (set by <see cref="ConnectAsync"/>, reset on domain reload — no render-path network).
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "GitHub Integration", menuName = "Molca/Editor/Integrations/GitHub", order = 110)]
    public class GitHubIntegrationProvider : OAuthIntegrationProvider
    {
        // GitHub's device-flow endpoints. Device flow needs only a public client_id — no secret, no
        // hosted callback — which is what makes it shippable in a distributable editor tool (Sprint 32).
        private const string DeviceCodeUrl = "https://github.com/login/device/code";
        private const string TokenUrl = "https://github.com/login/oauth/access_token";

        [Header("OAuth (device flow)")]
        [Tooltip("Public client id of a GitHub OAuth App used for device-flow sign-in. Not a secret. " +
                 "Leave empty to use only a personal access token.")]
        [SerializeField] private string oauthClientId;

        [Tooltip("Space-delimited OAuth scopes to request (e.g. \"repo read:org\").")]
        [SerializeField] private string oauthScope = "repo read:org";

        [Header("Target Repository")]
        [Tooltip("Repository owner (user or organization), e.g. \"molca\".")]
        [SerializeField] private string owner;

        [Tooltip("Repository name, e.g. \"framework-unity\".")]
        [SerializeField] private string repo;

        [Header("Automation")]
        [Tooltip("Open an issue when a build fails.")]
        [SerializeField] private bool pushOnBuild = true;

        [Tooltip("Publish a GitHub release when the project version is bumped.")]
        [SerializeField] private bool pushOnRelease = false;

        // Session-scoped cache; not serialized (resets on domain reload, as ConnectAsync repopulates it).
        [NonSerialized] private bool _connected;
        [NonSerialized] private string _connectedName;

        /// <inheritdoc/>
        public override string DisplayName => "GitHub";

        /// <inheritdoc/>
        public override string Description => "Build issues & release publishing";

        /// <inheritdoc/>
        public override string Glyph => "G";

        /// <inheritdoc/>
        public override string GlyphColor => "rgb(110, 84, 148)";

        /// <summary>Repository owner (user or organization).</summary>
        public string Owner => owner;

        /// <summary>Repository name.</summary>
        public string Repo => repo;

        /// <summary>Whether a failed build should open an issue.</summary>
        public bool PushOnBuild => pushOnBuild;

        /// <summary>Whether a version bump should publish a release.</summary>
        public bool PushOnRelease => pushOnRelease;

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

        /// <summary>Whether a GitHub OAuth App client id is configured (device-flow sign-in is available).</summary>
        public bool SupportsOAuth => !string.IsNullOrEmpty(oauthClientId);

        private bool HasRepo => !string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo);

        /// <inheritdoc/>
        public override bool ShouldPushOnBuild => enabled && pushOnBuild && HasToken && HasRepo;

        /// <inheritdoc/>
        public override bool ShouldPushOnRelease => enabled && pushOnRelease && HasToken && HasRepo;

        /// <summary>Stores the personal access token. Pass null/empty to clear it; does not validate.</summary>
        public void SetToken(string token)
        {
            IntegrationCredentialStore.SetToken(ProviderKey, token);
            // A changed token invalidates the previously verified session state.
            _connected = false;
            _connectedName = null;
        }

        /// <summary>
        /// Creates an API client bound to the preferred stored credential (OAuth access token when present,
        /// otherwise the PAT), or <c>null</c> when none is set. Does not refresh — use
        /// <see cref="ConnectAsync"/> for the refresh-aware path.
        /// </summary>
        public GitHubApiClient CreateClient()
        {
            var token = ResolveToken();
            return string.IsNullOrEmpty(token) ? null : new GitHubApiClient(token);
        }

        // The token to send: a stored OAuth access token wins over a PAT (no refresh on this sync path).
        private string ResolveToken()
        {
            var oauth = OAuthCredentialStore.GetTokens(ProviderKey);
            if (oauth != null && oauth.HasAccessToken)
                return oauth.accessToken;
            return IntegrationCredentialStore.GetToken(ProviderKey);
        }

        /// <inheritdoc/>
        protected override OAuthEndpointDescriptor BuildDescriptor() => new OAuthEndpointDescriptor
        {
            ClientId = oauthClientId,
            Scope = oauthScope,
            DeviceCodeUrl = DeviceCodeUrl,
            TokenUrl = TokenUrl
        };

        /// <inheritdoc/>
        public override async Awaitable<OAuthResult> BeginAuthorizationAsync(CancellationToken cancellationToken = default)
            => await ConnectWithDeviceFlowAsync(null, cancellationToken);

        /// <summary>
        /// Runs the GitHub device flow, surfacing the user code/verification URL to the UI before polling.
        /// </summary>
        /// <param name="onCodeReady">
        /// Invoked once the device + user codes are issued, so the UI can show the code and the
        /// verification URL. May be <c>null</c> for a headless attempt.
        /// </param>
        /// <param name="cancellationToken">Cancels the flow; cancellation is not an error.</param>
        /// <returns>The flow result; on success the tokens are already stored.</returns>
        public async Awaitable<OAuthResult> ConnectWithDeviceFlowAsync(
            Action<DeviceCodeInfo> onCodeReady, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(oauthClientId))
                return OAuthResult.Fail("No GitHub OAuth App client id configured. Set one in the provider " +
                                        "inspector, or use a personal access token.");

            var client = new OAuthDeviceFlowClient();
            var descriptor = BuildDescriptor();

            var codeResult = await client.RequestDeviceCodeAsync(descriptor, cancellationToken);
            if (!codeResult.Success)
                return OAuthResult.Fail(codeResult.Error);

            onCodeReady?.Invoke(codeResult.Info);

            var result = await client.PollForTokenAsync(descriptor, codeResult.Info, cancellationToken);
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

            // Prefer a (refreshed) OAuth token; fall back to a PAT.
            string token = HasOAuthTokens ? await GetFreshAccessTokenAsync(cancellationToken) : null;
            if (string.IsNullOrEmpty(token))
                token = IntegrationCredentialStore.GetToken(ProviderKey);
            if (string.IsNullOrEmpty(token))
            {
                Debug.LogWarning("[GitHub] No credential set; cannot connect.");
                return false;
            }

            var client = new GitHubApiClient(token);

            var user = await client.GetAuthenticatedUserAsync(cancellationToken);
            if (user == null)
                return false;

            // If a repo is configured, confirm it is reachable so the status reflects a usable connection.
            if (HasRepo)
            {
                var repository = await client.GetRepositoryAsync(owner, repo, cancellationToken);
                if (repository == null)
                {
                    Debug.LogWarning($"[GitHub] Token valid but '{owner}/{repo}' is not accessible.");
                    return false;
                }
            }

            _connected = true;
            _connectedName = !string.IsNullOrEmpty(user.name) ? user.name : user.login;
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

        /// <inheritdoc/>
        public override async Awaitable PushBuildActivityAsync(
            BuildActivity activity, CancellationToken cancellationToken = default)
        {
            // Only failed builds warrant an issue; successful builds are not posted as noise.
            if (activity.Succeeded)
                return;

            var client = CreateClient();
            if (client == null) return;

            string title = $"Build {activity.Result}: {activity.ProjectName} {activity.Version}";

            var body = new StringBuilder();
            body.AppendLine($"**Project:** {activity.ProjectName}");
            body.AppendLine($"**Version:** {activity.Version}");
            body.AppendLine($"**Platform:** {activity.Platform}");
            body.AppendLine($"**Result:** {activity.Result}");
            body.AppendLine($"**Duration:** {activity.Duration.Minutes}m {activity.Duration.Seconds}s");
            body.AppendLine($"**Errors:** {activity.Errors}");
            body.AppendLine($"**Triggered by:** {activity.TriggeredBy}");

            var result = await client.CreateIssueAsync(owner, repo, title, body.ToString(), cancellationToken);
            if (result.Success)
                Debug.Log($"[GitHub] Opened build-failure issue: {result.Url}");
            else
                Debug.LogWarning($"[GitHub] Build issue failed ({result.StatusCode}): {result.Error}");
        }

        /// <inheritdoc/>
        public override async Awaitable PushReleaseActivityAsync(
            ReleaseActivity activity, CancellationToken cancellationToken = default)
        {
            var client = CreateClient();
            if (client == null) return;

            string tag = $"v{activity.Version}";
            string name = $"{activity.ProjectName} {activity.Version}";
            string body = string.IsNullOrWhiteSpace(activity.Notes) ? name : activity.Notes.Trim();

            var result = await client.CreateReleaseAsync(owner, repo, tag, name, body, cancellationToken);
            if (result.Success)
                Debug.Log($"[GitHub] Published release: {result.Url}");
            else
                Debug.LogWarning($"[GitHub] Release publish failed ({result.StatusCode}): {result.Error}");
        }
    }
}

