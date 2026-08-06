using UnityEngine;
using UnityEngine.Serialization;
using UnityEditor;

namespace Molca.Settings
{
    /// <summary>
    /// Stores and manages the project's semantic version, build number, and changelog configuration.
    /// </summary>
    /// <remarks>
    /// The version components and <c>buildNumber</c> are authored configuration that is
    /// intentionally written back to the asset at build time (so the build number is tracked in
    /// version control). The commit each changelog entry was written at is recorded on the entry itself
    /// by <see cref="ChangelogWriter"/>, not on this asset and not in <see cref="EditorPrefs"/> — it is a
    /// fact about the project's history, so it belongs in the file that history lives in.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Version Settings", menuName = "Molca/Editor/Version Settings", order = 110)]
    public class VersionSettings : ScriptableObject
    {
        // Field names map to SemVer positions; FormerlySerializedAs preserves data authored under
        // the older mainVersion/stagingVersion/developmentVersion names.
        [FormerlySerializedAs("mainVersion")]
        [SerializeField] private int major = 0;
        [FormerlySerializedAs("stagingVersion")]
        [SerializeField] private int minor = 0;
        [FormerlySerializedAs("developmentVersion")]
        [SerializeField] private int patch = 1;
        [SerializeField] private int buildNumber = 1;

        [SerializeField] private string preReleaseIdentifier = "";
        [SerializeField] private string buildMetadata = "";
        [SerializeField] private bool includeGitCommitsInChangelog = true;

        // The former `autoSync` toggle is gone. It claimed to control whether a build syncs this asset
        // into PlayerSettings, but the build version preprocessor has always synced unconditionally, so
        // the toggle changed nothing and its tooltip described behaviour it did not have. This asset is
        // the project's version of record — a switch that lets the built player disagree with it is not
        // a feature. Old assets keep an orphaned `autoSync` key in their YAML, which Unity ignores.

        [SerializeField]
        [Tooltip("Automatically increment the build number after a successful build")]
        private bool autoIncrementBuildNumberOnBuild = false;

        [SerializeField]
        [Tooltip("Automatically append an entry to the changelog file after a successful build")]
        private bool autoAppendChangelogOnBuild = false;

        [SerializeField]
        [Tooltip("Changelog path relative to the project root (JSON format, e.g. CHANGELOG.json)")]
        private string changelogPath = "CHANGELOG.json";

        // -------------------------------------------------------------------
        // Version accessors
        // -------------------------------------------------------------------

        /// <summary>Returns the version string in Major.Minor.Patch format.</summary>
        public string GetVersionString() => $"{major}.{minor}.{patch}";

        /// <summary>Returns the version string formatted for the active build target.</summary>
        public string GetBundleVersionString() => GetBundleVersionString(EditorUserBuildSettings.activeBuildTarget);

        /// <summary>
        /// Returns the version string as it should be written to <see cref="PlayerSettings.bundleVersion"/>
        /// for <paramref name="target"/>.
        /// </summary>
        /// <param name="target">The target platform.</param>
        /// <returns>The full semantic version, or plain <c>Major.Minor.Patch</c> where the platform requires it.</returns>
        /// <remarks>
        /// <para>
        /// This used to ignore both its parameter and the pre-release/build-metadata fields, always
        /// returning <c>Major.Minor.Patch</c> — so a project could author <c>rc.1</c> in the Hub and ship
        /// a player that had never heard of it. The fields now reach the built player.
        /// </para>
        /// <para>
        /// <b>iOS is the exception, and it is Apple's.</b> <c>CFBundleShortVersionString</c> must be one
        /// to three dot-separated integers; <c>1.4.0-rc.1</c> is rejected at submission. iOS therefore
        /// gets the numeric version, and the pre-release identity travels in the build number instead.
        /// This is the reason the method takes a target at all.
        /// </para>
        /// </remarks>
        public string GetBundleVersionString(BuildTarget target) =>
            target == BuildTarget.iOS ? GetVersionString() : GetSemanticVersion();

        /// <summary>Returns the build number as a string.</summary>
        public string GetBuildNumberString() => buildNumber.ToString();

        /// <summary>Returns the full version string in Major.Minor.Patch.Build format.</summary>
        public string GetFullVersionString() => $"{GetVersionString()}.{buildNumber}";

        /// <summary>Returns the full semantic version string including pre-release and build metadata if set.</summary>
        public string GetSemanticVersion()
        {
            var version = GetVersionString();
            if (!string.IsNullOrEmpty(preReleaseIdentifier))
                version += $"-{preReleaseIdentifier}";
            if (!string.IsNullOrEmpty(buildMetadata))
                version += $"+{buildMetadata}";
            return version;
        }

        /// <summary>
        /// Returns the version a release is cut for: <c>Major.Minor.Patch</c> plus the pre-release
        /// identifier, without build metadata.
        /// </summary>
        /// <remarks>
        /// This is the release <em>identity</em>, and it is not the same string as
        /// <see cref="GetSemanticVersion"/>. SemVer §10 says build metadata is ignored when determining
        /// version precedence, so two builds differing only in metadata are the same release and must not
        /// produce two different tags. The pre-release identifier is the opposite: <c>1.4.0-rc.1</c> and
        /// <c>1.4.0</c> are different releases, and tagging the release candidate as <c>v1.4.0</c> both
        /// mislabels it and burns the tag the real release needs.
        /// </remarks>
        public string GetReleaseVersionString() =>
            string.IsNullOrEmpty(preReleaseIdentifier)
                ? GetVersionString()
                : $"{GetVersionString()}-{preReleaseIdentifier}";

        // -------------------------------------------------------------------
        // Version mutators
        // -------------------------------------------------------------------

        /// <summary>Increments the patch version component.</summary>
        public void IncrementPatch() => patch++;

        /// <summary>Increments the minor version component and resets patch to zero.</summary>
        public void IncrementMinor() { minor++; patch = 0; }

        /// <summary>Increments the major version component and resets minor and patch to zero.</summary>
        public void IncrementMajor() { major++; minor = 0; patch = 0; }

        /// <summary>Sets all version components explicitly.</summary>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when any component is negative, or buildNum is less than 1.</exception>
        public void SetVersion(int main, int staging, int development, int buildNum = 1)
        {
            if (main < 0) throw new System.ArgumentOutOfRangeException(nameof(main));
            if (staging < 0) throw new System.ArgumentOutOfRangeException(nameof(staging));
            if (development < 0) throw new System.ArgumentOutOfRangeException(nameof(development));
            if (buildNum < 1) throw new System.ArgumentOutOfRangeException(nameof(buildNum));

            major = main;
            minor = staging;
            patch = development;
            buildNumber = buildNum;
        }

        /// <summary>Returns true when all version components are in range.</summary>
        public bool IsValidVersion() =>
            major >= 0 && minor >= 0 && patch >= 0 && buildNumber >= 1;

        // -------------------------------------------------------------------
        // Pre-release / metadata
        // -------------------------------------------------------------------

        /// <summary>Returns the pre-release identifier, e.g. "alpha.1".</summary>
        public string GetPreReleaseIdentifier() => preReleaseIdentifier;

        /// <summary>Sets the pre-release identifier.</summary>
        public void SetPreReleaseIdentifier(string identifier) => preReleaseIdentifier = identifier;

        /// <summary>Clears the pre-release identifier.</summary>
        public void ClearPreReleaseIdentifier() => preReleaseIdentifier = "";

        /// <summary>Returns the build metadata string.</summary>
        public string GetBuildMetadata() => buildMetadata;

        /// <summary>Sets the build metadata string.</summary>
        public void SetBuildMetadata(string metadata) => buildMetadata = metadata;

        /// <summary>Clears the build metadata string.</summary>
        public void ClearBuildMetadata() => buildMetadata = "";

        // -------------------------------------------------------------------
        // Unity PlayerSettings sync
        // -------------------------------------------------------------------

        /// <summary>
        /// Writes this asset's version to <see cref="PlayerSettings.bundleVersion"/>, formatted for the
        /// active build target.
        /// </summary>
        /// <remarks>
        /// Unconditional. The old <c>force</c> parameter guarded a toggle that the build path always
        /// overrode anyway; see the note on the removed <c>autoSync</c> field.
        /// </remarks>
        public void SyncToUnityPlayerSettings()
        {
            var version = GetBundleVersionString();
            PlayerSettings.bundleVersion = version;
            Debug.Log($"VersionSettings: Synchronized Unity PlayerSettings version to {version}");
        }

        /// <summary>
        /// Writes the platform-specific version code derived from the build number:
        /// <c>PlayerSettings.Android.bundleVersionCode</c> and <c>PlayerSettings.iOS.buildNumber</c>.
        /// No-op for any other target.
        /// </summary>
        /// <param name="target">The build target being built.</param>
        /// <remarks>
        /// <para>
        /// App stores require a monotonically increasing integer version code per upload; the SemVer
        /// version name from <see cref="GetBundleVersionString()"/> does not satisfy that. Pair this
        /// with <c>autoIncrementBuildNumberOnBuild</c> so every build produces a fresh, higher code.
        /// </para>
        /// <para>
        /// <b>An iOS player cannot carry a pre-release identifier at all</b>, which is why building one
        /// warns. Both Apple version fields are numeric — <c>CFBundleShortVersionString</c> is one to
        /// three integers and <c>CFBundleVersion</c> the same — so there is nowhere in the app's own
        /// metadata for <c>rc.1</c> to live. The documentation used to claim the identifier "travels in
        /// the build number"; it does not, and quietly dropping it is how an rc reaches TestFlight
        /// indistinguishable from the final build. It survives in <c>build-info.json</c> and in the
        /// embedded build-info asset, both of which record the full semantic version.
        /// </para>
        /// </remarks>
        public void SyncPlatformVersionCode(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.Android:
                    PlayerSettings.Android.bundleVersionCode = buildNumber;
                    Debug.Log($"VersionSettings: Set Android bundleVersionCode to {buildNumber}");
                    break;
                case BuildTarget.iOS:
                    PlayerSettings.iOS.buildNumber = buildNumber.ToString();
                    Debug.Log($"VersionSettings: Set iOS buildNumber to {buildNumber}");
                    if (!string.IsNullOrEmpty(preReleaseIdentifier))
                    {
                        Debug.LogWarning(
                            $"VersionSettings: pre-release identifier '{preReleaseIdentifier}' is not carried by " +
                            "the iOS player — Apple's version fields are numeric only. The player reports " +
                            $"{GetVersionString()} (build {buildNumber}); the full version is recorded in " +
                            "build-info.json beside the output and in the embedded build-info asset.");
                    }
                    break;
            }
        }

        // -------------------------------------------------------------------
        // Build lifecycle
        // -------------------------------------------------------------------

        /// <summary>
        /// Called after a build <em>succeeds</em>: appends the changelog entry when
        /// <c>autoAppendChangelogOnBuild</c> is enabled, then advances the build number when
        /// <c>autoIncrementBuildNumberOnBuild</c> is enabled.
        /// </summary>
        /// <param name="buildNotes">Optional notes to include in the changelog entry.</param>
        /// <remarks>
        /// <para>
        /// <b>Both halves live here, in this order, on purpose.</b> The changelog entry must name the
        /// version that was just built, so it is written before the number moves.
        /// </para>
        /// <para>
        /// <b>The changelog used to be written when a build started</b>, from a build preprocessor that
        /// ran before the reference and localization gates could abort. A project whose builds were
        /// failing accumulated one changelog entry per attempt, each naming a version no artifact ever
        /// carried — and because the writer advances its "commits since last build" marker as it writes,
        /// each failed attempt also consumed the commit range the next real entry should have reported.
        /// A changelog is a record of what shipped; an attempt is not a thing that shipped.
        /// </para>
        /// </remarks>
        public void NotifyBuildComplete(string buildNotes)
        {
            if (autoAppendChangelogOnBuild)
                CreateChangelogWriter().AppendBuildEntry(GetFullVersionString(), buildNotes);

            if (autoIncrementBuildNumberOnBuild)
                buildNumber++;
        }

        /// <summary>Called after a build completes, with no changelog notes.</summary>
        public void NotifyBuildComplete() => NotifyBuildComplete(null);

        /// <summary>
        /// Appends a <c>release</c> entry for <paramref name="version"/> to this project's changelog.
        /// </summary>
        /// <param name="version">The released version identity (see <see cref="GetReleaseVersionString"/>).</param>
        /// <param name="notes">Optional release notes prepended to the entry.</param>
        /// <remarks>
        /// The release path writes through this asset rather than constructing its own
        /// <c>ChangelogWriter</c>. It used to do the latter with <c>includeGitCommits: true</c> hardcoded,
        /// so a project that had deliberately turned <em>Include Git Commits</em> off still got commit
        /// subjects in every release entry — the toggle governed builds and silently did not govern
        /// releases. Changelog policy belongs to the asset that declares it.
        /// </remarks>
        public void AppendReleaseEntry(string version, string notes) =>
            CreateChangelogWriter().AppendReleaseEntry(version, notes);

        // -------------------------------------------------------------------
        // Changelog history
        // -------------------------------------------------------------------

        /// <summary>Returns all version history entries from the changelog file.</summary>
        public VersionHistoryEntry[] GetVersionHistory() => CreateChangelogWriter().Read();

        /// <summary>Clears all version history entries from the changelog file.</summary>
        public void ClearVersionHistory() => CreateChangelogWriter().Clear();

        // -------------------------------------------------------------------
        // Properties used by external systems
        // -------------------------------------------------------------------

        /// <summary>When true, the build number is incremented automatically after each build.</summary>
        public bool AutoIncrementBuildNumberOnBuild => autoIncrementBuildNumberOnBuild;

        /// <summary>When true, a changelog entry is appended automatically before each build.</summary>
        public bool AutoAppendChangelogOnBuild => autoAppendChangelogOnBuild;

        /// <summary>Path to the JSON changelog file, relative to the project root.</summary>
        public string ChangelogPath => changelogPath;

        // -------------------------------------------------------------------
        // Private helpers
        // -------------------------------------------------------------------

        private ChangelogWriter CreateChangelogWriter() =>
            new ChangelogWriter(changelogPath, includeGitCommitsInChangelog);
    }
}
