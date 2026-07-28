using System;
using System.Collections.Generic;
using System.Text;
using Molca.Editor.Addons;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Molca.Editor.About
{
    /// <summary>One Molca package present in this project, as the About section reports it.</summary>
    /// <remarks>Placement: <c>Packages/com.molca.core/Editor/About/</c>. Plain value type; Editor-only.</remarks>
    internal readonly struct MolcaPackageEntry
    {
        /// <summary>Package name, e.g. <c>com.molca.core</c>.</summary>
        internal string Name { get; }

        /// <summary>Human-facing display name from the package manifest.</summary>
        internal string DisplayName { get; }

        /// <summary>Installed version.</summary>
        internal string Version { get; }

        /// <summary>How the package was installed.</summary>
        internal PackageSource Source { get; }

        /// <summary>Fully-qualified package id (carries the git revision for git installs).</summary>
        internal string PackageId { get; }

        internal MolcaPackageEntry(string name, string displayName, string version, PackageSource source, string packageId)
        {
            Name = name;
            DisplayName = displayName;
            Version = version;
            Source = source;
            PackageId = packageId;
        }
    }

    /// <summary>
    /// Resolves the version facts the Hub's About section reports: the installed Core, every sibling
    /// <c>com.molca.*</c> package, how each was installed, and the environment they run in.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/About/</c>. Static, UI-free, and side-effect free so
    /// the section and the diagnostics dump read the same facts. Every Package Manager call is wrapped:
    /// package metadata is unavailable in a few editor states (early domain reload, a broken manifest), and
    /// an About panel must degrade to "unknown" rather than throw. Editor-only; main thread.
    /// </remarks>
    internal static class FrameworkVersionInfo
    {
        /// <summary>Installed <c>com.molca.core</c> version, or empty when it cannot be resolved.</summary>
        internal static string CoreVersion => AddonDistributionConfig.CoreVersion();

        /// <summary>How <c>com.molca.core</c> was installed.</summary>
        internal static PackageSource CoreSource => CorePackage()?.source ?? PackageSource.Unknown;

        /// <summary>Fully-qualified id of the installed Core, including a git revision when applicable.</summary>
        internal static string CorePackageId => CorePackage()?.packageId ?? string.Empty;

        /// <summary>Running editor version.</summary>
        internal static string UnityVersion => Application.unityVersion;

        /// <summary>Scripting runtime this editor uses (<c>mono</c> or <c>coreclr</c>).</summary>
        internal static string EditorRuntime => AddonDistributionConfig.EditorRuntime();

        /// <summary>The <see cref="PackageInfo"/> for Core, or <c>null</c> when unavailable.</summary>
        internal static PackageInfo CorePackage()
        {
            try { return PackageInfo.FindForAssembly(typeof(FrameworkVersionInfo).Assembly); }
            catch { return null; }
        }

        /// <summary>
        /// Every installed <c>com.molca.*</c> package, Core first then alphabetical.
        /// </summary>
        /// <remarks>
        /// Enumerated rather than hardcoded to Core + SDK: a fork that ships its own Molca packages appears
        /// here without an edit to this file, which is the point of reporting it at all.
        /// </remarks>
        internal static IReadOnlyList<MolcaPackageEntry> MolcaPackages()
        {
            var entries = new List<MolcaPackageEntry>();
            try
            {
                foreach (var package in PackageInfo.GetAllRegisteredPackages())
                {
                    if (package == null || string.IsNullOrEmpty(package.name)) continue;
                    if (!package.name.StartsWith("com.molca.", StringComparison.Ordinal)) continue;
                    entries.Add(new MolcaPackageEntry(package.name, package.displayName, package.version,
                        package.source, package.packageId));
                }
            }
            catch
            {
                // Fall through to the single-package fallback below.
            }

            if (entries.Count == 0)
            {
                var core = CorePackage();
                if (core != null)
                    entries.Add(new MolcaPackageEntry(core.name, core.displayName, core.version, core.source, core.packageId));
            }

            entries.Sort((left, right) =>
            {
                bool leftCore = left.Name == "com.molca.core";
                bool rightCore = right.Name == "com.molca.core";
                if (leftCore != rightCore) return leftCore ? -1 : 1;
                return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });
            return entries;
        }

        /// <summary>Human-facing description of an install source, e.g. <c>"Registry"</c> or <c>"Embedded"</c>.</summary>
        internal static string DescribeSource(PackageSource source) => source switch
        {
            PackageSource.Registry => "Registry",
            PackageSource.Git => "Git",
            PackageSource.Embedded => "Embedded",
            PackageSource.Local => "Local folder",
            PackageSource.LocalTarball => "Local tarball",
            PackageSource.BuiltIn => "Built-in",
            _ => "Unknown",
        };

        /// <summary>
        /// One sentence explaining how an upgrade reaches this project, given how Core is installed. Shown
        /// under the update offer so the affordance is never ambiguous.
        /// </summary>
        internal static string DescribeUpgradePath(FrameworkUpgradePath path) => path switch
        {
            FrameworkUpgradePath.PackageManager =>
                "Core is installed from a registry, so the Package Manager can move this project to the new version.",
            FrameworkUpgradePath.GitPackageManager =>
                "Core is installed from a git URL. The Package Manager can repoint that dependency to the " +
                "release's tag and re-resolve it — git must be available to this editor.",
            FrameworkUpgradePath.Manifest =>
                "Core is installed from a git URL, but this release did not publish a pinned git ref. " +
                "Repoint the dependency in Packages/manifest.json, then let Unity resolve it.",
            FrameworkUpgradePath.Embedded =>
                "Core is embedded in this project, so its files are yours to update — the Package Manager does not own them.",
            _ => "No upgrade instruction was published for this release.",
        };

        /// <summary>
        /// Builds the markdown block the About section copies to the clipboard for bug reports: versions,
        /// install sources, environment, and the current update verdict.
        /// </summary>
        /// <param name="state">The latest update verdict, or <c>null</c> when no check has run.</param>
        /// <returns>A markdown fragment; never <c>null</c>.</returns>
        internal static string DiagnosticsMarkdown(FrameworkUpdateState state)
        {
            var text = new StringBuilder();
            text.AppendLine("### Molca diagnostics");
            text.AppendLine();
            text.AppendLine($"- Unity: `{UnityVersion}` ({EditorRuntime})");
            text.AppendLine($"- Platform: `{Application.platform}`");
            foreach (var package in MolcaPackages())
                text.AppendLine($"- `{package.Name}`: {Describe(package.Version)} · {DescribeSource(package.Source)}");
            text.AppendLine($"- Add-on catalog schema: `{AddonDistributionConfig.CatalogSchemaVersion}`");
            text.AppendLine($"- Update feed schema: `{FrameworkUpdateClient.SchemaVersion}`");

            if (state != null)
            {
                text.AppendLine($"- Update check: {state.Verdict}" +
                                (state.Target != null ? $" → `{state.Target.version}`" : string.Empty) +
                                (string.IsNullOrEmpty(state.Channel) ? string.Empty : $" (channel `{state.Channel}`)"));
                if (state.Eol)
                    text.AppendLine($"- Support: end-of-life; supported from `{Describe(state.MinSupportedVersion)}`");
            }

            return text.ToString();
        }

        private static string Describe(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }
}
