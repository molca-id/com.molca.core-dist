using UnityEditor;

namespace Molca.Settings.Integration
{
    /// <summary>
    /// Single gateway for reading and writing integration secrets (API tokens).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/</c>.
    /// Registration: static utility; not an asset.
    /// <para>
    /// Secrets are stored via <see cref="EditorUserSettings"/>, which persists to
    /// <c>ProjectSettings/EditorUserSettings.asset</c> — per-machine, excluded from version control by
    /// the default Unity <c>.gitignore</c>, and <b>not</b> placed in the machine-global registry the way
    /// <see cref="EditorPrefs"/> would. This keeps tokens off the card UI and out of committed assets.
    /// Providers must route all credential access through this class rather than touching
    /// <see cref="EditorUserSettings"/> directly, so the storage location stays in one place.
    /// </para>
    /// </remarks>
    public static class IntegrationCredentialStore
    {
        private const string KeyPrefix = "Molca.Integration.";

        private static string TokenKey(string providerKey) => $"{KeyPrefix}{providerKey}.Token";

        /// <summary>Reads the stored token for a provider, or <c>null</c>/empty if none is set.</summary>
        /// <param name="providerKey">The provider's stable key (see <see cref="IntegrationProvider.ProviderKey"/>).</param>
        public static string GetToken(string providerKey)
            => EditorUserSettings.GetConfigValue(TokenKey(providerKey));

        /// <summary>Stores (or, when given null/empty, clears) the token for a provider.</summary>
        /// <param name="providerKey">The provider's stable key.</param>
        /// <param name="token">The secret token; passing null or empty clears it.</param>
        public static void SetToken(string providerKey, string token)
            => EditorUserSettings.SetConfigValue(TokenKey(providerKey), token);

        /// <summary>Clears the stored token for a provider.</summary>
        /// <param name="providerKey">The provider's stable key.</param>
        public static void ClearToken(string providerKey)
            => EditorUserSettings.SetConfigValue(TokenKey(providerKey), null);

        /// <summary>Returns <c>true</c> if a non-empty token is stored for the provider.</summary>
        /// <param name="providerKey">The provider's stable key.</param>
        public static bool HasToken(string providerKey)
            => !string.IsNullOrEmpty(GetToken(providerKey));
    }
}
