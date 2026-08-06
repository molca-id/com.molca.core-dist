using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;

namespace Molca.Settings
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Build Settings", menuName = "Molca/Editor/Build Settings", order = 110)]
    public class BuildSettings : ScriptableObject
    {
        [Serializable]
        public class BuildProfile
        {
            /// <summary>
            /// Stable identity for this profile, independent of its display name.
            /// </summary>
            /// <remarks>
            /// A profile is referenced by more than the build system — a network environment declares which
            /// profiles enable it, and localization and integration policy are heading the same way. All of
            /// that used to key on <see cref="name"/>, which is a label a person edits in the Hub, so
            /// renaming "Staging" to "QA" silently unbound every reference to it with no error anywhere.
            /// Assigned once by <see cref="EnsureIds"/> and never rewritten; empty on profiles authored
            /// before this field existed, which is why name lookup still works.
            /// </remarks>
            [Tooltip("Stable identity, assigned automatically. Referenced by other systems; renaming the profile does not change it.")]
            public string id = "";

            public string name = "Development";
            public UnityEditor.BuildTarget target = UnityEditor.BuildTarget.StandaloneWindows64;
            public string outputPath = "Builds";

            [Tooltip("Scenes this profile builds, in order. Leave empty to build the enabled Editor Build Settings scenes.")]
            public List<UnityEditor.SceneAsset> scenes = new List<UnityEditor.SceneAsset>();

            public bool developmentBuild = true;
            public bool allowDebugging = true;

            public bool il2cpp = false;
            public bool compress = false;

            [Tooltip("Automatically run the player after building")]
            public bool autoRunPlayer = false;

            [Tooltip("Show the built player location in file explorer after building")]
            public bool showBuiltPlayer = true;

            [Tooltip("Clean build cache before building (slower but ensures clean build)")]
            public bool cleanBuildCache = false;

            [Tooltip("Connect to the profiler on startup")]
            public bool connectWithProfiler = false;

            [Tooltip("Enables deep profiling (impacts performance significantly)")]
            public bool deepProfiling = false;

            [Tooltip("Treat warnings as errors and fail the build")]
            public bool strictMode = false;

            [Tooltip("Include detailed build report information")]
            public bool detailedBuildReport = false;

            [Tooltip("Restore the original active build target after this build")]
            public bool restoreOriginalTarget = true;

            [Tooltip("The RuntimeManager prefab to use for this build")]
            public RuntimeManager runtimeManager;

            [Tooltip("The GlobalSettings asset to use for this build")]
            public GlobalSettings globalSettings;

            [Tooltip("Override application identifier (Android package name / iOS bundle ID). Applied only for Android and iOS. Leave empty to use project setting.")]
            public string applicationIdentifierOverride = "";

            [Tooltip("Build Addressables content before the player so the two are never out of sync. Aborts the build if the content build fails.")]
            public bool buildAddressablesFirst = false;

            [Tooltip("Build an Android App Bundle (.aab) instead of an APK. Required for Google Play uploads. Android only.")]
            public bool buildAppBundle = false;

            [Tooltip("Target CPU architectures for Android. IL2CPP release builds require ARM64 for Google Play. Android only.")]
            public AndroidArchitecture androidArchitectures = AndroidArchitecture.ARM64;

            [Tooltip("Apply the signing configuration below for this build (Android & iOS). Passwords are read from environment variables, never stored in this asset.")]
            public bool useCustomSigning = false;

            [Tooltip("Path to the Android keystore (.keystore/.jks), absolute or relative to the project root.")]
            public string androidKeystorePath = "";

            [Tooltip("Android key alias name within the keystore.")]
            public string androidKeyaliasName = "";

            [Tooltip("Name of the environment variable holding the keystore password.")]
            public string androidKeystorePassEnv = "MOLCA_ANDROID_KEYSTORE_PASS";

            [Tooltip("Name of the environment variable holding the key alias password.")]
            public string androidKeyaliasPassEnv = "MOLCA_ANDROID_KEYALIAS_PASS";

            [Tooltip("Apple Developer Team ID for iOS signing. Leave empty to keep the project setting.")]
            public string iosTeamId = "";

            [Tooltip("Use Xcode automatic signing for iOS.")]
            public bool iosAutomaticSigning = true;

            [Header("Defines")]
            [Tooltip("Scripting define symbols for the selected build target (semicolon separated)")]
            public string defineSymbols = "";

            /// <summary>True when this profile declares its own scene set.</summary>
            public bool HasSceneOverride => scenes != null && scenes.Count > 0;

            /// <summary>
            /// Resolves the scenes this profile builds.
            /// </summary>
            /// <param name="scenePaths">
            /// The profile's scene paths in order, or <c>null</c> when it declares none and the enabled
            /// Editor Build Settings scenes should be used.
            /// </param>
            /// <param name="failure">Why the scene set cannot be resolved; null on success.</param>
            /// <returns>True when the scene set is usable.</returns>
            /// <remarks>
            /// <para>
            /// Profiles previously had no scene set at all: every profile built the one global enabled list,
            /// so a development profile and a production profile could not ship different scenes — which is
            /// most of the reason to have profiles in the first place.
            /// </para>
            /// <para>
            /// <b>A missing entry is a failure, not a skip.</b> A deleted or unloadable scene asset leaves a
            /// null in the list, and quietly building the remaining scenes produces a player missing a level
            /// with no indication which one. Duplicates are collapsed, since a scene listed twice is a typo
            /// with no meaningful second interpretation.
            /// </para>
            /// </remarks>
            public bool TryResolveScenePaths(out string[] scenePaths, out string failure)
            {
                scenePaths = null;
                failure = null;

                if (!HasSceneOverride)
                    return true;

                var resolved = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < scenes.Count; i++)
                {
                    var scene = scenes[i];
                    if (scene == null)
                    {
                        failure =
                            $"profile '{name}' lists a scene at position {i + 1} that no longer exists " +
                            "(the scene asset was deleted or moved out of the project). Remove the empty " +
                            "entry or restore the scene.";
                        scenePaths = null;
                        return false;
                    }

                    var path = AssetDatabase.GetAssetPath(scene);
                    if (string.IsNullOrEmpty(path))
                    {
                        failure =
                            $"profile '{name}' lists a scene at position {i + 1} with no asset path.";
                        scenePaths = null;
                        return false;
                    }

                    if (seen.Add(path))
                        resolved.Add(path);
                }

                scenePaths = resolved.ToArray();
                return true;
            }
        }

        [SerializeField] private List<BuildProfile> profiles = new List<BuildProfile>();

        public IReadOnlyList<BuildProfile> Profiles => profiles;

        /// <summary>
        /// Finds a profile by stable id or by display name.
        /// </summary>
        /// <param name="profileName">A profile id or name; empty resolves to the first profile.</param>
        /// <returns>The profile, or null when the explicitly requested one does not exist.</returns>
        /// <remarks>
        /// Ids are tried first and matched exactly; names are matched case-insensitively, as they always
        /// were. This is what lets a stored reference survive a rename: a caller that recorded the id keeps
        /// resolving, and one that recorded the name keeps working until the name changes.
        /// </remarks>
        public BuildProfile GetProfile(string profileName)
        {
            if (TryGetProfile(profileName, out var profile))
                return profile;

            // An explicitly requested profile that doesn't exist is a configuration error —
            // failing loudly here beats silently building with the wrong (first) profile.
            var available = profiles.Count > 0
                ? string.Join(", ", profiles.ConvertAll(p => $"'{p.name}'"))
                : "(none)";
            Debug.LogError($"Build profile '{profileName}' not found in '{name}'. Available profiles: {available}.");
            return null;
        }

        /// <summary>
        /// Finds a profile by stable id or display name without logging when there is no match.
        /// </summary>
        /// <param name="profileName">A profile id or name; empty resolves to the first profile.</param>
        /// <param name="profile">The resolved profile, or null.</param>
        /// <returns>True when a profile was found.</returns>
        /// <remarks>
        /// The silent overload exists for callers whose job is to <em>report</em> a missing profile — a
        /// Doctor check validating stored bindings would otherwise log one console error per finding while
        /// producing the finding, which reads as a build failure rather than an audit result.
        /// </remarks>
        public bool TryGetProfile(string profileName, out BuildProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                profile = profiles.Count > 0 ? profiles[0] : null;
                return profile != null;
            }

            foreach (var candidate in profiles)
            {
                if (candidate != null && !string.IsNullOrEmpty(candidate.id) &&
                    string.Equals(candidate.id, profileName, StringComparison.Ordinal))
                {
                    profile = candidate;
                    return true;
                }
            }

            foreach (var candidate in profiles)
            {
                if (candidate != null &&
                    string.Equals(candidate.name, profileName, StringComparison.OrdinalIgnoreCase))
                {
                    profile = candidate;
                    return true;
                }
            }

            profile = null;
            return false;
        }

        /// <summary>
        /// Assigns a stable <see cref="BuildProfile.id"/> to every profile that lacks one.
        /// </summary>
        /// <returns>True when at least one id was assigned, so the caller can mark the asset dirty.</returns>
        /// <remarks>
        /// Called from the authoring surface rather than <c>OnEnable</c>: assigning ids means writing to the
        /// asset, and doing that during deserialization mutates it without dirtying it, so the ids would be
        /// regenerated on every domain reload and never persist — which for an identity is worse than not
        /// having one. An existing id is never rewritten.
        /// </remarks>
        public bool EnsureIds()
        {
            bool changed = false;
            foreach (var profile in profiles)
            {
                if (profile != null && string.IsNullOrEmpty(profile.id))
                {
                    profile.id = Guid.NewGuid().ToString("N");
                    changed = true;
                }
            }

            return changed;
        }

        private void OnEnable()
        {
            if (profiles == null || profiles.Count == 0)
            {
                profiles = new List<BuildProfile>
                {
                    new BuildProfile { name = "Development", developmentBuild = true, allowDebugging = true },
                    new BuildProfile { name = "Staging", developmentBuild = false, allowDebugging = false },
                    new BuildProfile { name = "Production", developmentBuild = false, allowDebugging = false }
                };
            }
        }
    }
}