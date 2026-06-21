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

            /// <summary>Initializes the release event payload.</summary>
            public ReleaseEventArgs(string version, string notes, string changelogPath)
            {
                Version = version;
                Notes = notes;
                ChangelogPath = changelogPath;
            }
        }

        /// <summary>
        /// Raised after a successful <see cref="CreateRelease"/> (version synced and changelog written),
        /// before any git tag is attempted. Integrations (e.g. ClickUp release sync) subscribe here to
        /// react to a release without coupling this tool to them. Handlers must not throw — exceptions are
        /// swallowed so a faulty subscriber cannot fail the release.
        /// </summary>
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
            /// <summary>The ref the commits were taken since (a tag, or null for recent history).</summary>
            public readonly string SinceRef;

            /// <summary>Initializes a bump suggestion.</summary>
            public BumpSuggestion(VersionBump bump, IReadOnlyList<string> commits, string sinceRef)
            {
                Bump = bump;
                Commits = commits;
                SinceRef = sinceRef;
            }
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName;

        /// <summary>
        /// Suggests the next version bump from conventional commits since the most recent <c>v*</c>
        /// release tag (or recent history when no such tag exists).
        /// </summary>
        /// <returns>The suggested bump and the commits it was derived from.</returns>
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

            var commits = GitLogReader.GetCommitMessages(root, sinceRef, out _, out _);
            return new BumpSuggestion(ConventionalCommits.SuggestBump(commits), commits, sinceRef);
        }

        /// <summary>Applies a <see cref="VersionBump"/> to <paramref name="settings"/> (no-op for <see cref="VersionBump.None"/>).</summary>
        /// <param name="settings">The version settings to mutate.</param>
        /// <param name="bump">The bump to apply.</param>
        public static void ApplyBump(VersionSettings settings, VersionBump bump)
        {
            if (settings == null)
                return;

            switch (bump)
            {
                case VersionBump.Major: settings.IncrementMajor(); break;
                case VersionBump.Minor: settings.IncrementMinor(); break;
                case VersionBump.Patch: settings.IncrementPatch(); break;
                default: return;
            }

            EditorUtility.SetDirty(settings);
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
        public static ReleaseResult CreateRelease(VersionSettings settings, bool createGitTag, string notes = null)
        {
            if (settings == null)
                return new ReleaseResult(false, null, false, "No VersionSettings assigned.");
            if (!settings.IsValidVersion())
                return new ReleaseResult(false, null, false, "Version is invalid; fix the components first.");

            var version = settings.GetVersionString();

            settings.SyncToUnityPlayerSettings(force: true);
            settings.SyncPlatformVersionCode(EditorUserBuildSettings.activeBuildTarget);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            new ChangelogWriter(settings.ChangelogPath, includeGitCommits: true)
                .AppendReleaseEntry(version, notes);

            RaiseReleaseCreated(new ReleaseEventArgs(version, notes, settings.ChangelogPath));

            if (!createGitTag)
                return new ReleaseResult(true, version, false, $"Released {version} (version synced, changelog updated).");

            var root = ProjectRoot;
            var tagName = "v" + version;
            if (!string.IsNullOrEmpty(root) &&
                GitLogReader.TryRunGit(root, $"tag -a {tagName} -m \"Release {tagName}\"", out _))
            {
                return new ReleaseResult(true, version, true, $"Released {version} and created tag {tagName} (not pushed).");
            }

            return new ReleaseResult(false, version, false,
                $"Version synced and changelog updated, but creating git tag '{tagName}' failed (it may already exist). See the console.");
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
