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
    /// version control). Transient per-build state that should <em>not</em> live in the asset — e.g.
    /// the last-build commit hash used for changelog diffing — is kept in <see cref="EditorPrefs"/>
    /// by <see cref="ChangelogWriter"/> instead.
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

        [SerializeField]
        [Tooltip("Automatically synchronize Unity PlayerSettings version when build starts")]
        private bool autoSync = false;

        [SerializeField]
        [Tooltip("Automatically increment build number when a build starts")]
        private bool autoIncrementBuildNumberOnBuild = false;

        [SerializeField]
        [Tooltip("Automatically append build info to a changelog file when a build starts")]
        private bool autoAppendChangelogOnBuild = false;

        [SerializeField]
        [Tooltip("Changelog path relative to the project root (YAML format, e.g. CHANGELOG.yaml)")]
        private string changelogPath = "CHANGELOG.yaml";

        // -------------------------------------------------------------------
        // Version accessors
        // -------------------------------------------------------------------

        /// <summary>Returns the version string in Major.Minor.Patch format.</summary>
        public string GetVersionString() => $"{major}.{minor}.{patch}";

        /// <summary>Returns the version string formatted for the active build target.</summary>
        public string GetBundleVersionString() => GetBundleVersionString(EditorUserBuildSettings.activeBuildTarget);

        /// <summary>Returns the version string formatted for <paramref name="target"/>.</summary>
        /// <param name="target">The target platform.</param>
        public string GetBundleVersionString(BuildTarget target) => GetVersionString();

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

        /// <summary>Synchronizes the version string to <see cref="PlayerSettings.bundleVersion"/>.</summary>
        /// <param name="force">When true, syncs even if <c>autoSync</c> is disabled.</param>
        public void SyncToUnityPlayerSettings(bool force = false)
        {
            if (!force && !autoSync)
                return;

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
        /// App stores require a monotonically increasing integer version code per upload; the SemVer
        /// version name from <see cref="GetBundleVersionString()"/> does not satisfy that. Pair this
        /// with <c>autoIncrementBuildNumberOnBuild</c> so every build produces a fresh, higher code.
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
                    break;
            }
        }

        // -------------------------------------------------------------------
        // Build lifecycle
        // -------------------------------------------------------------------

        /// <summary>
        /// Called before a build starts. Appends a changelog entry when <c>autoAppendChangelogOnBuild</c> is enabled.
        /// Build number is incremented after the build via <see cref="NotifyBuildComplete"/>.
        /// </summary>
        /// <param name="buildNotes">Optional notes to include in the changelog entry.</param>
        public void PrepareForBuild(string buildNotes)
        {
            if (autoAppendChangelogOnBuild)
                CreateChangelogWriter().AppendBuildEntry(GetFullVersionString(), buildNotes);
        }

        /// <summary>Called after a build completes. Increments the build number when <c>autoIncrementBuildNumberOnBuild</c> is enabled.</summary>
        public void NotifyBuildComplete()
        {
            if (autoIncrementBuildNumberOnBuild)
                buildNumber++;
        }

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

        /// <summary>Path to the YAML changelog file, relative to the project root.</summary>
        public string ChangelogPath => changelogPath;

        // -------------------------------------------------------------------
        // Private helpers
        // -------------------------------------------------------------------

        private ChangelogWriter CreateChangelogWriter() =>
            new ChangelogWriter(changelogPath, includeGitCommitsInChangelog);
    }
}
