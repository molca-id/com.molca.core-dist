using UnityEditor;
using UnityEditor.Build.Reporting;
using Molca.Settings;
using System;

namespace Molca.Editor
{
    /// <summary>
    /// Command-line build methods for CI/CD integration.
    /// Use with: Unity -quit -batchmode -executeMethod Molca.Editor.CommandLineBuild.BuildWithProfile -profile development
    /// </summary>
    /// <remarks>
    /// All entry points honor optional version overrides so CI can inject the version it owns:
    /// <c>-version 1.4.0</c> (or <c>1.4.0.250</c>) and <c>-buildNumber 250</c> (e.g. the CI run
    /// number). Overrides are applied to <see cref="VersionSettings"/> before the build, so the
    /// build version preprocessor picks them up.
    /// </remarks>
    public static class CommandLineBuild
    {
        /// <summary>Build the development profile from the command line.</summary>
        public static void BuildDevelopment() => RunBuild("development");

        /// <summary>Build the staging profile from the command line.</summary>
        public static void BuildStaging() => RunBuild("staging");

        /// <summary>Build the production profile from the command line.</summary>
        public static void BuildProduction() => RunBuild("production");

        /// <summary>
        /// Build the profile named by <c>-profile &lt;name&gt;</c> (defaults to <c>development</c>).
        /// </summary>
        public static void BuildWithProfile()
        {
            var profile = TryGetArg("-profile", out var value) ? value : "development";
            UnityEngine.Debug.Log($"Building with profile: {profile}");
            RunBuild(profile);
        }

        /// <summary>
        /// Runs the build for <paramref name="profileName"/> and exits the editor with
        /// 0 only when the build report says Succeeded. A null report (missing
        /// settings/profile or target-switch failure) and exceptions exit 1 so CI goes red.
        /// </summary>
        private static void RunBuild(string profileName)
        {
            int exitCode;
            try
            {
                ApplyVersionOverrides();

                BuildReport report = BuildManager.Build(profileName);
                exitCode = report != null && report.summary.result == BuildResult.Succeeded ? 0 : 1;
                if (exitCode != 0)
                {
                    UnityEngine.Debug.LogError(report == null
                        ? $"Build '{profileName}' did not run (configuration or target-switch error)."
                        : $"Build '{profileName}' finished with result: {report.summary.result}");
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"Build failed: {e}");
                exitCode = 1;
            }

            // Persist any assets modified during the build (version bump, project settings
            // restore) before killing the editor process.
            AssetDatabase.SaveAssets();
            EditorApplication.Exit(exitCode);
        }

        /// <summary>
        /// Applies <c>-version</c> / <c>-buildNumber</c> command-line overrides to
        /// <see cref="VersionSettings"/> before the build. No-op when neither is supplied.
        /// </summary>
        private static void ApplyVersionOverrides()
        {
            var versionSettings = MolcaEditorSettings.Instance?.VersionSettings;
            if (versionSettings == null)
                return;

            GetCurrentVersion(versionSettings, out int major, out int minor, out int patch, out int build);
            bool changed = false;

            if (TryGetArg("-version", out var versionArg) &&
                TryParseVersion(versionArg, out int vMajor, out int vMinor, out int vPatch, out int? vBuild))
            {
                major = vMajor;
                minor = vMinor;
                patch = vPatch;
                if (vBuild.HasValue)
                    build = vBuild.Value;
                changed = true;
            }

            if (TryGetArg("-buildNumber", out var buildArg) && int.TryParse(buildArg, out int parsedBuild) && parsedBuild >= 1)
            {
                build = parsedBuild;
                changed = true;
            }

            if (!changed)
                return;

            versionSettings.SetVersion(major, minor, patch, build);
            EditorUtility.SetDirty(versionSettings);
            UnityEngine.Debug.Log($"[CommandLineBuild] Version override applied: {major}.{minor}.{patch} (build {build}).");
        }

        private static void GetCurrentVersion(VersionSettings settings, out int major, out int minor, out int patch, out int build)
        {
            major = minor = patch = 0;
            var parts = settings.GetVersionString().Split('.');
            if (parts.Length > 0) int.TryParse(parts[0], out major);
            if (parts.Length > 1) int.TryParse(parts[1], out minor);
            if (parts.Length > 2) int.TryParse(parts[2], out patch);
            if (!int.TryParse(settings.GetBuildNumberString(), out build) || build < 1)
                build = 1;
        }

        /// <summary>Parses "M.m.p" or "M.m.p.b"; the optional 4th component becomes <paramref name="build"/>.</summary>
        private static bool TryParseVersion(string value, out int major, out int minor, out int patch, out int? build)
        {
            major = minor = patch = 0;
            build = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var parts = value.Trim().Split('.');
            if (parts.Length < 3)
                return false;
            if (!int.TryParse(parts[0], out major) || !int.TryParse(parts[1], out minor) || !int.TryParse(parts[2], out patch))
                return false;
            if (major < 0 || minor < 0 || patch < 0)
                return false;
            if (parts.Length >= 4 && int.TryParse(parts[3], out int b) && b >= 1)
                build = b;
            return true;
        }

        /// <summary>Reads the value following <paramref name="name"/> in the process command-line args.</summary>
        private static bool TryGetArg(string name, out string value)
        {
            value = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    value = args[i + 1];
                    return true;
                }
            }
            return false;
        }
    }
}
