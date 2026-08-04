using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// Maps the active build target to the runtime platform a release is published for.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <para>
    /// Shared by Compatibility, Verify and Publish rather than copied into each. All three have to agree
    /// on what platform is being built, and a release staged for one platform and published as another
    /// downloads bundles no device can load.
    /// </para>
    /// <para>
    /// An unmapped target falls through to <see cref="RuntimePlatform.LinuxEditor"/>, which
    /// <c>ReleasePlatform.Normalize</c> then rejects. That is deliberate: the pages ask for the
    /// normalized name and refuse to build or publish when it comes back empty, so an unsupported target
    /// stops at a named refusal rather than at a plausible-looking wrong platform.
    /// </para>
    /// </remarks>
    internal static class ContentPlatform
    {
        /// <summary>The runtime platform a build target publishes for.</summary>
        /// <param name="target">The active build target.</param>
        /// <returns>The runtime platform, or an editor platform for targets that cannot ship content.</returns>
        public static RuntimePlatform Of(BuildTarget target) => target switch
        {
            BuildTarget.StandaloneWindows64 or BuildTarget.StandaloneWindows => RuntimePlatform.WindowsPlayer,
            BuildTarget.StandaloneOSX => RuntimePlatform.OSXPlayer,
            BuildTarget.StandaloneLinux64 => RuntimePlatform.LinuxPlayer,
            BuildTarget.Android => RuntimePlatform.Android,
            BuildTarget.iOS => RuntimePlatform.IPhonePlayer,
            BuildTarget.WebGL => RuntimePlatform.WebGLPlayer,
            _ => RuntimePlatform.LinuxEditor,
        };
    }
}
