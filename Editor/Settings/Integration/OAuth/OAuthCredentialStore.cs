using System;
using UnityEditor;
using UnityEngine;

namespace Molca.Settings.Integration.OAuth
{
    /// <summary>
    /// Persists per-provider OAuth token bundles (access + refresh + expiry + scope) for editor
    /// integrations.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/OAuth/</c>.
    /// Registration: static utility; not an asset.
    /// <para>
    /// This is the OAuth counterpart to <see cref="IntegrationCredentialStore"/> (which holds a single
    /// PAT/webhook string). OAuth needs to persist access token + refresh token + expiry + scope, which
    /// the single-token store cannot hold — and editing that Core store is a Core change Sprint 32 avoids
    /// (protected-zone rule). Both stores write to <see cref="EditorUserSettings"/>, so secrets stay
    /// per-machine and out of version control, never on a ScriptableObject (Sprints 4.5 / 14.5).
    /// </para>
    /// <para>
    /// The bundle is serialized with <see cref="JsonUtility"/> and stored under a namespaced key keyed on
    /// the provider's <see cref="IntegrationProvider.ProviderKey"/>, parallel to the PAT key prefix used by
    /// <see cref="IntegrationCredentialStore"/> so the two never collide.
    /// </para>
    /// </remarks>
    public static class OAuthCredentialStore
    {
        private const string KeyPrefix = "Molca.Integration.OAuth.";

        /// <summary>Default clock skew treated as "expiring soon" so a refresh fires before the token dies.</summary>
        public static readonly TimeSpan DefaultExpirySkew = TimeSpan.FromMinutes(2);

        private static string BundleKey(string providerKey) => $"{KeyPrefix}{providerKey}.Bundle";

        /// <summary>Reads the stored token bundle for a provider, or <c>null</c> if none is set.</summary>
        /// <param name="providerKey">The provider's stable key (see <see cref="IntegrationProvider.ProviderKey"/>).</param>
        public static OAuthTokenBundle GetTokens(string providerKey)
        {
            var json = EditorUserSettings.GetConfigValue(BundleKey(providerKey));
            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                var bundle = JsonUtility.FromJson<OAuthTokenBundle>(json);
                return bundle != null && bundle.HasAccessToken ? bundle : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[OAuth] Failed to read token bundle for '{providerKey}': {e.Message}");
                return null;
            }
        }

        /// <summary>Stores (or, when given null, clears) the token bundle for a provider.</summary>
        /// <param name="providerKey">The provider's stable key.</param>
        /// <param name="bundle">The bundle to persist; passing <c>null</c> clears it.</param>
        public static void SetTokens(string providerKey, OAuthTokenBundle bundle)
        {
            if (bundle == null || !bundle.HasAccessToken)
            {
                Clear(providerKey);
                return;
            }

            EditorUserSettings.SetConfigValue(BundleKey(providerKey), JsonUtility.ToJson(bundle));
        }

        /// <summary>Clears the stored token bundle for a provider.</summary>
        /// <param name="providerKey">The provider's stable key.</param>
        public static void Clear(string providerKey)
            => EditorUserSettings.SetConfigValue(BundleKey(providerKey), null);

        /// <summary>Returns <c>true</c> if a bundle with a non-empty access token is stored for the provider.</summary>
        /// <param name="providerKey">The provider's stable key.</param>
        public static bool HasTokens(string providerKey) => GetTokens(providerKey) != null;

        /// <summary>
        /// Returns <c>true</c> if a stored access token exists and is not past its expiry.
        /// </summary>
        /// <param name="providerKey">The provider's stable key.</param>
        /// <remarks>A non-expiring token (no <c>expiresAtUtc</c>) is always considered valid while present.</remarks>
        public static bool HasValidAccessToken(string providerKey)
        {
            var bundle = GetTokens(providerKey);
            if (bundle == null || !bundle.HasAccessToken)
                return false;
            if (!bundle.TryGetExpiry(out var expiresAt))
                return true; // No expiry → valid while present.
            return DateTime.UtcNow < expiresAt;
        }

        /// <summary>
        /// Returns <c>true</c> if the stored token expires within <paramref name="skew"/> (or is already
        /// expired), so a caller can refresh proactively. <c>false</c> when there is no token or no expiry.
        /// </summary>
        /// <param name="providerKey">The provider's stable key.</param>
        /// <param name="skew">How far ahead of expiry to start treating the token as stale; defaults to
        /// <see cref="DefaultExpirySkew"/>.</param>
        public static bool IsExpiringSoon(string providerKey, TimeSpan? skew = null)
        {
            var bundle = GetTokens(providerKey);
            if (bundle == null || !bundle.TryGetExpiry(out var expiresAt))
                return false; // No token or non-expiring → nothing to refresh.
            return DateTime.UtcNow + (skew ?? DefaultExpirySkew) >= expiresAt;
        }
    }
}
