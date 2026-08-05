using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Molca.Settings;
using System;

namespace Molca.Editor
{
    /// <summary>
    /// Command-line build methods for CI/CD integration.
    /// Use with: <c>Unity -batchmode -executeMethod Molca.Editor.CommandLineBuild.BuildWithProfile -profile development</c>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Do not pass <c>-quit</c>.</b> These entry points run the pre-build Doctor gate, which is
    /// asynchronous, and then exit the editor themselves with the right code — exactly like
    /// <c>MolcaDoctor.RunCI</c>. With <c>-quit</c>, Unity would quit the moment the method returned
    /// and the build would never run, so a <c>-quit</c> command line is rejected loudly rather than
    /// exiting 0 with nothing built.
    /// </para>
    /// <para>
    /// All entry points honor optional version overrides so CI can inject the version it owns:
    /// <c>-version 1.4.0</c> (or <c>1.4.0.250</c>) and <c>-buildNumber 250</c> (e.g. the CI run
    /// number). Overrides are applied to <see cref="VersionSettings"/> before the build, so the
    /// build version preprocessor picks them up.
    /// </para>
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
        /// Runs the pre-build gate and the build for <paramref name="profileName"/>, then exits the
        /// editor with 0 only when the build report says Succeeded. A failed gate, a null report
        /// (missing settings/profile or target-switch failure), and exceptions all exit 1 so CI goes red.
        /// </summary>
        /// <param name="profileName">The build profile to build.</param>
        /// <remarks>
        /// <para>
        /// <b>CI builds are gated.</b> This used to call the synchronous, ungated
        /// <see cref="BuildManager.Build(string)"/>, so the build-correctness checks —
        /// <c>build-scenes-valid</c>, <c>version-settings-valid</c>, <c>build-profile-valid</c>,
        /// <c>unresolvable-scene-reference</c>, <c>content-package-valid</c> — ran when a developer
        /// clicked Build in the Hub and never when a release was cut. That is backwards: the build
        /// nobody is watching is the one that most needs checking.
        /// </para>
        /// <para>
        /// <c>async void</c> is permitted here as a CI entry-point shim (the async contract's Unity
        /// entry-point exception); the body is wrapped so no exception escapes into Unity's
        /// synchronization context.
        /// </para>
        /// </remarks>
        private static async void RunBuild(string profileName)
        {
            if (!TryResolveGate(out bool runGate))
                return;

            int exitCode;
            try
            {
                ApplyVersionOverrides();

                BuildReport report = await BuildManager.BuildAsync(profileName, runPreBuildChecks: runGate);
                exitCode = report != null && report.summary.result == BuildResult.Succeeded ? 0 : 1;
                if (exitCode != 0)
                {
                    UnityEngine.Debug.LogError(report == null
                        ? $"Build '{profileName}' did not run (pre-build gate, configuration, or target-switch error)."
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

        /// <summary>The flag that opts a pipeline out of the pre-build gate.</summary>
        internal const string SkipGateArg = "-molcaSkipBuildGate";

        /// <summary>
        /// Decides whether this run gates, and refuses to start a run that cannot do what it was asked.
        /// </summary>
        /// <param name="runGate">True when the pre-build Doctor gate should run.</param>
        /// <returns>False when the run was rejected and the caller must stop.</returns>
        /// <remarks>
        /// <para>
        /// The gate is asynchronous, so the editor must stay alive past <c>-executeMethod</c> returning
        /// — meaning no <c>-quit</c>. Some hosted runners (game-ci's <c>unity-builder</c> among them)
        /// bake <c>-quit</c> into their command line and cannot be talked out of it, so this is not a
        /// case we can simply declare unsupported.
        /// </para>
        /// <para>
        /// <b>What is not negotiable is that the choice is made on purpose.</b> A <c>-quit</c> run must
        /// also pass <c>-molcaSkipBuildGate</c>; then the build proceeds ungated with a warning. Without
        /// it the run is refused. The difference between the two is not technical — it is that one of
        /// them is a line in a CI config that someone wrote, reviewed, and can grep for, and the other
        /// is a hole nobody knows about. Pipelines that take the opt-out should run
        /// <c>MolcaDoctor.RunCI</c> as a separate step before the build; it is the same set of checks.
        /// </para>
        /// </remarks>
        private static bool TryResolveGate(out bool runGate)
        {
            var args = Environment.GetCommandLineArgs();
            bool hasQuit = Array.IndexOf(args, "-quit") >= 0;
            bool skipRequested = Array.IndexOf(args, SkipGateArg) >= 0;

            runGate = !skipRequested;

            if (skipRequested)
            {
                UnityEngine.Debug.LogWarning(
                    $"[CommandLineBuild] '{SkipGateArg}' was passed: building without the pre-build " +
                    "Doctor gate. Run 'MolcaDoctor.RunCI' as a separate step so this pipeline still " +
                    "checks build correctness somewhere.");
                return true;
            }

            if (!hasQuit)
                return true;

            UnityEngine.Debug.LogError(
                "[CommandLineBuild] Refusing to build: the editor was launched with '-quit', which " +
                "terminates Unity as soon as the executed method returns — before the asynchronous " +
                "pre-build gate and the build itself can finish. Either drop '-quit' (this method exits " +
                $"the editor itself with the build's exit code), or pass '{SkipGateArg}' to build " +
                "without the gate deliberately. Exiting 1 rather than exiting 0 having built nothing.");

            if (Application.isBatchMode)
                EditorApplication.Exit(1);

            return false;
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
