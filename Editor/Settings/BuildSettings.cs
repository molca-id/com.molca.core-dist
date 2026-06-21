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
            public string name = "Development";
            public UnityEditor.BuildTarget target = UnityEditor.BuildTarget.StandaloneWindows64;
            public string outputPath = "Builds";

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
        }

        [SerializeField] private List<BuildProfile> profiles = new List<BuildProfile>();

        public IReadOnlyList<BuildProfile> Profiles => profiles;

        public BuildProfile GetProfile(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                return profiles.Count > 0 ? profiles[0] : null;
            }

            foreach (var profile in profiles)
            {
                if (string.Equals(profile.name, profileName, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }

            // An explicitly requested profile that doesn't exist is a configuration error —
            // failing loudly here beats silently building with the wrong (first) profile.
            var available = profiles.Count > 0
                ? string.Join(", ", profiles.ConvertAll(p => $"'{p.name}'"))
                : "(none)";
            Debug.LogError($"Build profile '{profileName}' not found in '{name}'. Available profiles: {available}.");
            return null;
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