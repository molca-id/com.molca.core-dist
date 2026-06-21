using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Molca.ContentPackage;
using Molca.ContentPackage.Core;

namespace Molca.Editor.ContentPackage
{
    /// <summary>
    /// Utility class for building Addressables content for content packages
    /// </summary>
    public static class AddressablesBuildUtility
    {
        /// <summary>
        /// Event raised when an Addressables build starts
        /// </summary>
        public static event System.Action<BuildOptions> OnBuildStarted;
        
        /// <summary>
        /// Event raised when an Addressables build completes
        /// </summary>
        public static event System.Action<BuildResult> OnBuildCompleted;

        /// <summary>
        /// Build result information
        /// </summary>
        public class BuildResult
        {
            public bool Success;
            public string Message;
            public float Duration;
            public long TotalSize;
            public List<string> BuiltGroups = new List<string>();
            // Folder containing Addressables build artifacts (catalog/settings/bundles)
            public string BuildPath;
            // Raw output path returned by Addressables build (often a settings.json/catalog path)
            public string OutputFilePath;
            public string ErrorMessage;
        }

        /// <summary>
        /// Build options for Addressables content
        /// </summary>
        public class BuildOptions
        {
            public string ProfileName = "Default";
            public bool CleanBuild = false;
            public bool BuildPlayerContent = true;
            public List<string> TargetGroups = new List<string>();
        }

        /// <summary>
        /// Builds all Addressables content
        /// </summary>
        public static BuildResult BuildAllContent(BuildOptions options = null)
        {
            options ??= new BuildOptions();
            var startTime = EditorApplication.timeSinceStartup;
            var result = new BuildResult();

            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    result.Success = false;
                    result.Message = "Addressables settings not found. Please configure Addressables first.";
                    return result;
                }

                // Set active profile
                SetActiveProfile(settings, options.ProfileName);

                // Clean build if requested
                if (options.CleanBuild)
                {
                    AddressableAssetSettings.CleanPlayerContent(settings.ActivePlayerDataBuilder);
                }

                // Notify build started
                OnBuildStarted?.Invoke(options);

                // Build content
                Debug.Log($"[AddressablesBuild] Starting content build with profile: {options.ProfileName}");
                
                AddressableAssetSettings.BuildPlayerContent(out var buildResult);

                result.Success        = string.IsNullOrEmpty(buildResult.Error);
                result.Duration       = (float)(EditorApplication.timeSinceStartup - startTime);
                result.OutputFilePath = buildResult.OutputPath;
                result.ErrorMessage   = buildResult.Error;

                // Resolve bundle output folder from RemoteBuildPath profile variable.
                // buildResult.OutputPath always points into Library/com.unity.addressables/…
                // regardless of where groups write their bundles, so it cannot be used here.
                var buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
                result.BuildPath = ResolveBuildPath(settings, buildTarget)
                    ?? (string.IsNullOrEmpty(buildResult.OutputPath) ? null : Path.GetDirectoryName(buildResult.OutputPath));

                // Calculate total size
                if (result.Success && !string.IsNullOrEmpty(result.BuildPath))
                {
                    result.TotalSize = CalculateDirectorySize(result.BuildPath);
                }

                // Get built groups
                foreach (var group in settings.groups)
                {
                    if (group != null && group.HasSchema<BundledAssetGroupSchema>())
                    {
                        result.BuiltGroups.Add(group.Name);
                    }
                }

                result.Message = result.Success 
                    ? $"Build completed successfully!\n\nTime: {result.Duration:F2}s\nSize: {FormatFileSize(result.TotalSize)}\nGroups: {result.BuiltGroups.Count}\nOutput Folder: {result.BuildPath}"
                    : $"Build failed: {buildResult.Error}";

                Debug.Log($"[AddressablesBuild] {result.Message}");
                if (result.Success && !string.IsNullOrEmpty(result.BuildPath))
                {
                    Debug.Log($"[AddressablesBuild] Output folder: {result.BuildPath}");
                    if (!string.IsNullOrEmpty(result.OutputFilePath))
                        Debug.Log($"[AddressablesBuild] Output file: {result.OutputFilePath}");
                }
            }
            catch (System.Exception ex)
            {
                result.Success = false;
                result.Message = $"Build exception: {ex.Message}";
                result.ErrorMessage = ex.Message;
                result.Duration = (float)(EditorApplication.timeSinceStartup - startTime);
                Debug.LogError($"[AddressablesBuild] {result.Message}\n{ex}");
            }
            finally
            {
                // Always notify build completed
                OnBuildCompleted?.Invoke(result);
            }

            return result;
        }

        /// <summary>
        /// Builds only the changed groups since the last full build (content update).
        /// Requires an existing <c>addressables_content_state.bin</c> from a previous full build.
        /// </summary>
        /// <param name="contentStateBinPath">Path to <c>addressables_content_state.bin</c>.</param>
        /// <param name="options">Optional build options. <see cref="BuildOptions.CleanBuild"/> is ignored for content updates.</param>
        public static BuildResult BuildContentUpdate(string contentStateBinPath, BuildOptions options = null)
        {
            options ??= new BuildOptions();
            var startTime = EditorApplication.timeSinceStartup;
            var result    = new BuildResult();

            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    result.Success      = false;
                    result.ErrorMessage = "Addressables settings not found.";
                    result.Message      = result.ErrorMessage;
                    return result;
                }

                if (!System.IO.File.Exists(contentStateBinPath))
                {
                    result.Success      = false;
                    result.ErrorMessage = $"Content state file not found: {contentStateBinPath}";
                    result.Message      = result.ErrorMessage;
                    return result;
                }

                SetActiveProfile(settings, options.ProfileName);
                OnBuildStarted?.Invoke(options);

                Debug.Log($"[AddressablesBuild] Starting content update build from: {contentStateBinPath}");

                ContentUpdateScript.BuildContentUpdate(settings, contentStateBinPath);

                // ContentUpdateScript does not return a result struct; treat completion as success.
                result.Success  = true;
                result.Duration = (float)(EditorApplication.timeSinceStartup - startTime);
                var buildTarget2 = EditorUserBuildSettings.activeBuildTarget.ToString();
                result.BuildPath = ResolveBuildPath(settings, buildTarget2)
                    ?? System.IO.Path.GetDirectoryName(contentStateBinPath);

                // Collect groups
                foreach (var group in settings.groups)
                    if (group != null && group.HasSchema<BundledAssetGroupSchema>())
                        result.BuiltGroups.Add(group.Name);

                if (!string.IsNullOrEmpty(result.BuildPath))
                    result.TotalSize = CalculateDirectorySize(result.BuildPath);

                result.Message = $"Content update build completed successfully!\n\nTime: {result.Duration:F2}s\nSize: {FormatFileSize(result.TotalSize)}";
                Debug.Log($"[AddressablesBuild] {result.Message}");
            }
            catch (System.Exception ex)
            {
                result.Success      = false;
                result.ErrorMessage = ex.Message;
                result.Message      = $"Content update exception: {ex.Message}";
                result.Duration     = (float)(EditorApplication.timeSinceStartup - startTime);
                Debug.LogError($"[AddressablesBuild] {result.Message}\n{ex}");
            }
            finally
            {
                OnBuildCompleted?.Invoke(result);
            }

            return result;
        }

        /// <summary>
        /// Builds specific Addressables groups (for individual packages)
        /// </summary>
        public static BuildResult BuildSpecificGroups(List<string> groupNames, BuildOptions options = null)
        {
            options ??= new BuildOptions();
            var startTime = EditorApplication.timeSinceStartup;
            var result = new BuildResult();

            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    result.Success = false;
                    result.Message = "Addressables settings not found.";
                    return result;
                }

                // Find the groups
                var targetGroups = new List<AddressableAssetGroup>();
                foreach (var groupName in groupNames)
                {
                    var group = settings.FindGroup(groupName);
                    if (group != null)
                    {
                        targetGroups.Add(group);
                        result.BuiltGroups.Add(groupName);
                    }
                    else
                    {
                        Debug.LogWarning($"[AddressablesBuild] Group not found: {groupName}");
                    }
                }

                if (targetGroups.Count == 0)
                {
                    result.Success = false;
                    result.Message = "No valid groups found to build.";
                    return result;
                }

                // Set active profile
                SetActiveProfile(settings, options.ProfileName);

                // Set target groups in options for notification
                options.TargetGroups = result.BuiltGroups;

                // Temporarily disable other groups
                var originalStates = new Dictionary<AddressableAssetGroup, bool>();
                foreach (var group in settings.groups)
                {
                    if (group != null)
                    {
                        originalStates[group] = group.HasSchema<BundledAssetGroupSchema>();
                    }
                }

                // Notify build started
                OnBuildStarted?.Invoke(options);

                // Build
                Debug.Log($"[AddressablesBuild] Building {targetGroups.Count} group(s)");
                AddressableAssetSettings.BuildPlayerContent(out var buildResult);

                // Restore states
                foreach (var kvp in originalStates)
                {
                    // Groups are already built, no need to restore
                }

                result.Success = string.IsNullOrEmpty(buildResult.Error);
                result.Duration = (float)(EditorApplication.timeSinceStartup - startTime);
                result.OutputFilePath = buildResult.OutputPath;
                result.BuildPath = string.IsNullOrEmpty(buildResult.OutputPath) ? null : Path.GetDirectoryName(buildResult.OutputPath);
                result.ErrorMessage = buildResult.Error;

                if (result.Success && !string.IsNullOrEmpty(result.BuildPath))
                {
                    result.TotalSize = CalculateDirectorySize(result.BuildPath);
                }

                result.Message = result.Success
                    ? $"Built {targetGroups.Count} group(s) successfully!\n\nTime: {result.Duration:F2}s\nSize: {FormatFileSize(result.TotalSize)}\nOutput Folder: {result.BuildPath}"
                    : $"Build failed: {buildResult.Error}";

                Debug.Log($"[AddressablesBuild] {result.Message}");
                if (result.Success && !string.IsNullOrEmpty(result.BuildPath))
                {
                    Debug.Log($"[AddressablesBuild] Output folder: {result.BuildPath}");
                    if (!string.IsNullOrEmpty(result.OutputFilePath))
                        Debug.Log($"[AddressablesBuild] Output file: {result.OutputFilePath}");
                }
            }
            catch (System.Exception ex)
            {
                result.Success = false;
                result.Message = $"Build exception: {ex.Message}";
                result.ErrorMessage = ex.Message;
                result.Duration = (float)(EditorApplication.timeSinceStartup - startTime);
                Debug.LogError($"[AddressablesBuild] {result.Message}\n{ex}");
            }
            finally
            {
                // Always notify build completed
                OnBuildCompleted?.Invoke(result);
            }

            return result;
        }

        /// <summary>
        /// Writes a <c>packages.json</c> remote manifest to <paramref name="buildPath"/> after a successful build.
        /// The manifest contains per-package metadata including accurate compressed bundle sizes
        /// derived from the build output, replacing the imprecise editor-scan estimate.
        /// The file is deployed to CDN automatically when the output folder is uploaded.
        /// When <paramref name="buildConfig"/> is provided the derived public URL is also written back
        /// to <see cref="ContentPackageSettings"/> so it never needs to be set manually.
        /// </summary>
        /// <param name="buildPath">The Addressables build output folder (from <see cref="BuildResult.BuildPath"/>).</param>
        /// <param name="packageSettings">The <see cref="ContentPackageSettings"/> asset defining all packages.</param>
        /// <param name="addrSettings">The project's <see cref="AddressableAssetSettings"/>.</param>
        /// <param name="buildConfig">Optional build config used to auto-derive and persist the manifest URL.</param>
        public static void WritePackageManifest(
            string buildPath,
            ContentPackageSettings packageSettings,
            AddressableAssetSettings addrSettings,
            ContentPackageBuildConfig buildConfig = null)
        {
            if (string.IsNullOrEmpty(buildPath) || !Directory.Exists(buildPath))
            {
                Debug.LogWarning($"[AddressablesBuild] WritePackageManifest: build path '{buildPath}' not found");
                return;
            }

            if (packageSettings == null || addrSettings == null)
            {
                Debug.LogWarning("[AddressablesBuild] WritePackageManifest: missing settings — skipping");
                return;
            }

            var buildTarget = buildConfig != null ? new DirectoryInfo(buildPath).Name : "";

            // Resolve catalog URL first so it can be embedded in packages.json.
            // Addressables 1.20+ writes catalog_*.bin; older versions write catalog_*.json.
            var catalogFile = Directory.EnumerateFiles(buildPath, "catalog_*.json")
                .Concat(Directory.EnumerateFiles(buildPath, "catalog_*.bin"))
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();

            string resolvedCatalogUrl = null;
            if (catalogFile != null && buildConfig != null)
                resolvedCatalogUrl = buildConfig.GetCatalogUrl(buildTarget, Path.GetFileName(catalogFile));

            if (catalogFile == null)
                Debug.LogWarning("[AddressablesBuild] No catalog_*.bin/json found in build output — catalogUrl will be absent from packages.json.");

            var manifest = new RemotePackageManifest
            {
                schemaVersion = "1",
                generatedAt   = DateTime.UtcNow.ToString("O"),
                catalogUrl    = resolvedCatalogUrl ?? "",
                packages      = new List<RemotePackageEntry>()
            };

            foreach (var config in packageSettings.packageConfigs)
            {
                if (config == null || string.IsNullOrEmpty(config.packageId)) continue;

                var entry = new RemotePackageEntry
                {
                    packageId       = config.packageId,
                    version         = config.metadata?.version ?? "1.0.0",
                    description     = config.metadata?.description ?? "",
                    author          = config.metadata?.author ?? "",
                    tags            = config.metadata?.tags ?? Array.Empty<string>(),
                    bundleSizeBytes = CalculatePackageBundleSize(config, addrSettings, buildPath),
                    changelog       = ""
                };

                manifest.packages.Add(entry);
                Debug.Log($"[AddressablesBuild] Package '{config.packageId}': {FormatFileSize(entry.bundleSizeBytes)} bundle size");
            }

            var outputPath = Path.Combine(buildPath, "packages.json");
            try
            {
                File.WriteAllText(outputPath, JsonUtility.ToJson(manifest, prettyPrint: true));
                Debug.Log($"[AddressablesBuild] Wrote packages.json → {outputPath} ({manifest.packages.Count} package(s))");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AddressablesBuild] Failed to write packages.json: {ex.Message}");
                return;
            }

            // Auto-populate both remote URLs on ContentPackageSettings from the build output.
            if (buildConfig != null)
            {
                var so = new SerializedObject(packageSettings);
                so.Update();

                var manifestUrl = buildConfig.GetPackagesManifestUrl(buildTarget);
                if (!string.IsNullOrEmpty(manifestUrl))
                {
                    so.FindProperty("_remotePackagesManifestUrl").stringValue = manifestUrl;
                    Debug.Log($"[AddressablesBuild] Auto-set RemotePackagesManifestUrl → {manifestUrl}");
                }

                if (!string.IsNullOrEmpty(resolvedCatalogUrl))
                {
                    so.FindProperty("_remoteCatalogUrl").stringValue = resolvedCatalogUrl;
                    Debug.Log($"[AddressablesBuild] Auto-set RemoteCatalogUrl → {resolvedCatalogUrl}");
                }

                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(packageSettings);
                AssetDatabase.SaveAssetIfDirty(packageSettings);
            }
        }

        /// <summary>
        /// Sums the sizes and counts of all <c>.bundle</c> files in <paramref name="buildPath"/> that
        /// belong to Addressables groups containing at least one entry with a label matching this package.
        /// Returns <c>(count, totalBytes)</c>.
        /// </summary>
        public static (int count, long bytes) GetPackageBundleInfo(
            ContentPackageSettings.PackageConfig config,
            AddressableAssetSettings addrSettings,
            string buildPath)
        {
            long bytes = CalculatePackageBundleSize(config, addrSettings, buildPath);
            if (bytes == 0) return (0, 0);

            // Count matched files for the caller.
            if (config.addressableLabels == null || config.addressableLabels.Length == 0) return (0, 0);
            var packageLabels = new HashSet<string>(
                config.addressableLabels.Where(l => !string.IsNullOrEmpty(l)),
                StringComparer.OrdinalIgnoreCase);
            var matchedGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in addrSettings.groups)
                if (group != null && group.entries.Any(e => e != null && e.labels.Overlaps(packageLabels)))
                    matchedGroupNames.Add(group.Name);

            int count = Directory.EnumerateFiles(buildPath, "*.bundle")
                .Count(f =>
                {
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    return matchedGroupNames.Any(g => name.StartsWith(g.ToLowerInvariant().Replace(" ", "")));
                });
            return (count, bytes);
        }

        /// <summary>
        /// Sums the sizes of all <c>.bundle</c> files in <paramref name="buildPath"/> that belong to
        /// Addressables groups containing at least one entry with a label matching this package.
        /// </summary>
        private static long CalculatePackageBundleSize(
            ContentPackageSettings.PackageConfig config,
            AddressableAssetSettings addrSettings,
            string buildPath)
        {
            if (config.addressableLabels == null || config.addressableLabels.Length == 0)
                return 0;

            var packageLabels = new HashSet<string>(
                config.addressableLabels.Where(l => !string.IsNullOrEmpty(l)),
                StringComparer.OrdinalIgnoreCase);

            if (packageLabels.Count == 0) return 0;

            // Find the names of groups that contain at least one entry matching this package's labels.
            var matchedGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in addrSettings.groups)
            {
                if (group == null) continue;
                if (group.entries.Any(e => e != null && e.labels.Overlaps(packageLabels)))
                    matchedGroupNames.Add(group.Name);
            }

            if (matchedGroupNames.Count == 0) return 0;

            // Addressables names bundle files by lowercasing the group name and removing spaces
            // (not replacing with underscores). E.g. "Test DLC_Exclude" → "testdlc_exclude_assets_all_<hash>.bundle"
            long total = 0;
            foreach (var bundleFile in Directory.EnumerateFiles(buildPath, "*.bundle"))
            {
                var fileName = Path.GetFileName(bundleFile).ToLowerInvariant();
                if (matchedGroupNames.Any(g => fileName.StartsWith(g.ToLowerInvariant().Replace(" ", ""))))
                    total += new FileInfo(bundleFile).Length;
            }

            return total;
        }

        /// <summary>
        /// Gets list of all Addressables groups
        /// </summary>
        public static List<string> GetAllGroupNames()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return new List<string>();

            return settings.groups
                .Where(g => g != null && g.HasSchema<BundledAssetGroupSchema>())
                .Select(g => g.Name)
                .ToList();
        }

        /// <summary>
        /// Gets list of available build profiles
        /// </summary>
        public static List<string> GetProfileNames()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return new List<string> { "Default" };

            return settings.profileSettings.GetAllProfileNames().ToList();
        }

        /// <summary>
        /// Gets the active profile name
        /// </summary>
        public static string GetActiveProfileName()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return "None";

            return settings.profileSettings.GetProfileName(settings.activeProfileId);
        }

        #region Helper Methods

        private static void SetActiveProfile(AddressableAssetSettings settings, string profileName)
        {
            var profileId = settings.profileSettings.GetProfileId(profileName);
            if (!string.IsNullOrEmpty(profileId))
            {
                settings.activeProfileId = profileId;
                Debug.Log($"[AddressablesBuild] Set active profile to: {profileName}");
            }
            else
            {
                Debug.LogWarning($"[AddressablesBuild] Profile '{profileName}' not found, using current profile");
            }
        }

        /// <summary>
        /// Resolves the bundle output folder from the <c>RemoteBuildPath</c> profile variable
        /// for the active profile. Returns <c>null</c> if the variable is not found or empty,
        /// allowing callers to fall back to a secondary strategy.
        /// </summary>
        private static string ResolveBuildPath(AddressableAssetSettings settings, string buildTarget)
        {
            var profileId = settings.activeProfileId;
            var raw = settings.profileSettings.GetValueByName(profileId, "RemoteBuildPath");
            if (string.IsNullOrEmpty(raw)) return null;

            // Evaluate profile variable references (e.g. [BuildTarget]) the same way Addressables does.
            var evaluated = settings.profileSettings.EvaluateString(profileId, raw);
            return string.IsNullOrEmpty(evaluated) ? null : evaluated;
        }

        private static long CalculateDirectorySize(string directory)
        {
            if (!System.IO.Directory.Exists(directory)) return 0;

            long size = 0;
            try
            {
                var files = System.IO.Directory.GetFiles(directory, "*", System.IO.SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var fileInfo = new System.IO.FileInfo(file);
                    size += fileInfo.Length;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AddressablesBuild] Failed to calculate directory size: {ex.Message}");
            }

            return size;
        }

        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }

        #endregion
    }
}