using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using AddressablesBuildUtility = Molca.Editor.ContentPackage.AddressablesBuildUtility;

namespace Molca.ContentPackage.Editor
{
    /// <summary>
    /// Produces the clean, immutable build a release is cut from.
    ///
    /// Building into a directory that already holds output is the reason releases could contain
    /// objects nobody declared: a bundle from a previous build with a different hash sits there
    /// looking exactly like a current one, gets uploaded, and the server rejects the release for
    /// carrying an undeclared object — or worse, an earlier build's catalog is published against
    /// the current bundles. Staging into a directory this class created and owns removes the
    /// question entirely.
    /// </summary>
    public static class ContentReleaseStaging
    {
        /// <summary>The outcome of a staging build.</summary>
        public sealed class StagingResult
        {
            /// <summary>True when the build succeeded and the directory holds its output.</summary>
            public bool Success;

            /// <summary>Why it failed, or empty.</summary>
            public string Error = string.Empty;

            /// <summary>The directory the build wrote to.</summary>
            public string Directory = string.Empty;

            /// <summary>The build layout report for this build.</summary>
            public string LayoutPath = string.Empty;

            /// <summary>Files produced.</summary>
            public int FileCount;

            /// <summary>Total bytes produced.</summary>
            public long TotalBytes;
        }

        /// <summary>Where staged builds are written. Outside Assets, so Unity never imports them.</summary>
        public static string StagingRoot =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "Molca", "ContentStaging");

        /// <summary>
        /// Builds Addressables content into a freshly created staging directory.
        /// </summary>
        /// <param name="platform">Normalized platform identifier, used to name the directory.</param>
        /// <returns>The staging result. Inspect <see cref="StagingResult.Success"/> before using it.</returns>
        public static StagingResult BuildClean(string platform)
        {
            var result = new StagingResult();

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                result.Error = "No Addressables settings asset. Create one before building content.";
                return result;
            }

            string directory = Path.Combine(StagingRoot, platform ?? "Unknown");
            result.Directory = directory;

            try
            {
                // Removed and recreated, not merely emptied: a leftover read-only or locked file
                // would otherwise survive and silently join the release.
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                result.Error = $"Could not prepare a clean staging directory at '{directory}': {ex.Message}";
                return result;
            }

            try
            {
                // Through the shared build entry, not AddressableAssetSettings.BuildPlayerContent
                // directly. Calling the engine here meant a release cut was the one content build that
                // raised no build-started/completed events, so anything observing content builds --
                // notifications, and any future observer -- was blind to the build that matters most.
                //
                // The layout report is requested rather than toggled by hand: the graph is built from
                // it, and a release cut without it would fall back to guessing which bundles belong to
                // which package. Requesting it means an author never has to know it exists, and the
                // shared entry restores the project's own setting afterwards.
                var buildResult = AddressablesBuildUtility.BuildAllContent(
                    new AddressablesBuildUtility.BuildOptions
                    {
                        ProfileName = AddressablesBuildUtility.GetActiveProfileName(),
                        CleanBuild = false, // the staging directory above is already clean
                        GenerateBuildLayout = true,
                    });

                if (!buildResult.Success)
                {
                    result.Error = string.IsNullOrEmpty(buildResult.ErrorMessage)
                        ? buildResult.Message
                        : buildResult.ErrorMessage;
                    return result;
                }

                string layout = ContentBuildGraph.DefaultLayoutPath;
                if (!File.Exists(layout))
                {
                    result.Error =
                        "The build completed but wrote no build layout report, so package content cannot be " +
                        "resolved. Check Preferences > Addressables > 'Debug Build Layout'.";
                    return result;
                }
                result.LayoutPath = layout;

                var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
                result.FileCount = files.Count;
                result.TotalBytes = files.Sum(path => new FileInfo(path).Length);

                if (result.FileCount == 0)
                {
                    result.Error =
                        $"The build produced no files in '{directory}'. The Addressables profile's " +
                        "RemoteBuildPath does not point at the staging directory, so the release would be empty.";
                    return result;
                }

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = $"Addressables build failed: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Removes staged output for a platform, or all of it when no platform is given.
        /// Staged builds are reproducible, so this is never destructive to anything published.
        /// </summary>
        /// <param name="platform">The platform to clear, or null for every platform.</param>
        public static void Clear(string platform = null)
        {
            string target = string.IsNullOrEmpty(platform) ? StagingRoot : Path.Combine(StagingRoot, platform);
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ContentStaging] Could not clear '{target}': {ex.Message}");
            }
        }
    }
}
