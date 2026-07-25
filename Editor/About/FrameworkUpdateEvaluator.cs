using Molca.Editor.Addons;
using UnityEditor.PackageManager;

namespace Molca.Editor.About
{
    /// <summary>
    /// Turns a raw update-feed response into the single verdict the About section renders: what to offer,
    /// what is blocked by this editor's Unity, whether the installed Core is still supported, and how an
    /// upgrade could actually be applied here.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/About/</c>. Pure and static — no Package Manager calls,
    /// no UI, no network — so every decision below is exercised by
    /// <c>Tests/Editor/About/FrameworkUpdateTests.cs</c>. Version ordering reuses
    /// <see cref="AddonSemVer"/> rather than adding a second SemVer implementation to the package.
    /// </remarks>
    internal static class FrameworkUpdateEvaluator
    {
        /// <summary>
        /// Evaluates a feed response against this project.
        /// </summary>
        /// <param name="installedVersion">Installed Core version; empty when it could not be resolved.</param>
        /// <param name="unityVersion">Running editor version, e.g. <c>6000.0.25f1</c>.</param>
        /// <param name="source">How Core is installed, which decides the offered upgrade path.</param>
        /// <param name="response">The feed response, or <c>null</c> when no check has succeeded.</param>
        /// <returns>The resolved state; never <c>null</c>.</returns>
        internal static FrameworkUpdateState Evaluate(
            string installedVersion, string unityVersion, PackageSource source, FrameworkUpdateResponse response)
        {
            var state = new FrameworkUpdateState
            {
                InstalledVersion = installedVersion ?? string.Empty,
                Source = source,
                Channel = response?.channel ?? string.Empty,
                MaxChannel = response?.maxChannel ?? string.Empty,
                Eol = response?.installed?.eol ?? false,
                MinSupportedVersion = response?.installed?.minSupportedVersion ?? string.Empty,
            };

            var latest = Present(response?.latest);
            if (latest == null || string.IsNullOrWhiteSpace(installedVersion))
            {
                state.Verdict = FrameworkUpdateVerdict.Unknown;
                return state;
            }

            int ordering = AddonSemVer.Compare(latest.version, installedVersion);
            if (ordering == 0)
            {
                state.Verdict = FrameworkUpdateVerdict.UpToDate;
                return state;
            }

            if (ordering < 0)
            {
                // A local build, or a prerelease installed from a channel this license can no longer see.
                // Never present that as "up to date": the two facts have different causes.
                state.Verdict = FrameworkUpdateVerdict.InstalledIsNewer;
                return state;
            }

            if (SupportsUnity(unityVersion, latest.minUnity))
            {
                state.Verdict = FrameworkUpdateVerdict.UpdateAvailable;
                state.Target = latest;
                state.UpgradePath = ResolvePath(source, latest);
                return state;
            }

            // The newest release needs a newer editor. If the feed also named an older release this editor
            // can take, offer that and keep the blocked one visible — a dead button explains nothing.
            var fallback = Present(response.latestCompatible);
            bool fallbackIsNewer = fallback != null &&
                                   AddonSemVer.Compare(fallback.version, installedVersion) > 0 &&
                                   SupportsUnity(unityVersion, fallback.minUnity);
            if (fallbackIsNewer)
            {
                state.Verdict = FrameworkUpdateVerdict.UpdateAvailable;
                state.Target = fallback;
                state.Blocked = latest;
                state.UpgradePath = ResolvePath(source, fallback);
                return state;
            }

            state.Verdict = FrameworkUpdateVerdict.BlockedByUnity;
            state.Blocked = latest;
            return state;
        }

        /// <summary>
        /// True when this editor satisfies a release's minimum Unity version.
        /// </summary>
        /// <param name="editorVersion">Running editor version.</param>
        /// <param name="minUnity">Minimum required version, e.g. <c>6000.1</c>; empty means unconstrained.</param>
        /// <remarks>
        /// Mirrors the server's rule deliberately, including its bias: an unreadable version on either side
        /// admits the upgrade. Withholding an update because a version string was not recognized would be a
        /// worse failure than offering one the editor then refuses to resolve.
        /// </remarks>
        internal static bool SupportsUnity(string editorVersion, string minUnity)
        {
            if (!TryParseUnity(minUnity, out var required)) return true;
            if (!TryParseUnity(editorVersion, out var editor)) return true;
            if (editor.Major != required.Major) return editor.Major > required.Major;
            if (editor.Minor != required.Minor) return editor.Minor > required.Minor;
            return editor.Patch >= required.Patch;
        }

        /// <summary>
        /// Parses the ordered part of a Unity version (<c>6000.0.25f1</c> → 6000.0.25). The release-type
        /// suffix (<c>f1</c>, <c>b3</c>) carries no ordering this comparison needs and is dropped.
        /// </summary>
        internal static bool TryParseUnity(string text, out (int Major, int Minor, int Patch) version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Trim().Split('.');
            if (!int.TryParse(parts[0], out int major)) return false;
            int minor = parts.Length > 1 ? LeadingInt(parts[1]) : 0;
            int patch = parts.Length > 2 ? LeadingInt(parts[2]) : 0;
            version = (major, minor, patch);
            return true;
        }

        /// <summary>
        /// How an upgrade could reach this project. A release with no <c>upgradeSpec</c> offers no path at
        /// all, whatever the install source — the client refuses to guess a dependency string.
        /// </summary>
        internal static FrameworkUpgradePath ResolvePath(PackageSource source, FrameworkReleaseDto release)
        {
            if (release == null || string.IsNullOrWhiteSpace(release.upgradeSpec))
                return FrameworkUpgradePath.None;

            return source switch
            {
                PackageSource.Registry => FrameworkUpgradePath.PackageManager,
                PackageSource.Git => FrameworkUpgradePath.Manifest,
                PackageSource.Embedded or PackageSource.Local or PackageSource.LocalTarball =>
                    FrameworkUpgradePath.Embedded,
                _ => FrameworkUpgradePath.None,
            };
        }

        private static FrameworkReleaseDto Present(FrameworkReleaseDto release) =>
            release != null && release.IsPresent ? release : null;

        private static int LeadingInt(string text)
        {
            int end = 0;
            while (end < text.Length && char.IsDigit(text[end])) end++;
            return end > 0 && int.TryParse(text.Substring(0, end), out int value) ? value : 0;
        }
    }
}
