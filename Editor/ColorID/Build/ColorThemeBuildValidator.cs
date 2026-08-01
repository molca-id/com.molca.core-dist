#if UNITY_EDITOR
using System.Collections.Generic;
using Molca.ColorID;
using Molca.Settings;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>Severity of a colour-theme build finding.</summary>
    public enum ColorThemeBuildSeverity
    {
        /// <summary>Reported, does not fail the build.</summary>
        Warning = 0,

        /// <summary>Fails the build under production policy.</summary>
        Error = 1
    }

    /// <summary>One colour-theme problem found during build validation.</summary>
    public readonly struct ColorThemeBuildFinding
    {
        /// <summary>How serious it is.</summary>
        public ColorThemeBuildSeverity Severity { get; }

        /// <summary>What is wrong and what to do about it.</summary>
        public string Message { get; }

        /// <summary>Creates a finding.</summary>
        /// <param name="severity">How serious it is.</param>
        /// <param name="message">What is wrong and how to fix it.</param>
        public ColorThemeBuildFinding(ColorThemeBuildSeverity severity, string message)
        {
            Severity = severity;
            Message = message;
        }

        /// <inheritdoc/>
        public override string ToString() => $"[{Severity}] {Message}";
    }

    /// <summary>
    /// Validates the colour theme before a build, and fails the build on a shipping-blocking problem.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Build/</c>.
    /// <b>Shape:</b> <see cref="IPreprocessBuildWithReport"/>, discovered by Unity's build pipeline.
    /// <para/>
    /// Hooking the Unity pipeline rather than only Molca's own build entry point is deliberate: a
    /// developer building straight from the Build Profiles window must hit the same gate as CI, or the
    /// gate is advisory. <see cref="Validate"/> is public so Molca's build manager, automation preflight
    /// and the Hub can run the identical checks without going through a build.
    /// <para/>
    /// A <b>legacy-only project is not a failure.</b> During the compatibility window a project with no
    /// theme settings module is a supported configuration, so it produces no findings at all rather than
    /// being nagged into migrating.
    /// </remarks>
    public class ColorThemeBuildValidator : IPreprocessBuildWithReport
    {
        /// <inheritdoc/>
        /// <remarks>
        /// Runs late enough that content generation has settled, but before the player is written.
        /// </remarks>
        public int callbackOrder => 100;

        /// <inheritdoc/>
        public void OnPreprocessBuild(BuildReport report)
        {
            var findings = Validate(out bool hasErrors);

            foreach (var finding in findings)
            {
                if (finding.Severity == ColorThemeBuildSeverity.Error)
                    Debug.LogError($"[ColorTheme] {finding.Message}");
                else
                    Debug.LogWarning($"[ColorTheme] {finding.Message}");
            }

            if (hasErrors)
            {
                throw new BuildFailedException(
                    "Colour theme validation failed. See the errors above. A shipped build must have a "
                    + "valid theme set, every required token resolvable in every variant, and current "
                    + "generated UI Toolkit output.");
            }
        }

        /// <summary>Runs every colour-theme build check.</summary>
        /// <param name="hasErrors">Whether any finding would fail a build.</param>
        /// <returns>Every finding, in reporting order.</returns>
        public static List<ColorThemeBuildFinding> Validate(out bool hasErrors)
        {
            var findings = new List<ColorThemeBuildFinding>();
            hasErrors = false;

            var settings = FindThemeSettings();
            if (settings == null)
            {
                // Legacy-only project: supported during the compatibility window.
                return findings;
            }

            if (settings.ThemeSet == null)
            {
                Add(findings, ref hasErrors, ColorThemeBuildSeverity.Error,
                    $"'{settings.name}' is installed but references no theme set, so no colour token "
                    + "can resolve at runtime. Assign a Color Theme Set or remove the module.");
                return findings;
            }

            var themeSet = settings.ThemeSet;

            var structuralErrors = new List<string>();
            if (!themeSet.Validate(structuralErrors))
            {
                foreach (string error in structuralErrors)
                {
                    Add(findings, ref hasErrors, ColorThemeBuildSeverity.Error,
                        $"theme set '{themeSet.DisplayName}': {error}");
                }
                // Per-variant checks below would repeat the same structural errors once per variant.
                return findings;
            }

            string defaultVariant = settings.DefaultVariantId;
            if (string.IsNullOrEmpty(defaultVariant) || themeSet.GetVariant(defaultVariant) == null)
            {
                Add(findings, ref hasErrors, ColorThemeBuildSeverity.Error,
                    $"the default variant '{defaultVariant}' is not declared by theme set "
                    + $"'{themeSet.DisplayName}', so a fresh install has nothing to activate.");
            }

            var manifest = FindManifest(themeSet.StableSetId);
            bool uiToolkitOutputRequired = manifest != null;

            foreach (string variantId in themeSet.GetVariantIds())
            {
                var outcome = ColorThemeResolver.TryResolve(themeSet, variantId, 0, out var theme,
                    out var diagnostics);

                if (outcome != ColorThemeActivation.Activated)
                {
                    Add(findings, ref hasErrors, ColorThemeBuildSeverity.Error,
                        $"variant '{variantId}' does not resolve ({outcome}): "
                        + string.Join("; ", diagnostics));
                    continue;
                }

                foreach (var contrast in ColorThemeResolver.EvaluateContrast(themeSet, theme))
                {
                    if (contrast.IsIncomplete)
                    {
                        // Incomplete is a request for information, never a build failure — see
                        // ColorContrast for why guessing an under-surface is worse than saying so.
                        Add(findings, ref hasErrors, ColorThemeBuildSeverity.Warning,
                            $"contrast requirement could not be measured: {contrast}");
                        continue;
                    }

                    if (contrast.Passed) continue;

                    // Severity is the author's declared judgement of how much this pair matters.
                    var severity = contrast.Requirement.Severity == ColorContrastSeverity.Error
                        ? ColorThemeBuildSeverity.Error
                        : ColorThemeBuildSeverity.Warning;
                    Add(findings, ref hasErrors, severity, $"contrast failure: {contrast}");
                }

                if (!uiToolkitOutputRequired) continue;

                if (!manifest.IsFresh(theme, ColorThemeUssGeneratorVersion.Current, out string stale))
                {
                    Add(findings, ref hasErrors, ColorThemeBuildSeverity.Error,
                        $"generated UI Toolkit output for variant '{variantId}' is stale — {stale} "
                        + "Regenerate it before building, or delete the manifest if this project does "
                        + "not use runtime UI Toolkit.");
                }
            }

            return findings;
        }

        private static void Add(List<ColorThemeBuildFinding> findings, ref bool hasErrors,
            ColorThemeBuildSeverity severity, string message)
        {
            findings.Add(new ColorThemeBuildFinding(severity, message));
            if (severity == ColorThemeBuildSeverity.Error) hasErrors = true;
        }

        /// <summary>
        /// Finds the project's theme settings module through the settings graph.
        /// </summary>
        /// <remarks>
        /// Reads <c>GlobalSettings.modules</c> directly rather than calling
        /// <c>GlobalSettings.GetModule</c>, because at edit time the module cache is not built and the
        /// runtime accessor would return null on a correctly configured project.
        /// </remarks>
        private static ColorThemeSettings FindThemeSettings()
        {
            var globalSettings = GlobalSettings.main;
            if (globalSettings?.modules == null) return null;

            foreach (var module in globalSettings.modules)
            {
                if (module is ColorThemeSettings themeSettings) return themeSettings;
            }
            return null;
        }

        /// <summary>Finds the generated manifest for a theme set, if the project has one.</summary>
        /// <remarks>
        /// Absence means "this project does not use runtime UI Toolkit theming", which is the documented
        /// way to opt out of the generated-output requirement.
        /// </remarks>
        private static ColorThemeManifest FindManifest(string stableSetId)
        {
            if (string.IsNullOrEmpty(stableSetId)) return null;

            foreach (string guid in AssetDatabase.FindAssets("t:ColorThemeManifest"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var manifest = AssetDatabase.LoadAssetAtPath<ColorThemeManifest>(path);
                if (manifest != null && manifest.ThemeSetStableId == stableSetId) return manifest;
            }
            return null;
        }
    }
}
#endif
