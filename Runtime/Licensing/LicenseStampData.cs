namespace Molca.Licensing
{
    /// <summary>
    /// The small license record baked into a build's <c>Resources</c> at build time and read back at
    /// runtime by <see cref="LicenseHeartbeat"/>. Deliberately minimal: it carries the licensee
    /// identity and Core version for a soft usage/audit signal, and no developer machine id or other
    /// per-developer detail that a shipped player should not contain.
    /// </summary>
    [System.Serializable]
    public class LicenseStampData
    {
        /// <summary>Licensee identity (Workspace domain, or the individual email for a per-seat license).</summary>
        public string licenseeId;

        /// <summary>Core package version the build was produced against.</summary>
        public string coreVersion;

        /// <summary>UTC time the stamp was written, ISO-8601.</summary>
        public string stampedAtUtc;

        /// <summary>
        /// Signed, revocable claim minted by the control plane at build time, letting this player report
        /// usage without carrying a developer credential. Empty when the build machine was offline or
        /// unlicensed, in which case runtime reporting is simply disabled for this build.
        /// </summary>
        /// <remarks>
        /// This is not a secret in the credential sense — it authorizes appending usage events for one
        /// licensee and nothing else, and any build can be revoked individually from the dashboard.
        /// </remarks>
        public string buildToken;

        /// <summary>Server-side id of the build this stamp belongs to; empty when unstamped.</summary>
        public string buildId;

        /// <summary>Player application version recorded at build time.</summary>
        public string appVersion;

        /// <summary>
        /// Control-plane base URL to report to. Carried in the stamp because the server address lives in
        /// editor-only configuration that runtime assemblies cannot reference.
        /// </summary>
        public string serverBaseUrl;
    }
}
