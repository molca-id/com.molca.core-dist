using System;
using UnityEditor.PackageManager;

namespace Molca.Editor.About
{
    /// <summary>
    /// One Core release as described by the control plane's update feed
    /// (<c>GET /framework/releases/latest</c>).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/About/</c>.
    /// Field names and casing must match the server payload exactly — this is deserialized by
    /// <see cref="UnityEngine.JsonUtility"/>, which binds by field name and silently leaves unmatched
    /// fields at their default.
    /// </remarks>
    [Serializable]
    internal sealed class FrameworkReleaseDto
    {
        /// <summary>Release version, e.g. <c>1.15.0</c>.</summary>
        public string version;

        /// <summary>Channel this release was published on (<c>stable</c>/<c>beta</c>/<c>internal</c>).</summary>
        public string channel;

        /// <summary>Publish timestamp, ISO-8601 UTC.</summary>
        public string publishedAt;

        /// <summary>Minimum Unity version this release supports (e.g. <c>6000.0</c>); empty when unconstrained.</summary>
        public string minUnity;

        /// <summary>
        /// The server's own reading of whether this editor satisfies <see cref="minUnity"/>. Informational:
        /// <see cref="FrameworkUpdateEvaluator"/> recomputes it locally so the verdict is identical whether
        /// or not the request carried an editor version.
        /// </summary>
        public bool unitySatisfied;

        /// <summary>
        /// The exact upgrade instruction — a registry spec (<c>com.molca.core@1.15.0</c>) or a git ref
        /// (<c>git+https://…#v1.15.0</c>). The client never invents this.
        /// </summary>
        public string upgradeSpec;

        /// <summary>HTTPS changelog URL, or empty.</summary>
        public string changelogUrl;

        /// <summary>Free-text migration guidance shown before the upgrade is applied.</summary>
        public string upgradeNotes;

        /// <summary>Short "what's new" lines.</summary>
        public string[] highlights;

        /// <summary>True when this instance carries a real release rather than an absent one.</summary>
        internal bool IsPresent => !string.IsNullOrWhiteSpace(version);
    }

    /// <summary>Support posture of the Core version this project has installed.</summary>
    [Serializable]
    internal sealed class FrameworkInstalledDto
    {
        /// <summary>The version the client reported.</summary>
        public string version;

        /// <summary>True when the feed knows this exact version (false for private or pre-feed builds).</summary>
        public bool known;

        /// <summary>False only when the installed version is older than the declared support floor.</summary>
        public bool supported;

        /// <summary>True when this version is past end of support.</summary>
        public bool eol;

        /// <summary>Oldest Core version still supported, or empty when no policy is declared.</summary>
        public string minSupportedVersion;
    }

    /// <summary>Envelope of the framework update feed.</summary>
    [Serializable]
    internal sealed class FrameworkUpdateResponse
    {
        /// <summary>Wire schema; a mismatch with <see cref="FrameworkUpdateClient.SchemaVersion"/> is fatal.</summary>
        public int schemaVersion;

        /// <summary>Channel actually served (the licensee's ceiling caps what the client requested).</summary>
        public string channel;

        /// <summary>Highest channel this license may request.</summary>
        public string maxChannel;

        /// <summary>Support posture of the installed version.</summary>
        public FrameworkInstalledDto installed;

        /// <summary>Newest release visible to this license, whatever Unity it requires.</summary>
        public FrameworkReleaseDto latest;

        /// <summary>
        /// Newest release this editor's Unity can actually take, present only when it differs from
        /// <see cref="latest"/> — i.e. only when the newest release is blocked by its Unity requirement.
        /// </summary>
        public FrameworkReleaseDto latestCompatible;
    }

    /// <summary>What the update check concluded about this project.</summary>
    internal enum FrameworkUpdateVerdict
    {
        /// <summary>No feed answer yet, or the feed has no releases to compare against.</summary>
        Unknown,

        /// <summary>The installed version is the newest one available on this channel.</summary>
        UpToDate,

        /// <summary>A newer version exists and this editor can install it.</summary>
        UpdateAvailable,

        /// <summary>A newer version exists but every candidate requires a newer Unity than this editor.</summary>
        BlockedByUnity,

        /// <summary>The installed version is newer than anything published — a local or pre-release build.</summary>
        InstalledIsNewer,
    }

    /// <summary>How this project could actually take an upgrade, given how Core is installed.</summary>
    /// <remarks>
    /// The install source decides the affordance. Offering a one-click Package Manager update to a project
    /// that embeds Core under <c>Packages/</c> would be a lie — the manager does not own that directory.
    /// </remarks>
    internal enum FrameworkUpgradePath
    {
        /// <summary>Nothing actionable (no target, or no upgrade spec published).</summary>
        None,

        /// <summary>Registry install: the Package Manager can add the target version directly.</summary>
        PackageManager,

        /// <summary>Git install with a spec the client cannot apply: the manifest line must be repointed by hand.</summary>
        Manifest,

        /// <summary>Embedded, local, or tarball install: the files are project-owned; update them in place.</summary>
        Embedded,

        /// <summary>
        /// Git install with a revision-pinned git spec: the Package Manager can repoint the git dependency
        /// directly, the same move its own <c>Manage ▸ Update</c> makes.
        /// </summary>
        /// <remarks>
        /// Appended rather than slotted next to <see cref="PackageManager"/> so no existing member's ordinal
        /// moves. Behaves identically to <see cref="PackageManager"/> at the call site — the two are kept
        /// apart only so the panel can explain which dependency is about to change.
        /// </remarks>
        GitPackageManager,
    }

    /// <summary>
    /// The resolved, presentation-ready outcome of one update check: what to offer, what is blocked, and
    /// whether the installed version is still supported.
    /// </summary>
    /// <remarks>
    /// Produced by <see cref="FrameworkUpdateEvaluator.Evaluate"/> and rendered by
    /// <c>MolcaHubAboutSection</c>. Deliberately free of UI strings so the decisions stay testable.
    /// </remarks>
    internal sealed class FrameworkUpdateState
    {
        /// <summary>What the check concluded.</summary>
        internal FrameworkUpdateVerdict Verdict { get; set; } = FrameworkUpdateVerdict.Unknown;

        /// <summary>The Core version installed in this project.</summary>
        internal string InstalledVersion { get; set; } = string.Empty;

        /// <summary>The release being offered, or <c>null</c> when there is nothing to offer.</summary>
        internal FrameworkReleaseDto Target { get; set; }

        /// <summary>
        /// A newer release that exists but cannot be installed on this editor, or <c>null</c>. Set alongside
        /// <see cref="Target"/> when an older-but-installable version is being offered instead.
        /// </summary>
        internal FrameworkReleaseDto Blocked { get; set; }

        /// <summary>True when the installed version is past end of support.</summary>
        internal bool Eol { get; set; }

        /// <summary>Oldest still-supported Core version, or empty when no policy is declared.</summary>
        internal string MinSupportedVersion { get; set; } = string.Empty;

        /// <summary>Channel the feed served.</summary>
        internal string Channel { get; set; } = string.Empty;

        /// <summary>Highest channel this license may request.</summary>
        internal string MaxChannel { get; set; } = string.Empty;

        /// <summary>How an upgrade could be applied to this project.</summary>
        internal FrameworkUpgradePath UpgradePath { get; set; } = FrameworkUpgradePath.None;

        /// <summary>How Core is installed, which is what <see cref="UpgradePath"/> is derived from.</summary>
        internal PackageSource Source { get; set; } = PackageSource.Unknown;

        /// <summary>True when a newer, installable version is on offer.</summary>
        internal bool HasOffer => Verdict == FrameworkUpdateVerdict.UpdateAvailable && Target != null;
    }
}
