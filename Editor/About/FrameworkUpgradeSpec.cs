using System;

namespace Molca.Editor.About
{
    /// <summary>
    /// Reads the one upgrade instruction the feed publishes (<c>FrameworkReleaseDto.upgradeSpec</c>) well
    /// enough to decide what can be done with it, and normalizes it into the form Unity accepts.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/About/</c>. Pure string handling — no Package Manager,
    /// no UI, no network — so <see cref="FrameworkUpdateEvaluator"/> (which decides the affordance) and
    /// <see cref="FrameworkUpgrader"/> (which applies it) parse the spec exactly once, the same way, and
    /// <c>Tests/Editor/About/FrameworkUpdateTests.cs</c> can exercise it directly.
    /// <para>
    /// This class never invents a spec. It only recognizes the two published shapes — a registry spec
    /// (<c>com.molca.core@1.15.0</c>) and a git ref (<c>https://…/molca-core.git#v1.15.0</c>) — and refuses
    /// to classify anything else as git.
    /// </para>
    /// </remarks>
    internal static class FrameworkUpgradeSpec
    {
        private const string CorePackagePrefix = "com.molca.core@";

        /// <summary>URL schemes Unity's Package Manager accepts for a git dependency.</summary>
        private static readonly string[] GitSchemes = { "https://", "http://", "ssh://", "git://", "file://" };

        /// <summary>
        /// Trims the spec and drops a leading <c>git+</c>.
        /// </summary>
        /// <param name="spec">The raw spec as published.</param>
        /// <returns>The spec in the form Unity accepts; never <c>null</c>.</returns>
        /// <remarks>
        /// <c>git+https://…</c> is npm's spelling and the shape the control-plane admin form suggests, but
        /// neither <see cref="UnityEditor.PackageManager.Client"/> nor <c>Packages/manifest.json</c> accepts
        /// the prefix. Stripping it here means a spec published in either spelling works, and the clipboard
        /// fallback pastes something that actually resolves.
        /// </remarks>
        internal static string Normalize(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return string.Empty;

            string trimmed = spec.Trim();
            return trimmed.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring(4).TrimStart()
                : trimmed;
        }

        /// <summary>
        /// True when the spec points at a git repository rather than a registry version.
        /// </summary>
        /// <param name="spec">The raw spec as published.</param>
        /// <remarks>
        /// Recognizes the URL schemes Unity supports plus the scp-like <c>git@host:owner/repo.git</c> form.
        /// A leading <c>com.molca.core@</c> is ignored first, so the package-id spelling
        /// (<c>com.molca.core@https://…</c>) classifies the same as the bare URL.
        /// </remarks>
        internal static bool IsGitUrl(string spec)
        {
            string url = StripName(Normalize(spec));
            if (url.Length == 0) return false;

            foreach (string scheme in GitSchemes)
                if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                    return true;

            // scp-like SSH: user@host:path. Distinguished from a registry spec by the ':' that must follow
            // the host — 'com.molca.core@1.15.0' has no colon, 'git@github.com:molca/core.git' does.
            int at = url.IndexOf('@');
            return at > 0 && url.IndexOf(':', at) > at;
        }

        /// <summary>
        /// True when a git spec pins an explicit revision (the <c>#tag</c>, <c>#branch</c>, or <c>#sha</c>
        /// fragment).
        /// </summary>
        /// <param name="spec">The raw spec as published.</param>
        /// <remarks>
        /// A git URL with no fragment resolves to whatever the default branch's HEAD happens to be at that
        /// moment, which is not the version the panel just named. That gap is why an unpinned spec is not
        /// offered as a one-click update: the developer is shown the dependency line instead, so what they
        /// are pinning is visible before it lands.
        /// </remarks>
        internal static bool HasRevision(string spec)
        {
            string url = Normalize(spec);
            int hash = url.IndexOf('#');
            return hash > 0 && hash < url.Length - 1;
        }

        /// <summary>
        /// Strips a leading <c>com.molca.core@</c> so the remainder can be used as a manifest value.
        /// </summary>
        /// <param name="spec">A normalized spec.</param>
        internal static string StripName(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return string.Empty;

            // Only a *leading* name@ is stripped; an '@' inside a git URL (user@host) must survive.
            return spec.StartsWith(CorePackagePrefix, StringComparison.Ordinal)
                ? spec.Substring(CorePackagePrefix.Length)
                : spec;
        }
    }
}
