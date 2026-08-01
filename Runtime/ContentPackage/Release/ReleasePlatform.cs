using UnityEngine;

namespace Molca.ContentPackage.Release
{
    /// <summary>
    /// Maps the running platform to a normalized contract identifier — see
    /// <c>contracts/content-release-v1.md</c> §1.
    /// </summary>
    /// <remarks>
    /// The closed set in the contract is not Unity's <see cref="RuntimePlatform"/> enum, and the two
    /// disagree in ways that matter. Unity distinguishes <c>WindowsPlayer</c> from
    /// <c>WindowsEditor</c>; a release does not, because the editor plays the content a Windows
    /// player would. Unity has no single "Android" — it has <c>Android</c>, and also runs Quest
    /// builds under it, which is exactly right for content that ships one Android release.
    ///
    /// An unmapped platform returns empty rather than a guess. The server rejects an unknown
    /// platform anyway (§1), and a guess would turn a clear <c>platform_unsupported</c> into a
    /// confusing download failure for a release built for something else.
    /// </remarks>
    public static class ReleasePlatform
    {
        /// <summary>The normalized identifier for the running platform, or empty when unmapped.</summary>
        public static string Current => Normalize(Application.platform);

        /// <summary>Maps a Unity runtime platform to a contract identifier.</summary>
        /// <param name="platform">The platform to map.</param>
        /// <returns>A contract identifier, or empty when the platform is not publishable.</returns>
        public static string Normalize(RuntimePlatform platform) => platform switch
        {
            // The editor resolves the content its target player would, so an author previewing a
            // release sees what ships rather than nothing at all.
            RuntimePlatform.WindowsPlayer or RuntimePlatform.WindowsEditor => "StandaloneWindows64",
            RuntimePlatform.OSXPlayer or RuntimePlatform.OSXEditor => "StandaloneOSX",
            RuntimePlatform.LinuxPlayer or RuntimePlatform.LinuxEditor => "StandaloneLinux64",
            RuntimePlatform.Android => "Android",
            RuntimePlatform.IPhonePlayer => "iOS",
            RuntimePlatform.WebGLPlayer => "WebGL",
            _ => "",
        };
    }
}
