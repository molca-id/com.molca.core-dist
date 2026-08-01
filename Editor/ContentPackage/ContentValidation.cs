using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Molca.ContentPackage.Editor
{
    /// <summary>How much a validation finding matters.</summary>
    public enum ContentIssueSeverity
    {
        /// <summary>Worth knowing. Publishing is not affected.</summary>
        Info,

        /// <summary>Probably a mistake, but the release is still well-formed.</summary>
        Warning,

        /// <summary>The release is invalid. Publishing must not proceed.</summary>
        Error
    }

    /// <summary>One validation finding.</summary>
    public sealed class ContentIssue
    {
        /// <summary>Stable machine-readable identifier, e.g. <c>package_id_duplicate</c>.</summary>
        public string Code;

        /// <summary>How much it matters.</summary>
        public ContentIssueSeverity Severity;

        /// <summary>The package the finding concerns, or empty when release-wide.</summary>
        public string PackageId = string.Empty;

        /// <summary>Human-readable explanation, including what to do about it.</summary>
        public string Message;

        /// <inheritdoc/>
        public override string ToString() =>
            string.IsNullOrEmpty(PackageId) ? $"[{Severity}] {Code}: {Message}"
                                            : $"[{Severity}] {Code} ({PackageId}): {Message}";
    }

    /// <summary>The outcome of a validation run.</summary>
    public sealed class ContentValidationReport
    {
        /// <summary>Every finding, most severe first.</summary>
        public List<ContentIssue> Issues { get; } = new List<ContentIssue>();

        /// <summary>True when nothing blocks publishing.</summary>
        public bool CanPublish => Issues.All(issue => issue.Severity != ContentIssueSeverity.Error);

        /// <summary>Count of blocking findings.</summary>
        public int ErrorCount => Issues.Count(issue => issue.Severity == ContentIssueSeverity.Error);

        /// <summary>Count of non-blocking findings worth attention.</summary>
        public int WarningCount => Issues.Count(issue => issue.Severity == ContentIssueSeverity.Warning);

        /// <summary>
        /// What was actually checked. A caller that ran without a build graph has not validated
        /// content, and a UI button saying "Validate" must be able to say so rather than implying
        /// a completeness it did not achieve.
        /// </summary>
        public bool IncludedBuildGraph { get; internal set; }

        internal void Add(string code, ContentIssueSeverity severity, string message, string packageId = "")
            => Issues.Add(new ContentIssue
            {
                Code = code, Severity = severity, Message = message, PackageId = packageId ?? string.Empty
            });

        internal void Sort() => Issues.Sort((a, b) =>
        {
            int bySeverity = b.Severity.CompareTo(a.Severity);
            if (bySeverity != 0) return bySeverity;
            int byPackage = string.CompareOrdinal(a.PackageId, b.PackageId);
            return byPackage != 0 ? byPackage : string.CompareOrdinal(a.Code, b.Code);
        });
    }

    /// <summary>
    /// The single validation engine for content authoring.
    ///
    /// One engine exists because several surfaces used to answer the same question differently: the
    /// inspector, the Doctor check, automation, and the MCP tools each had their own idea of what
    /// "valid" meant, so a package could pass where the author was looking and fail where it
    /// mattered. Hidden required packages were the worst case — some surfaces skipped invisible
    /// packages entirely, and a hidden required package that never shipped is an app that cannot
    /// start.
    ///
    /// Checks are split by what they need. <see cref="ValidateSettings"/> needs only the settings
    /// asset and is safe to run continuously; <see cref="Validate"/> additionally consumes a
    /// <see cref="ContentBuildGraph"/> and can therefore say whether a package ships anything.
    /// Visibility never affects correctness, only presentation.
    /// </summary>
    public static class ContentValidation
    {
        private static readonly Regex PackageIdPattern = new Regex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
        private static readonly Regex SemVerPattern =
            new Regex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

        /// <summary>
        /// Validates the package definitions alone. Cheap and build-independent.
        /// </summary>
        /// <param name="configs">The package configurations from settings.</param>
        /// <param name="contentVersion">The content version being authored, or null to skip that check.</param>
        /// <param name="minAppVersion">Optional minimum app version for the release.</param>
        /// <param name="maxAppVersion">Optional maximum app version for the release.</param>
        public static ContentValidationReport ValidateSettings(
            IReadOnlyList<ContentPackageSettings.PackageConfig> configs,
            string contentVersion = null,
            string minAppVersion = null,
            string maxAppVersion = null)
        {
            var report = new ContentValidationReport();
            if (configs == null || configs.Count == 0)
            {
                report.Add("packages_missing", ContentIssueSeverity.Error,
                    "No content packages are configured. A release must contain at least one package.");
                return report;
            }

            CheckIdentity(report, configs);
            CheckLabels(report, configs);
            CheckDependencies(report, configs);
            CheckVersions(report, configs, contentVersion, minAppVersion, maxAppVersion);

            report.Sort();
            return report;
        }

        /// <summary>
        /// Validates definitions against what a build actually produced.
        /// </summary>
        /// <param name="configs">The package configurations from settings.</param>
        /// <param name="graph">The resolved build graph.</param>
        /// <param name="contentVersion">The content version being authored, or null to skip that check.</param>
        /// <param name="minAppVersion">Optional minimum app version for the release.</param>
        /// <param name="maxAppVersion">Optional maximum app version for the release.</param>
        public static ContentValidationReport Validate(
            IReadOnlyList<ContentPackageSettings.PackageConfig> configs,
            ContentBuildGraph graph,
            string contentVersion = null,
            string minAppVersion = null,
            string maxAppVersion = null)
        {
            var report = ValidateSettings(configs, contentVersion, minAppVersion, maxAppVersion);
            if (graph == null) return report;

            report.IncludedBuildGraph = true;
            CheckBuildContent(report, configs, graph);
            report.Sort();
            return report;
        }

        private static void CheckIdentity(
            ContentValidationReport report, IReadOnlyList<ContentPackageSettings.PackageConfig> configs)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var config in configs)
            {
                if (config == null)
                {
                    report.Add("package_null", ContentIssueSeverity.Error,
                        "A null entry is present in the package list. Remove the empty element.");
                    continue;
                }

                string id = config.packageId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id))
                {
                    report.Add("package_id_missing", ContentIssueSeverity.Error,
                        $"A package has no ID (display name '{config.displayName}'). Every package needs a unique ID.");
                    continue;
                }

                if (!PackageIdPattern.IsMatch(id))
                {
                    report.Add("package_id_invalid", ContentIssueSeverity.Error,
                        "Package IDs must be lowercase and may contain only letters, digits, dot, dash, and " +
                        "underscore, up to 64 characters. The server rejects anything else, so this cannot be published.",
                        id);
                }

                if (!seen.Add(id))
                {
                    report.Add("package_id_duplicate", ContentIssueSeverity.Error,
                        "Two packages share this ID. Dependency resolution, install, and cache accounting would " +
                        "each be free to pick a different one, so the whole definition set is rejected.", id);
                }

                if (string.IsNullOrWhiteSpace(config.displayName))
                {
                    report.Add("package_display_name_missing", ContentIssueSeverity.Error,
                        "No display name. This is what players see in the content manager.", id);
                }
            }
        }

        private static void CheckLabels(
            ContentValidationReport report, IReadOnlyList<ContentPackageSettings.PackageConfig> configs)
        {
            var labelOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var config in configs)
            {
                if (config == null || string.IsNullOrWhiteSpace(config.packageId)) continue;
                string id = config.packageId;
                var labels = config.addressableLabels ?? Array.Empty<string>();

                var nonEmpty = labels.Where(label => !string.IsNullOrWhiteSpace(label)).ToArray();
                if (nonEmpty.Length != labels.Length)
                {
                    report.Add("label_empty", ContentIssueSeverity.Warning,
                        "Has one or more empty label entries. They resolve to nothing and hide how much " +
                        "content the package really has.", id);
                }

                if (nonEmpty.Length == 0)
                {
                    // Not an error on its own: a metadata-only package is legitimate. The build
                    // check decides whether that was intended.
                    report.Add("labels_missing", ContentIssueSeverity.Warning,
                        "Declares no Addressables labels, so it ships no content.", id);
                    continue;
                }

                var duplicates = nonEmpty.GroupBy(label => label, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
                if (duplicates.Length > 0)
                {
                    report.Add("label_duplicate", ContentIssueSeverity.Warning,
                        $"Declares {string.Join(", ", duplicates)} more than once. Harmless, but it usually " +
                        "means a label was pasted rather than chosen.", id);
                }

                foreach (var label in nonEmpty.Distinct(StringComparer.Ordinal))
                {
                    if (!labelOwners.TryGetValue(label, out var owners))
                        labelOwners[label] = owners = new List<string>();
                    owners.Add(id);
                }
            }

            foreach (var pair in labelOwners.Where(pair => pair.Value.Count > 1))
            {
                report.Add("label_shared", ContentIssueSeverity.Info,
                    $"Label '{pair.Key}' is claimed by {string.Join(", ", pair.Value)}. Their content overlaps, " +
                    "and the shared bundles are counted in each package's download size.");
            }
        }

        private static void CheckDependencies(
            ContentValidationReport report, IReadOnlyList<ContentPackageSettings.PackageConfig> configs)
        {
            var byId = configs.Where(config => config != null && !string.IsNullOrWhiteSpace(config.packageId))
                .GroupBy(config => config.packageId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (var config in byId.Values)
            {
                string id = config.packageId;
                var dependencies = (config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>())
                    .Select(dependency => dependency?.packageId)
                    .Where(dependencyId => !string.IsNullOrWhiteSpace(dependencyId))
                    .ToArray();

                foreach (var dependencyId in dependencies.Distinct(StringComparer.Ordinal))
                {
                    if (string.Equals(dependencyId, id, StringComparison.Ordinal))
                    {
                        report.Add("dependency_self", ContentIssueSeverity.Error,
                            "Depends on itself.", id);
                        continue;
                    }

                    if (!byId.TryGetValue(dependencyId, out var dependency))
                    {
                        report.Add("dependency_missing", ContentIssueSeverity.Error,
                            $"Depends on '{dependencyId}', which is not defined.", id);
                        continue;
                    }

                    // A required package that depends on an optional one can be uninstalled out
                    // from under the app: the optional package is removable by definition.
                    if (config.isRequired && !dependency.isRequired)
                    {
                        report.Add("required_depends_on_optional", ContentIssueSeverity.Error,
                            $"Is required but depends on optional package '{dependencyId}'. The dependency can " +
                            "be uninstalled, which would break required content. Mark it required too.", id);
                    }
                }

                if (dependencies.Length != dependencies.Distinct(StringComparer.Ordinal).Count())
                {
                    report.Add("dependency_duplicate", ContentIssueSeverity.Warning,
                        "Lists the same dependency more than once.", id);
                }
            }

            foreach (var cycle in FindCycles(byId))
            {
                report.Add("dependency_cycle", ContentIssueSeverity.Error,
                    $"Circular dependency: {string.Join(" -> ", cycle)}. Nothing in the cycle can be installed.",
                    cycle[0]);
            }
        }

        /// <summary>Depth-first cycle detection, reporting each cycle once by its entry point.</summary>
        private static List<List<string>> FindCycles(
            Dictionary<string, ContentPackageSettings.PackageConfig> byId)
        {
            var cycles = new List<List<string>>();
            var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 unvisited, 1 on stack, 2 done
            var stack = new List<string>();
            var reported = new HashSet<string>(StringComparer.Ordinal);

            void Visit(string id)
            {
                state.TryGetValue(id, out int current);
                if (current == 2) return;
                if (current == 1)
                {
                    int start = stack.IndexOf(id);
                    if (start >= 0)
                    {
                        var cycle = stack.Skip(start).Concat(new[] { id }).ToList();
                        // Canonicalise so A->B->A and B->A->B are one finding.
                        string key = string.Join(">", cycle.Take(cycle.Count - 1).OrderBy(x => x, StringComparer.Ordinal));
                        if (reported.Add(key)) cycles.Add(cycle);
                    }
                    return;
                }

                state[id] = 1;
                stack.Add(id);
                if (byId.TryGetValue(id, out var config))
                {
                    foreach (var dependency in config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>())
                    {
                        string dependencyId = dependency?.packageId;
                        if (!string.IsNullOrWhiteSpace(dependencyId) && byId.ContainsKey(dependencyId))
                            Visit(dependencyId);
                    }
                }
                stack.RemoveAt(stack.Count - 1);
                state[id] = 2;
            }

            foreach (var id in byId.Keys.OrderBy(x => x, StringComparer.Ordinal)) Visit(id);
            return cycles;
        }

        private static void CheckVersions(
            ContentValidationReport report,
            IReadOnlyList<ContentPackageSettings.PackageConfig> configs,
            string contentVersion, string minAppVersion, string maxAppVersion)
        {
            foreach (var config in configs)
            {
                if (config == null || string.IsNullOrWhiteSpace(config.packageId)) continue;
                string version = config.metadata?.version;
                if (string.IsNullOrWhiteSpace(version))
                {
                    report.Add("package_version_missing", ContentIssueSeverity.Error,
                        "Has no version. Update detection compares the installed version against the release, " +
                        "so a package without one can never be offered an update.", config.packageId);
                }
                else if (!SemVerPattern.IsMatch(version))
                {
                    report.Add("package_version_invalid", ContentIssueSeverity.Error,
                        $"Version '{version}' is not semantic (major.minor.patch). The server rejects it.",
                        config.packageId);
                }
            }

            if (!string.IsNullOrWhiteSpace(contentVersion) && !SemVerPattern.IsMatch(contentVersion))
            {
                report.Add("content_version_invalid", ContentIssueSeverity.Error,
                    $"Content version '{contentVersion}' is not semantic (major.minor.patch).");
            }

            bool hasMin = !string.IsNullOrWhiteSpace(minAppVersion);
            bool hasMax = !string.IsNullOrWhiteSpace(maxAppVersion);
            if (hasMin && !SemVerPattern.IsMatch(minAppVersion))
                report.Add("min_app_version_invalid", ContentIssueSeverity.Error,
                    $"Minimum app version '{minAppVersion}' is not semantic.");
            if (hasMax && !SemVerPattern.IsMatch(maxAppVersion))
                report.Add("max_app_version_invalid", ContentIssueSeverity.Error,
                    $"Maximum app version '{maxAppVersion}' is not semantic.");

            if (hasMin && hasMax && SemVerPattern.IsMatch(minAppVersion) && SemVerPattern.IsMatch(maxAppVersion)
                && CompareSemVer(minAppVersion, maxAppVersion) > 0)
            {
                report.Add("compatibility_range_inverted", ContentIssueSeverity.Error,
                    $"Minimum app version {minAppVersion} is above the maximum {maxAppVersion}. " +
                    "No app build could ever resolve this release.");
            }
        }

        private static void CheckBuildContent(
            ContentValidationReport report,
            IReadOnlyList<ContentPackageSettings.PackageConfig> configs,
            ContentBuildGraph graph)
        {
            var nodesById = graph.Packages.ToDictionary(node => node.PackageId, node => node, StringComparer.Ordinal);

            foreach (var config in configs)
            {
                if (config == null || string.IsNullOrWhiteSpace(config.packageId)) continue;
                string id = config.packageId;

                if (!nodesById.TryGetValue(id, out var node))
                {
                    report.Add("package_not_in_build", ContentIssueSeverity.Error,
                        "Was not resolved against the build. It will ship nothing.", id);
                    continue;
                }

                bool declaresLabels = (config.addressableLabels ?? Array.Empty<string>())
                    .Any(label => !string.IsNullOrWhiteSpace(label));

                if (declaresLabels && node.ResolvedAssetCount == 0)
                {
                    // The labels exist in settings but nothing in the build carries them: almost
                    // always a renamed label or assets that were never marked Addressable.
                    report.Add("labels_resolve_to_nothing", config.isRequired
                            ? ContentIssueSeverity.Error : ContentIssueSeverity.Warning,
                        $"Labels [{string.Join(", ", node.Labels)}] matched no assets in the build. " +
                        "Check that the labels exist and that the assets are marked Addressable.", id);
                }

                if (config.isRequired && node.AllBundles.Any() == false)
                {
                    // Visibility is irrelevant here. A hidden required package that ships nothing
                    // is exactly as broken as a visible one, and is far easier to miss.
                    report.Add("required_package_empty", ContentIssueSeverity.Error,
                        "Is required but resolves to no bundles, so the app would start without content it " +
                        "declares it needs.", id);
                }

                if (node.DependencyBundles.Count > 0)
                {
                    report.Add("dependency_bundles_included", ContentIssueSeverity.Info,
                        $"Pulls in {node.DependencyBundles.Count} bundle(s) it does not directly label, " +
                        "through asset references. They are part of its download.", id);
                }

                var shared = node.AllBundles.Where(bundle => bundle.IsShared).ToList();
                if (shared.Count > 0)
                {
                    long sharedBytes = shared.Sum(bundle => bundle.FileSize);
                    report.Add("shared_bundles", ContentIssueSeverity.Info,
                        $"Shares {shared.Count} bundle(s) ({sharedBytes:N0} bytes) with other packages. " +
                        "Each package that needs them counts them in its own download size.", id);
                }
            }

            foreach (var orphan in graph.OrphanBundles)
            {
                report.Add("bundle_unreferenced", ContentIssueSeverity.Warning,
                    $"Bundle '{orphan.Name}' ({orphan.FileSize:N0} bytes, group '{orphan.GroupName}') is not " +
                    "reachable from any package. It would be uploaded and never downloaded, or rejected by the " +
                    "server as an undeclared object.");
            }
        }

        /// <summary>Compares two validated semantic versions by release precedence, ignoring build metadata.</summary>
        private static int CompareSemVer(string left, string right)
        {
            static (int major, int minor, int patch, string pre) Parse(string value)
            {
                string core = value.Split('+')[0];
                string[] split = core.Split(new[] { '-' }, 2);
                string[] numbers = split[0].Split('.');
                return (int.Parse(numbers[0]), int.Parse(numbers[1]), int.Parse(numbers[2]),
                    split.Length > 1 ? split[1] : null);
            }

            var a = Parse(left);
            var b = Parse(right);
            if (a.major != b.major) return a.major.CompareTo(b.major);
            if (a.minor != b.minor) return a.minor.CompareTo(b.minor);
            if (a.patch != b.patch) return a.patch.CompareTo(b.patch);

            // A pre-release precedes its release; two pre-releases compare as text, which is enough
            // for the range check this feeds.
            if (a.pre == null && b.pre == null) return 0;
            if (a.pre == null) return 1;
            if (b.pre == null) return -1;
            return string.CompareOrdinal(a.pre, b.pre);
        }
    }
}
