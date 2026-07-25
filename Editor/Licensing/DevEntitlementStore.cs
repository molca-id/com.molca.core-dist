using UnityEditor;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Persists the signed developer entitlement token per-machine in
    /// <see cref="EditorUserSettings"/> — the same store the OAuth credential bundles use, so the
    /// token stays out of version control and off any ScriptableObject (the Sprint 4.5 / 14.5
    /// secret rule). A CI build instead supplies the token via
    /// <see cref="DevLicenseConfig.EntitlementEnvVar"/> (see <see cref="LoadEffective"/>).
    /// </summary>
    internal static class DevEntitlementStore
    {
        private const string ConfigKey = "Molca.License.DevEntitlement";

        /// <summary>Reads the stored entitlement token, or empty if none is set.</summary>
        public static string Load() => EditorUserSettings.GetConfigValue(ConfigKey) ?? string.Empty;

        /// <summary>Persists (or, when null/empty, clears) the entitlement token.</summary>
        public static void Save(string token) =>
            EditorUserSettings.SetConfigValue(ConfigKey, string.IsNullOrEmpty(token) ? null : token);

        /// <summary>Clears the stored entitlement token.</summary>
        public static void Clear() => EditorUserSettings.SetConfigValue(ConfigKey, null);

        /// <summary>
        /// Resolves the entitlement to use: the CI/headless environment override if present,
        /// otherwise the per-machine stored token. The env var lets an unattended build carry a
        /// pre-activated entitlement without an interactive sign-in.
        /// </summary>
        public static string LoadEffective()
        {
            string fromEnv = System.Environment.GetEnvironmentVariable(DevLicenseConfig.EntitlementEnvVar);
            return !string.IsNullOrEmpty(fromEnv) ? fromEnv : Load();
        }
    }
}
