using System;
using System.Collections.Generic;
using System.IO;
using Molca.Settings;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// App-version release helper: treats <see cref="VersionSettings"/> as the source of truth,
    /// syncs it to PlayerSettings, appends a release entry to the app changelog, and (optionally)
    /// creates an annotated <c>v{version}</c> git tag. Suggests the next bump from conventional
    /// commits since the most recent release tag.
    /// </summary>
    /// <remarks>
    /// This is the consumer-project release path (a project that builds on the framework). It does
    /// not touch <c>com.molca.core</c>'s own <c>package.json</c> version, which is the framework
    /// package's separate release concern.
    /// </remarks>
    public static class ReleaseTool
    {
        /// <summary>Describes a completed release for <see cref="ReleaseCreated"/> subscribers.</summary>
        public readonly struct ReleaseEventArgs
        {
            /// <summary>The released version string.</summary>
            public readonly string Version;
            /// <summary>The raw notes passed to the release (may be null/empty).</summary>
            public readonly string Notes;
            /// <summary>Path to the changelog the release entry was written to (relative to project root).</summary>
            public readonly string ChangelogPath;

            /// <summary>
            /// The annotated tag created for this release (e.g. <c>v1.4.0</c>), or null when the release was
            /// cut without one or tagging failed.
            /// </summary>
            public readonly string TagName;

            /// <summary>
            /// The full commit hash the release was cut at, or null when it could not be resolved.
            /// </summary>
            /// <remarks>
            /// A subscriber that publishes to a forge should pass this as the release target. Without it the
            /// forge creates its own tag at the default branch head, which is a different object from the
            /// local annotated tag and need not be the same commit.
            /// </remarks>
            public readonly string Commit;

            /// <summary>Initializes the release event payload.</summary>
            public ReleaseEventArgs(string version, string notes, string changelogPath)
                : this(version, notes, changelogPath, null, null)
            {
            }

            /// <summary>Initializes the release event payload, including the released git ref.</summary>
            /// <param name="version">The released version string.</param>
            /// <param name="notes">The raw notes passed to the release.</param>
            /// <param name="changelogPath">Path to the changelog written.</param>
            /// <param name="tagName">The tag created, or null when none was.</param>
            /// <param name="commit">The commit released, or null when unresolved.</param>
            public ReleaseEventArgs(string version, string notes, string changelogPath, string tagName, string commit)
            {
                Version = version;
                Notes = notes;
                ChangelogPath = changelogPath;
                TagName = tagName;
                Commit = commit;
            }
        }

        /// <summary>
        /// Raised after a successful <see cref="CreateRelease"/> — version synced, changelog written, and
        /// the git tag created (or established as not wanted). Integrations (e.g. ClickUp release sync)
        /// subscribe here to react to a release without coupling this tool to them. Handlers must not throw
        /// — exceptions are swallowed so a faulty subscriber cannot fail the release.
        /// </summary>
        /// <remarks>
        /// <b>Raised after tagging, deliberately.</b> It used to fire before, so a subscriber publishing to
        /// a forge got there first: the forge minted its own lightweight tag at the default branch head, and
        /// the local annotated tag created moments later was a different object, possibly at a different
        /// commit. Nothing reported the divergence. Firing afterwards also means
        /// <see cref="ReleaseEventArgs.TagName"/> and <see cref="ReleaseEventArgs.Commit"/> describe
        /// something that exists rather than something intended.
        /// </remarks>
        public static event Action<ReleaseEventArgs> ReleaseCreated;

        /// <summary>Outcome of a <see cref="CreateRelease"/> call.</summary>
        public readonly struct ReleaseResult
        {
            /// <summary>True when the release completed (version synced and changelog written).</summary>
            public readonly bool Success;
            /// <summary>The released version string.</summary>
            public readonly string Version;
            /// <summary>True when a git tag was created.</summary>
            public readonly bool TagCreated;
            /// <summary>Human-readable summary for display.</summary>
            public readonly string Message;

            /// <summary>Initializes a release result.</summary>
            public ReleaseResult(bool success, string version, bool tagCreated, string message)
            {
                Success = success;
                Version = version;
                TagCreated = tagCreated;
                Message = message;
            }
        }

        /// <summary>A suggested version bump and the commit subjects it was derived from.</summary>
        public readonly struct BumpSuggestion
        {
            /// <summary>The largest bump implied by the commits.</summary>
            public readonly VersionBump Bump;
            /// <summary>The commit subjects evaluated.</summary>
            public readonly IReadOnlyList<string> Commits;
            /// <summary>The release tag the commits were taken since, or null when none was found.</summary>
            public readonly string SinceRef;

            /// <summary>Initializes a bump suggestion.</summary>
            public BumpSuggestion(VersionBump bump, IReadOnlyList<string> commits, string sinceRef)
            {
                Bump = bump;
                Commits = commits ?? Array.Empty<string>();
                SinceRef = sinceRef;
            }

            /// <summary>
            /// True when the suggestion was measured from a release tag, and is therefore a statement
            /// about "everything since the last release" rather than a guess.
            /// </summary>
            /// <remarks>
            /// Without a <c>v*</c> tag there is no baseline, and this used to fall back to
            /// <c>HEAD~10..HEAD</c> — an arbitrary ten-commit window whose bump was then presented in the
            /// UI as derived from history. A caller must show a suggestion with no baseline as
            /// unmeasurable, not as <see cref="VersionBump.None"/> meaning "nothing changed".
            /// </remarks>
            public bool HasBaseline => !string.IsNullOrEmpty(SinceRef);
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName;

        /// <summary>
        /// Suggests the next version bump from the conventional commits since the most recent <c>v*</c>
        /// release tag.
        /// </summary>
        /// <returns>
        /// The suggested bump and the commits it was derived from. When no release tag exists there is no
        /// baseline to measure from, so the bump is <see cref="VersionBump.None"/> and
        /// <see cref="BumpSuggestion.HasBaseline"/> is false — the caller must say so rather than present
        /// the result as a reading of history.
        /// </returns>
        public static BumpSuggestion SuggestBump()
        {
            var root = ProjectRoot;
            if (string.IsNullOrEmpty(root))
                return new BumpSuggestion(VersionBump.None, Array.Empty<string>(), null);

            string sinceRef = null;
            if (GitLogReader.TryRunGit(root, "describe --tags --abbrev=0 --match v*", out var tag))
            {
                sinceRef = tag.Trim();
                if (sinceRef.Length == 0)
                    sinceRef = null;
            }

            if (sinceRef == null)
            {
                // Report what is there for context, but never a bump: GetCommitMessages falls back to
                // HEAD~10 when handed no baseline, and ten is not a fact about this project's release
                // history. The first release is the one a person picks.
                var recent = GitLogReader.GetCommitMessages(root, null, out _, out _);
                return new BumpSuggestion(VersionBump.None, recent, null);
            }

            var commits = GitLogReader.GetCommitMessages(root, sinceRef, out _, out _);
            return new BumpSuggestion(ConventionalCommits.SuggestBump(commits), commits, sinceRef);
        }

        /// <summary>Applies a <see cref="VersionBump"/> to <paramref name="settings"/> (no-op for <see cref="VersionBump.None"/>).</summary>
        /// <param name="settings">The version settings to mutate.</param>
        /// <param name="bump">The bump to apply.</param>
        /// <returns>True when a bump was applied; false for a null asset or <see cref="VersionBump.None"/>.</returns>
        /// <remarks>
        /// <para>
        /// A bump clears the pre-release identifier. Carrying it forward turned <c>1.4.0-rc.1</c> into
        /// <c>1.5.0-rc.1</c> — a version claiming to be a release candidate for a release nobody had
        /// started preparing, which then tags and publishes as one.
        /// </para>
        /// <para>
        /// The build number is deliberately <em>not</em> reset. App stores require it to increase
        /// monotonically across every upload for an application, not per version, so rewinding it makes
        /// the next upload rejected rather than fresh.
        /// </para>
        /// </remarks>
        public static bool ApplyBump(VersionSettings settings, VersionBump bump)
        {
            if (settings == null)
                return false;

            switch (bump)
            {
                case VersionBump.Major: settings.IncrementMajor(); break;
                case VersionBump.Minor: settings.IncrementMinor(); break;
                case VersionBump.Patch: settings.IncrementPatch(); break;
                default: return false;
            }

            settings.ClearPreReleaseIdentifier();
            EditorUtility.SetDirty(settings);
            return true;
        }

        /// <summary>
        /// Cuts a release for the current <paramref name="settings"/> version: syncs PlayerSettings
        /// (version name + platform version code for the active target), appends a release changelog
        /// entry, and optionally creates an annotated <c>v{version}</c> git tag. The tag is not pushed.
        /// </summary>
        /// <param name="settings">The version settings to release.</param>
        /// <param name="createGitTag">When true, creates a local annotated <c>v{version}</c> tag.</param>
        /// <param name="notes">Optional release notes prepended to the changelog entry.</param>
        /// <returns>The release outcome.</returns>
        /// <remarks>
        /// <para>
        /// The released identity is <see cref="VersionSettings.GetReleaseVersionString"/> — the numeric
        /// version plus any pre-release identifier. It used to be the numeric version alone, so releasing
        /// <c>1.4.0-rc.1</c> wrote a changelog entry for "1.4.0" and tagged it <c>v1.4.0</c>; the real
        /// 1.4.0 release then could not be tagged at all, because its tag had been spent on a release
        /// candidate.
        /// </para>
        /// <para>
        /// <b>Everything that can be checked is checked before anything is written.</b> A tag that already
        /// exists is the ordinary way this fails, and the old order discovered it last — after PlayerSettings
        /// had been synced, the changelog entry appended and <see cref="ReleaseCreated"/> raised, at which
        /// point it returned <c>Success = false</c> for a release that had substantially happened. A caller
        /// cannot act on that, and a partially-recorded release is worse than a refused one.
        /// </para>
        /// </remarks>
        public static ReleaseResult CreateRelease(VersionSettings settings, bool createGitTag, string notes = null)
        {
            if (settings == null)
                return new ReleaseResult(false, null, false, "No VersionSettings assigned.");
            if (!settings.IsValidVersion())
                return new ReleaseResult(false, null, false, "Version is invalid; fix the components first.");

            var version = settings.GetReleaseVersionString();
            var root = ProjectRoot;
            var tagName = "v" + version;

            // Pre-flight, before any side effect. Refusing here leaves the project exactly as it was.
            if (createGitTag)
            {
                if (string.IsNullOrEmpty(root))
                {
                    return new ReleaseResult(false, version, false,
                        "Cannot resolve the project root, so no git tag can be created. Nothing was written.");
                }

                if (!GitLogReader.IsGitRepository(root))
                {
                    return new ReleaseResult(false, version, false,
                        $"'{root}' is not a git repository, so tag '{tagName}' cannot be created. Nothing was written. " +
                        "Clear the tag option to release without one.");
                }

                if (GitLogReader.TagExists(root, tagName))
                {
                    return new ReleaseResult(false, version, false,
                        $"Tag '{tagName}' already exists — {version} has been released before. Nothing was written. " +
                        "Bump the version (or set a pre-release identifier) and release again.");
                }
            }

            settings.SyncToUnityPlayerSettings(EditorUserBuildSettings.activeBuildTarget);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            settings.AppendReleaseEntry(version, notes);

            if (!createGitTag)
            {
                RaiseReleaseCreated(new ReleaseEventArgs(
                    version, notes, settings.ChangelogPath, null, ResolveHeadCommit(root)));
                return new ReleaseResult(true, version, false, $"Released {version} (version synced, changelog updated).");
            }

            bool tagged = GitLogReader.TryRunGit(root, $"tag -a {tagName} -m \"Release {tagName}\"", out _);

            // Subscribers are notified only once the tag question is settled, so a provider publishing to a
            // forge can target the commit that was released instead of letting the forge pick one.
            RaiseReleaseCreated(new ReleaseEventArgs(
                version, notes, settings.ChangelogPath, tagged ? tagName : null, ResolveHeadCommit(root)));

            if (tagged)
                return new ReleaseResult(true, version, true, $"Released {version} and created tag {tagName} (not pushed).");

            // The release itself is recorded and cannot be un-recorded, so this reports success with the
            // tag named as the outstanding step rather than describing the whole release as failed.
            return new ReleaseResult(true, version, false,
                $"Released {version} (version synced, changelog updated), but creating tag '{tagName}' failed — " +
                "see the console. Create it by hand, or re-run after resolving the git error.");
        }

        // The full hash of HEAD, or null when it cannot be read. Full rather than short because it is handed
        // to remote APIs as a commitish, where an abbreviation is accepted but ambiguous.
        private static string ResolveHeadCommit(string root)
        {
            if (string.IsNullOrEmpty(root))
                return null;
            if (!GitLogReader.TryRunGit(root, "rev-parse HEAD", out var hash))
                return null;

            var trimmed = hash?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        // Notifies subscribers, isolating each handler so a faulty integration cannot fail the release.
        private static void RaiseReleaseCreated(ReleaseEventArgs args)
        {
            var handlers = ReleaseCreated;
            if (handlers == null)
                return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action<ReleaseEventArgs>)handler).Invoke(args); }
                catch (Exception ex) { Debug.LogWarning($"ReleaseTool: a ReleaseCreated handler threw.\n{ex}"); }
            }
        }
    }
}
