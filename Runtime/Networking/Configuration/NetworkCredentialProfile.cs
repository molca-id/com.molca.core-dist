using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Networking.Configuration
{
    /// <summary>Where a credential's secret material comes from at execution time.</summary>
    /// <remarks>
    /// The catalog stores this <em>kind</em>, never a value. Secrets are supplied by platform
    /// implementations of <c>INetworkCredentialProvider</c>.
    /// </remarks>
    public enum NetworkCredentialProviderKind
    {
        /// <summary>No credential. Requests on this scope are anonymous.</summary>
        None = 0,

        /// <summary>The runtime <c>AuthManager</c> session token.</summary>
        AuthManagerToken,

        /// <summary>Editor-only secure storage, for authoring and the request console.</summary>
        EditorSecureStorage,

        /// <summary>A process environment variable, typically injected by CI.</summary>
        EnvironmentVariable,

        /// <summary>An OS or platform key store.</summary>
        PlatformKeyStore,

        /// <summary>A project-supplied provider registered with the network subsystem.</summary>
        Custom
    }

    /// <summary>When an acquired credential is refreshed.</summary>
    public enum NetworkCredentialRefreshMode
    {
        /// <summary>Never refreshed; acquired once per session.</summary>
        None = 0,

        /// <summary>Refreshed when the provider reports the credential as expired.</summary>
        OnExpiry,

        /// <summary>Refreshed after a 401, once, with a single-flight guard.</summary>
        OnUnauthorized,

        /// <summary>Refreshed on expiry and after a 401.</summary>
        OnExpiryAndUnauthorized
    }

    /// <summary>
    /// Non-secret description of a credential: which provider supplies it, how it is attached, and
    /// which services and hosts may ever see it. Serialized inside <see cref="NetworkCatalog"/>.
    /// </summary>
    /// <remarks>
    /// This type has no secret-valued field and must never gain one. Its purpose is the opposite:
    /// to bound where a secret obtained elsewhere is permitted to travel (plan §6.6).
    /// <para>
    /// <see cref="AllowedServiceIds"/> and <see cref="AllowedHostPatterns"/> deny when empty. A
    /// profile with no scope authored attaches to nothing.
    /// </para>
    /// </remarks>
    [Serializable]
    public class NetworkCredentialProfile
    {
        [SerializeField] private string _id = "";
        [SerializeField] private string _displayName = "";
        [SerializeField] private NetworkCredentialProviderKind _providerKind = NetworkCredentialProviderKind.None;

        [Tooltip("Non-secret provider lookup key: an environment variable name, key-store entry name, or custom provider ID. Never a value.")]
        [SerializeField] private string _providerKey = "";

        [Tooltip("Audience or resource this credential is issued for. Non-secret metadata.")]
        [SerializeField] private string _audience = "";

        [Tooltip("Scopes requested from the issuer. Non-secret metadata.")]
        [SerializeField] private List<string> _scopes = new List<string>();

        [Header("Attachment")]
        [Tooltip("Header the credential is sent in.")]
        [SerializeField] private string _headerName = "Authorization";

        [Tooltip("Scheme prefix placed before the credential, e.g. 'Bearer '.")]
        [SerializeField] private string _scheme = "Bearer ";

        [SerializeField] private NetworkCredentialRefreshMode _refreshMode = NetworkCredentialRefreshMode.OnExpiry;

        [Header("Scope — empty denies")]
        [Tooltip("Service IDs permitted to use this credential. Empty means none.")]
        [SerializeField] private List<string> _allowedServiceIds = new List<string>();

        [Tooltip("Hosts permitted to receive this credential, e.g. api.example.com or *.example.com. Empty means none.")]
        [SerializeField] private List<string> _allowedHostPatterns = new List<string>();

        [Tooltip("Allow the Hub request console to use this credential. Production sends remain separately gated.")]
        [SerializeField] private bool _usableFromRequestConsole = false;

        /// <summary>Stable kebab-case identifier.</summary>
        public string Id => _id;

        /// <summary>Human-readable name.</summary>
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? _id : _displayName;

        /// <summary>Which provider supplies the secret at execution time.</summary>
        public NetworkCredentialProviderKind ProviderKind => _providerKind;

        /// <summary>Non-secret lookup key handed to the provider. Never a credential value.</summary>
        public string ProviderKey => _providerKey;

        /// <summary>Audience or resource the credential is issued for.</summary>
        public string Audience => _audience;

        /// <summary>Requested scopes. Non-secret.</summary>
        public IReadOnlyList<string> Scopes => _scopes;

        /// <summary>Header the credential is attached to.</summary>
        public string HeaderName => string.IsNullOrEmpty(_headerName) ? "Authorization" : _headerName;

        /// <summary>Scheme prefix placed before the credential value.</summary>
        public string Scheme => _scheme;

        /// <summary>When the credential is refreshed.</summary>
        public NetworkCredentialRefreshMode RefreshMode => _refreshMode;

        /// <summary>Service IDs permitted to use this credential. Empty denies.</summary>
        public IReadOnlyList<string> AllowedServiceIds => _allowedServiceIds;

        /// <summary>Host patterns permitted to receive this credential. Empty denies.</summary>
        public IReadOnlyList<string> AllowedHostPatterns => _allowedHostPatterns;

        /// <summary>Whether the Hub request console may use this credential at all.</summary>
        public bool UsableFromRequestConsole => _usableFromRequestConsole;

        /// <summary>Whether this profile attaches anything. A <see cref="NetworkCredentialProviderKind.None"/> profile does not.</summary>
        public bool IsAnonymous => _providerKind == NetworkCredentialProviderKind.None;

        /// <summary>
        /// Whether <paramref name="serviceId"/> is inside this credential's service scope.
        /// </summary>
        /// <param name="serviceId">The service attempting to use the credential.</param>
        /// <returns><c>false</c> when the scope list is empty — an unauthored scope denies.</returns>
        public bool AllowsService(string serviceId)
        {
            if (_allowedServiceIds == null || string.IsNullOrEmpty(serviceId))
                return false;

            for (int i = 0; i < _allowedServiceIds.Count; i++)
            {
                if (string.Equals(_allowedServiceIds[i], serviceId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Whether <paramref name="host"/> may receive this credential.
        /// </summary>
        /// <param name="host">The final resolved host, after any redirect.</param>
        /// <returns><c>false</c> when the pattern list is empty — an unauthored scope denies.</returns>
        /// <remarks>
        /// Call this against the <em>final</em> host, not the authored origin. A redirect changes the
        /// answer, and re-checking is what stops a credential following a 302 off-domain.
        /// </remarks>
        public bool AllowsHost(string host) => NetworkHostRule.MatchesAny(_allowedHostPatterns, host);

        /// <summary>
        /// Creates a profile in code. Used by migration, import, and tests.
        /// </summary>
        /// <param name="id">Stable identifier; must satisfy <see cref="NetworkIds.IsValid"/>.</param>
        /// <param name="displayName">Human-readable name, or <c>null</c> to reuse <paramref name="id"/>.</param>
        /// <param name="providerKind">Which provider supplies the secret.</param>
        internal static NetworkCredentialProfile Create(
            string id,
            string displayName,
            NetworkCredentialProviderKind providerKind)
        {
            return new NetworkCredentialProfile
            {
                _id = id,
                _displayName = displayName ?? id,
                _providerKind = providerKind
            };
        }
    }
}
