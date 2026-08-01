using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Molca.Editor.Hub.Docs;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Coverage guardrail for the reference-docs system: verifies that every Core <c>Runtime/*</c>
    /// top-level system — and, when present, every SDK <c>Runtime/Scripts/*</c> feature area — maps to a
    /// reference guide that actually exists in the docs registry. Flags a system whose guide is missing,
    /// and a system directory with no guide mapping at all (so a newly added system cannot ship
    /// undocumented without this check going yellow).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Editor/Doctor/Checks/</c>. Mirrors the Sprint-86 convention-enforcement checks —
    /// it turns "one guide per system" from a review habit into an enforced rule. Findings are
    /// <see cref="DoctorSeverity.Warning"/>: a fork legitimately may not ship every Core guide, and a
    /// docs gap should never break a build. Runs on the main thread because it reads the
    /// <see cref="MolcaDocsRegistry"/> (Package Manager + <c>TypeCache</c>). The expected-guide maps are
    /// exposed <c>internal</c> for the unit test, which checks them against disk without the registry.
    /// </remarks>
    public class DocsCoverageCheck : IDoctorCheck
    {
        public string Id => "docs-coverage";
        public string Description => "Every Core Runtime system and SDK feature area has a reference guide";

        internal const string CoreRuntimeDir = "Packages/com.molca.core/Runtime";
        internal const string CoreReferenceDir = "Packages/com.molca.core/Documentation~/reference";
        internal const string SdkScriptsDir = "Packages/com.molca.sdk/Runtime/Scripts";

        /// <summary>Core <c>Runtime/</c> subdirectory → the guide id (file stem) that documents it.</summary>
        internal static readonly IReadOnlyDictionary<string, string> CoreRuntimeGuides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Attributes"] = "ATTRIBUTES",
                ["Audio"] = "AUDIO",
                ["ColorID"] = "COLOR_ID",
                ["ContentPackage"] = "CONTENT_PACKAGES",
                // Diagnostics (the vendor-neutral sink facade) is documented alongside telemetry —
                // TELEMETRY.md is the "Telemetry & Diagnostics" guide, not a telemetry-only guide.
                ["Diagnostics"] = "TELEMETRY",
                ["Events"] = "EVENTS",
                ["Licensing"] = "LICENSING",
                ["Localization"] = "LOCALIZATION",
                ["Logging"] = "LOGGING",
                ["Modals"] = "MODALS",
                ["Networking"] = "NETWORKING",
                ["ReferenceSystem"] = "REFERENCE_SYSTEM",
                ["Runtime"] = "RUNTIME_MANAGER",
                ["Settings"] = "SETTINGS",
                ["Telemetry"] = "TELEMETRY",
                ["UI"] = "UI_TOKENS",
                ["Utilities"] = "UTILITIES",
            };

        /// <summary>
        /// Core <c>Runtime/</c> subdirectories intentionally without their own guide (covered elsewhere).
        /// <c>UIToolkit</c> is an editor-authoring pipeline surfaced through the UI/Figma tooling guides;
        /// <c>DevPlayer</c> is the development-only automation bridge documented in the Unity automation
        /// plan (§17), not a user-facing runtime system.
        /// </summary>
        internal static readonly IReadOnlyCollection<string> ExcludedCoreDirs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UIToolkit", "DevPlayer" };

        /// <summary>SDK <c>Runtime/Scripts/</c> feature area → the guide id that documents it.</summary>
        internal static readonly IReadOnlyDictionary<string, string> SdkFeatureGuides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Auth"] = "SDK_AUTH",
                ["Media"] = "SDK_MEDIA",
                ["Modal"] = "SDK_MODALS",
                ["UI"] = "SDK_UI",
                ["Utilities"] = "SDK_UTILITIES",
                ["Home"] = "SDK_APP_FLOW",
                ["Preload"] = "SDK_APP_FLOW",
            };

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            // Stay on the main thread — MolcaDocsRegistry queries the Package Manager and TypeCache.
            await Awaitable.NextFrameAsync(cancellationToken);

            var issues = new List<DoctorIssue>();

            // Phase trace: the registry scan (Package Manager + TypeCache) dominates this check's runtime,
            // so announce it before it blocks rather than leaving the run log silent.
            context.ReportStatus("Loading docs registry…");
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in MolcaDocsRegistry.GetDocs())
                available.Add(doc.Id);

            context.ReportStatus("Auditing Core Runtime systems…");
            AuditRuntimeDirectory(CoreRuntimeDir, CoreRuntimeGuides, ExcludedCoreDirs, "Core Runtime", available, issues);

            // SDK is optional — only audit when the shared SDK package is present in this project.
            if (Directory.Exists(SdkScriptsDir))
            {
                context.ReportStatus("Auditing SDK feature areas…");
                AuditRuntimeDirectory(SdkScriptsDir, SdkFeatureGuides, Array.Empty<string>(), "SDK feature", available, issues);
            }

            return issues;
        }

        private void AuditRuntimeDirectory(
            string root,
            IReadOnlyDictionary<string, string> expected,
            IReadOnlyCollection<string> excluded,
            string label,
            ISet<string> available,
            List<DoctorIssue> issues)
        {
            if (!Directory.Exists(root)) return;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (excluded.Contains(name)) continue;

                var normalized = dir.Replace('\\', '/');

                if (!expected.TryGetValue(name, out var guideId))
                {
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"{label} system '{name}' has no reference-guide mapping — add one to DocsCoverageCheck and write the guide.",
                        normalized));
                    continue;
                }

                if (!available.Contains(guideId))
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"{label} system '{name}' expects reference guide '{guideId}.md', which is not in the docs registry.",
                        normalized));
            }
        }

        /// <summary>
        /// Pure coverage check exposed for testing: the required guide ids that are not present in
        /// <paramref name="available"/> (case-insensitive), in input order and de-duplicated.
        /// </summary>
        internal static IReadOnlyList<string> FindMissing(ISet<string> available, IEnumerable<string> required)
        {
            var missing = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in required ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                if (available == null || !available.Contains(id))
                    missing.Add(id);
            }
            return missing;
        }
    }
}
