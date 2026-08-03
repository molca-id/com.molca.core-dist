using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Validation;
using Molca.Editor.Upgrade;
using UnityEditor;

namespace Molca.Editor.Networking.Migration
{
    /// <summary>Projects legacy networking and catalog schema work into the 1.x to 2.x report.</summary>
    public sealed class NetworkUpgradeDetector : IMolcaUpgradeDetector
    {
        /// <summary>The aggregate legacy-network migration finding.</summary>
        public const string LegacyMigrationCode = "network.legacy-migration";

        /// <summary>Legacy artifacts the deterministic migration deliberately cannot translate.</summary>
        public const string LegacySkippedCode = "network.legacy-migration-skipped";

        /// <inheritdoc/>
        public string System => "Network";

        /// <inheritdoc/>
        public IEnumerable<MolcaUpgradeFinding> Detect()
        {
            var plan = LegacyMigrationExecutor.DryRun();
            if (plan.Report.HasWork && plan.HasWork)
            {
                var locations = plan.Report.Items
                    .Select(item => item.Asset != null ? AssetDatabase.GetAssetPath(item.Asset) : null)
                    .Where(path => !string.IsNullOrEmpty(path))
                    .Distinct()
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();

                if (locations.Count == 0) locations.Add("(project network settings)");

                yield return new MolcaUpgradeFinding(
                    LegacyMigrationCode,
                    $"Legacy networking needs {plan.Steps.Count} catalog migration step(s)",
                    $"{plan.Report.Summarize()}. The migration authors routed catalog entries alongside "
                    + "the existing assets; it does not delete legacy assets or decide credential scope.",
                    MolcaUpgradeSeverity.Blocking,
                    locations,
                    fixId: "upgrade.migrate-legacy-networking");
            }

            var skipped = plan.Skipped.Where(item => !item.AlreadyMigrated).ToList();
            if (skipped.Count > 0)
            {
                var skippedLocations = skipped.Select(item =>
                {
                    string path = item.Item?.Asset != null
                        ? AssetDatabase.GetAssetPath(item.Item.Asset)
                        : item.Item?.DisplayName;
                    if (string.IsNullOrEmpty(path)) path = "(legacy network artifact)";
                    return $"{path} — {item.Reason}";
                }).ToList();

                yield return new MolcaUpgradeFinding(
                    LegacySkippedCode,
                    $"{skipped.Count} legacy network artifact(s) need review",
                    "The deterministic catalog migration skipped these artifacts rather than guessing. "
                    + "Resolve each stated reason and run the upgrade report again.",
                    MolcaUpgradeSeverity.Blocking,
                    skippedLocations);
            }

            // Migration authors routed entries alongside legacy assets; it intentionally does not remove
            // a legacy credential declaration. Keep that security decision visible even after the plan is
            // otherwise complete.
            foreach (var finding in LegacyCompatibilityAudit.Audit(plan.Report).Findings
                         .Where(finding => finding.Code == LegacyCompatibilityAudit.CodeFullUrlWithCredential))
            {
                string path = finding.TargetObject != null
                    ? AssetDatabase.GetAssetPath(finding.TargetObject)
                    : "(legacy network artifact)";
                yield return new MolcaUpgradeFinding(
                    finding.Code,
                    finding.Message,
                    string.IsNullOrEmpty(finding.Remedy)
                        ? "Review the legacy request's credential behaviour."
                        : finding.Remedy,
                    MolcaUpgradeSeverity.Blocking,
                    new[] { path });
            }

            var catalog = NetworkCatalogLocator.FindCatalog();
            if (catalog != null && catalog.RequiresSchemaMigration)
            {
                string path = AssetDatabase.GetAssetPath(catalog);
                yield return new MolcaUpgradeFinding(
                    NetworkCatalogValidator.CodeSchemaMigrationRequired,
                    $"Network catalog schema v{catalog.SchemaVersion} needs migration",
                    $"Core 2.0 authors schema v{Molca.Networking.Configuration.NetworkCatalog.CurrentSchemaVersion}. "
                    + "Run the versioned catalog migration before relying on network validation.",
                    MolcaUpgradeSeverity.Blocking,
                    new[] { string.IsNullOrEmpty(path) ? catalog.name : path },
                    fixId: "network.migrate-catalog-schema");
            }
        }
    }
}
