using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using Molca.ContentPackage;
using Molca.Editor.ContentPackage;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// The schema-v1 delivery path: build Addressables to a configured local path, write
    /// <c>packages.json</c> beside it, and check what each package resolved to.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> rendered by <see cref="ContentDeliveryView"/>, beside the legacy remote
    /// URLs it belongs to.
    /// <para>
    /// <b>Not the same thing as Ship → Verify.</b> That page builds into a throwaway staging directory
    /// and produces a signed release candidate. This builds into the path a
    /// <see cref="ContentPackageBuildConfig"/> names, for a project that still uploads a catalog
    /// somewhere itself. The two are alternatives, which is why they sit on different pages instead of
    /// two buttons next to each other — the surface this replaced had exactly that, and the one with a
    /// button was the superseded one.
    /// </para>
    /// <para>
    /// <b>A profile mismatch is reported, never fixed.</b> This used to write <c>RemoteBuildPath</c>,
    /// <c>RemoteLoadPath</c> and the catalog flags into the shared
    /// <see cref="AddressableAssetSettings"/> asset on every build. That asset is version controlled
    /// and shared by the team, so a local build silently rewrote it to whoever built last and the diff
    /// surfaced in someone else's commit.
    /// </para>
    /// </remarks>
    internal static class ContentLegacyBuild
    {
        private const string BuildConfigPrefKey = "Molca.ContentPackage.BuildConfigGuid";

        /// <summary>One package's outcome from a verification pass over the built output.</summary>
        internal readonly struct VerifyRow
        {
            /// <summary>The package.</summary>
            public string PackageId { get; }

            /// <summary>Bundles found for it, or 0.</summary>
            public int Bundles { get; }

            /// <summary>Total bytes of those bundles.</summary>
            public long Bytes { get; }

            /// <summary>Why nothing was found, or null.</summary>
            public string Error { get; }

            /// <summary>Whether the package resolved to something.</summary>
            public bool Ok => string.IsNullOrEmpty(Error) && Bundles > 0;

            internal VerifyRow(string packageId, int bundles, long bytes, string error)
            {
                PackageId = packageId;
                Bundles = bundles;
                Bytes = bytes;
                Error = error;
            }
        }

        /// <summary>The build config remembered for this project, or null.</summary>
        /// <returns>The config asset, or null when none is chosen or it was deleted.</returns>
        public static ContentPackageBuildConfig LoadConfig()
        {
            string guid = MolcaEditorPrefs.GetString(BuildConfigPrefKey, "");
            if (string.IsNullOrEmpty(guid)) return null;

            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAssetAtPath<ContentPackageBuildConfig>(path);
        }

        /// <summary>Remembers the chosen build config for this project.</summary>
        /// <param name="config">The config, or null to forget it.</param>
        /// <remarks>
        /// Stored per user rather than on the settings asset: which local path someone builds to is a
        /// machine fact, and putting it in a shared asset is what the profile write-back above did.
        /// </remarks>
        public static void SaveConfig(ContentPackageBuildConfig config)
        {
            string path = config != null ? AssetDatabase.GetAssetPath(config) : "";
            MolcaEditorPrefs.SetString(BuildConfigPrefKey,
                string.IsNullOrEmpty(path) ? "" : AssetDatabase.AssetPathToGUID(path));
        }

        /// <summary>Creates a build config asset where the user chooses, and remembers it.</summary>
        /// <returns>The created config, or null when cancelled.</returns>
        public static ContentPackageBuildConfig CreateConfig()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create Build Config", "ContentPackageBuildConfig", "asset",
                "Choose where to save the build config.");
            if (string.IsNullOrEmpty(path)) return null;

            var asset = ScriptableObject.CreateInstance<ContentPackageBuildConfig>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            SaveConfig(asset);
            return asset;
        }

        /// <summary>Whether an incremental content update is possible.</summary>
        /// <returns>True when a previous build's state file exists.</returns>
        public static bool CanBuildUpdate() => File.Exists(ContentUpdatePath());

        /// <summary>The Addressables content-state file a content update builds against.</summary>
        /// <returns>The path, or empty when Addressables is not configured.</returns>
        public static string ContentUpdatePath()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            return settings == null
                ? ""
                : UnityEditor.AddressableAssets.Build.ContentUpdateScript
                    .GetContentStateDataPath(false, settings);
        }

        /// <summary>
        /// Runs a full or incremental Addressables build and writes the package manifest beside it.
        /// </summary>
        /// <param name="settings">The content settings the manifest is written from.</param>
        /// <param name="config">The build config naming the output path.</param>
        /// <param name="fullBuild">True for a full rebuild, false for a content update.</param>
        /// <returns>What happened, for the caller to show.</returns>
        public static string Run(
            ContentPackageSettings settings, ContentPackageBuildConfig config, bool fullBuild)
        {
            var addressables = AddressableAssetSettingsDefaultObject.Settings;
            if (addressables == null) return "Addressables is not configured in this project.";
            if (config == null) return "No build config selected.";

            string mismatch = DescribeProfileMismatch(addressables, config);
            if (mismatch != null &&
                !EditorUtility.DisplayDialog("Addressables profile does not match the build config",
                    mismatch + "\n\nFix these in the Addressables Profiles window. Build anyway?",
                    "Build anyway", "Cancel"))
            {
                return "Cancelled: the Addressables profile disagrees with the build config.";
            }

            var options = new AddressablesBuildUtility.BuildOptions
            {
                ProfileName = AddressablesBuildUtility.GetActiveProfileName(),
                CleanBuild = false,
            };

            AddressablesBuildUtility.BuildResult result;
            if (fullBuild)
            {
                result = AddressablesBuildUtility.BuildAllContent(options);
            }
            else
            {
                string statePath = ContentUpdatePath();
                if (!File.Exists(statePath))
                    return $"No previous build state at '{statePath}'. Run a full build first.";

                result = AddressablesBuildUtility.BuildContentUpdate(statePath, options);
            }

            if (!result.Success)
            {
                Debug.LogError($"[ContentPackage] Build failed: {result.ErrorMessage}");
                return $"✕ Build failed: {result.ErrorMessage}";
            }

            if (!string.IsNullOrEmpty(result.BuildPath))
                AddressablesBuildUtility.WritePackageManifest(result.BuildPath, settings, addressables, config);

            return $"✓ Built to {result.BuildPath}";
        }

        /// <summary>
        /// Reports where the active Addressables profile disagrees with the build config.
        /// </summary>
        /// <param name="addressables">The Addressables settings.</param>
        /// <param name="config">The build config.</param>
        /// <returns>The problems, one per line, or null when they agree.</returns>
        public static string DescribeProfileMismatch(
            AddressableAssetSettings addressables, ContentPackageBuildConfig config)
        {
            if (addressables == null || config == null) return null;

            string profileId = addressables.activeProfileId;
            var problems = new List<string>();

            void Compare(string key, string expected)
            {
                string actual = addressables.profileSettings.GetValueByName(profileId, key);
                if (actual == null)
                    problems.Add($"Profile variable '{key}' does not exist.");
                else if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    problems.Add($"'{key}' is '{actual}', build config expects '{expected}'.");
            }

            Compare("RemoteBuildPath", config.localBuildPath);
            Compare("RemoteLoadPath", config.remoteLoadURL);

            if (!addressables.BuildRemoteCatalog)
                problems.Add("Build Remote Catalog is off, so no catalog will be produced to publish.");

            return problems.Count == 0 ? null : string.Join("\n", problems);
        }

        /// <summary>
        /// Reports what each package resolved to in an already-built output directory.
        /// </summary>
        /// <param name="settings">The content settings.</param>
        /// <param name="buildPath">The built output directory.</param>
        /// <returns>One row per defined package.</returns>
        /// <remarks>
        /// Hidden packages are verified too. Visibility affects presentation, not correctness — a
        /// hidden <em>required</em> package is still built, uploaded, and installed, and skipping it
        /// meant the one package a player cannot start without was the one never looked at.
        /// </remarks>
        public static List<VerifyRow> Verify(ContentPackageSettings settings, string buildPath)
        {
            var rows = new List<VerifyRow>();
            if (settings == null) return rows;

            var addressables = AddressableAssetSettingsDefaultObject.Settings;

            foreach (var config in settings.packageConfigs)
            {
                if (string.IsNullOrEmpty(config?.packageId)) continue;

                if (config.addressableLabels == null || config.addressableLabels.Length == 0)
                {
                    rows.Add(new VerifyRow(config.packageId, 0, 0, "no labels configured"));
                    continue;
                }

                if (addressables == null)
                {
                    rows.Add(new VerifyRow(config.packageId, 0, 0, "Addressables not configured"));
                    continue;
                }

                var (count, bytes) = AddressablesBuildUtility.GetPackageBundleInfo(
                    config, addressables, buildPath);

                rows.Add(count > 0
                    ? new VerifyRow(config.packageId, count, bytes, null)
                    : new VerifyRow(config.packageId, 0, 0, "no bundles found"));
            }

            return rows;
        }
    }
}
