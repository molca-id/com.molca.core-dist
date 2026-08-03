using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Editor.Doctor;
using Molca.Editor.Networking.Authoring;
using Molca.Editor.Networking.Migration;
using Molca.Editor.Networking.Validation;
using Molca.Editor.Remediation;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Networking.Remediation
{
    /// <summary>
    /// Base for network fixes: resolves the domain context once and refuses cleanly when it is absent.
    /// </summary>
    internal abstract class NetworkFixBase : MolcaFixBase
    {
        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var domain = target?.DomainContext as NetworkFixDomainContext;

            // The upgrade report owns a different context type. Only schema migration is shared across
            // those domains, so resolve that catalog-level target without making every network fix accept
            // a foreign context.
            if ((domain == null || domain.Catalog == null) &&
                string.Equals(target?.FindingCode, NetworkCatalogValidator.CodeSchemaMigrationRequired,
                    System.StringComparison.Ordinal))
            {
                var catalog = NetworkCatalogLocator.FindCatalog();
                if (catalog != null)
                {
                    domain = new NetworkFixDomainContext(
                        NetworkCatalogValidator.Validate(catalog).Findings.FirstOrDefault(
                            finding => finding.Code == NetworkCatalogValidator.CodeSchemaMigrationRequired),
                        catalog);
                }
            }

            if (domain?.Catalog == null)
                return MolcaFixOutcome.NotApplied(
                    "No network catalog is available for this finding, so there is nothing to repair.");

            return Apply(domain, dryRun, cancellationToken);
        }

        /// <summary>Applies the fix against a resolved catalog.</summary>
        /// <param name="domain">The finding and its catalog.</param>
        /// <param name="dryRun">When true, report what would change without writing.</param>
        /// <param name="cancellationToken">Cancellation for long operations.</param>
        /// <returns>The outcome.</returns>
        protected abstract MolcaFixOutcome Apply(
            NetworkFixDomainContext domain, bool dryRun, CancellationToken cancellationToken);
    }

    /// <summary>Authors routed catalog state for legacy networking without rewriting legacy assets.</summary>
    internal sealed class LegacyNetworkUpgradeFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "upgrade.migrate-legacy-networking";

        /// <inheritdoc/>
        public override string Description =>
            "Creates the catalog entities required to route legacy network assets, preserving the originals.";

        /// <inheritdoc/>
        public override string HandledFindingCode => NetworkUpgradeDetector.LegacyMigrationCode;

        /// <inheritdoc/>
        public override FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var plan = LegacyMigrationExecutor.DryRun();
            if (plan == null || !plan.Report.HasWork || !plan.HasWork)
                return MolcaFixOutcome.NotApplied("No legacy networking migration steps remain.");

            if (dryRun)
            {
                return new MolcaFixOutcome(
                    true,
                    $"Would apply {plan.Steps.Count} legacy networking migration step(s).",
                    "legacy assets",
                    "routed catalog entries");
            }

            var catalogBefore = plan.Report.ExistingCatalog;
            bool catalogExisted = catalogBefore != null;
            var assetsBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (catalogBefore != null)
            {
                AddAssetPath(assetsBefore, catalogBefore);
                foreach (var collection in catalogBefore.EndpointCollections ??
                         Array.Empty<Molca.Networking.Configuration.NetworkEndpointCollection>())
                    AddAssetPath(assetsBefore, collection);
            }

            var pathsToSnapshot = new List<string>();
            if (catalogBefore != null)
            {
                pathsToSnapshot.Add(AssetDatabase.GetAssetPath(catalogBefore));
                var legacyCollection = catalogBefore.EndpointCollections?.FirstOrDefault(
                    collection => collection != null &&
                                  collection.CollectionId == LegacyMigrationPlan.LegacyCollectionId);
                if (legacyCollection != null)
                    pathsToSnapshot.Add(AssetDatabase.GetAssetPath(legacyCollection));
            }
            else
            {
                var settings = MolcaProjectSettings.Instance;
                if (settings?.GlobalSettings != null)
                    pathsToSnapshot.Add(AssetDatabase.GetAssetPath(settings.GlobalSettings));
            }

            var snapshots = new MolcaFileSnapshotGroup(
                pathsToSnapshot, Id, "Migrate legacy networking");
            if (!snapshots.IsReady)
                return MolcaFixOutcome.NotApplied(
                    "Could not snapshot every affected network asset, so migration was not started.");

            var result = LegacyMigrationExecutor.Apply(
                plan, () => cancellationToken.IsCancellationRequested);

            if (result.Catalog != null)
            {
                RecordIfCreated(result.Catalog, assetsBefore, snapshots, "network catalog");
                foreach (var collection in result.Catalog.EndpointCollections ??
                         Array.Empty<Molca.Networking.Configuration.NetworkEndpointCollection>())
                    RecordIfCreated(collection, assetsBefore, snapshots, "endpoint collection");
            }

            bool changed = result.Applied.Count > 0 || (!catalogExisted && result.Catalog != null);
            if (!changed)
            {
                snapshots.Discard();
                return MolcaFixOutcome.NotApplied("The migration reported no catalog changes.");
            }

            string message = result.Summarize();
            if (result.Failures.Count > 0)
                message += " " + string.Join("; ", result.Failures);

            return new MolcaFixOutcome(
                true,
                message,
                "legacy assets",
                $"{result.Applied.Count} routed catalog change(s)",
                snapshots.EntryId);
        }

        private static void AddAssetPath(HashSet<string> paths, UnityEngine.Object asset)
        {
            string path = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }

        private void RecordIfCreated(
            UnityEngine.Object asset,
            HashSet<string> assetsBefore,
            MolcaFileSnapshotGroup snapshots,
            string label)
        {
            string path = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            if (string.IsNullOrEmpty(path) || assetsBefore.Contains(path)) return;
            snapshots.RecordCreated(path, Id, $"Legacy migration created {label} '{path}'");
            assetsBefore.Add(path);
        }
    }

    /// <summary>
    /// Upgrades a catalog whose serialized schema predates the framework, via the existing
    /// <see cref="NetworkCatalogSchemaMigrator"/>.
    /// </summary>
    /// <remarks>
    /// Deterministic by construction — the migration is a fixed sequence of versioned steps, not a guess —
    /// but the migrator saves the asset to disk, so it reverts by file snapshot rather than Ctrl+Z and is
    /// therefore <b>not</b> in the safe pass. The design doc classified this fix as Green; declaring the
    /// facet honestly reclassifies it as an opt-in, preview-first repair. That is the facet system working:
    /// "deterministic" and "revertible with one Ctrl+Z" are different properties.
    /// </remarks>
    internal sealed class NetworkSchemaMigrationFix : NetworkFixBase
    {
        /// <inheritdoc/>
        public override string Id => "network.migrate-catalog-schema";

        /// <inheritdoc/>
        public override string Description =>
            "Upgrades the network catalog's serialized schema to the version this framework authors.";

        /// <inheritdoc/>
        public override string HandledFindingCode => NetworkCatalogValidator.CodeSchemaMigrationRequired;

        /// <inheritdoc/>
        public override FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        protected override MolcaFixOutcome Apply(
            NetworkFixDomainContext domain, bool dryRun, CancellationToken cancellationToken)
        {
            var catalog = domain.Catalog;
            var preview = NetworkCatalogSchemaMigrator.Preview(catalog);

            if (preview.IsBlocked)
                return MolcaFixOutcome.NotApplied(
                    $"The migration is blocked: {preview.BlockedReason}");

            if (!preview.ChangesRequired)
                return MolcaFixOutcome.NotApplied("The catalog schema is already current.");

            var before = $"schema v{preview.FromVersion}";
            if (dryRun)
                return new MolcaFixOutcome(
                    true,
                    preview.Notes.Count > 0
                        ? string.Join("; ", preview.Notes)
                        : $"Would migrate {before} to the current schema.",
                    before,
                    "schema current");

            string path = AssetDatabase.GetAssetPath(catalog);
            var snapshots = new MolcaFileSnapshotGroup(
                new[] { path }, Id, $"Network catalog schema migration in '{path}'");
            if (!snapshots.IsReady)
                return MolcaFixOutcome.NotApplied(
                    "Could not snapshot the network catalog, so migration was not started.");

            var report = NetworkCatalogSchemaMigrator.Migrate(catalog);
            if (!report.Applied) snapshots.Discard();
            return report.Applied
                ? new MolcaFixOutcome(
                    true,
                    $"Migrated '{catalog.name}' from schema v{report.FromVersion} to v{report.ToVersion}.",
                    before,
                    $"schema v{report.ToVersion}",
                    snapshots.EntryId)
                : MolcaFixOutcome.NotApplied(
                    report.IsBlocked ? report.BlockedReason : "The migrator reported no change.");
        }
    }

    /// <summary>
    /// Names the catalog's default environment when the choice is not a choice: exactly one environment is
    /// authored.
    /// </summary>
    /// <remarks>
    /// With two or more environments this fix declines — which of them is "default" is a deployment
    /// decision the data does not record, and picking the first would silently point every unqualified call
    /// site at an arbitrary environment (possibly production).
    /// </remarks>
    internal sealed class NetworkDefaultEnvironmentFix : NetworkFixBase
    {
        /// <inheritdoc/>
        public override string Id => "network.set-sole-default-environment";

        /// <inheritdoc/>
        public override string Description =>
            "Sets the catalog's default environment when exactly one environment is authored.";

        /// <inheritdoc/>
        public override string HandledFindingCode => NetworkCatalogValidator.CodeDefaultEnvironmentMissing;

        /// <inheritdoc/>
        protected override MolcaFixOutcome Apply(
            NetworkFixDomainContext domain, bool dryRun, CancellationToken cancellationToken)
        {
            var environments = domain.Catalog.Environments;
            if (environments == null || environments.Count == 0)
                return MolcaFixOutcome.NotApplied(
                    "The catalog authors no environments, so there is nothing to make default. Create one first.");

            if (environments.Count > 1)
                return MolcaFixOutcome.NotApplied(
                    $"{environments.Count} environments are authored "
                    + $"({string.Join(", ", environments.Select(e => e.Id))}); which one is the default is a "
                    + "deployment decision. Choose it in the Network workspace.");

            var only = environments[0].Id;
            if (dryRun)
                return new MolcaFixOutcome(
                    true, $"Would set the default environment to the only one authored, '{only}'.",
                    "(none)", only);

            var result = domain.Editing.SetDefaultEnvironment(only);
            return result.Success
                ? new MolcaFixOutcome(true, result.Message, "(none)", only)
                : MolcaFixOutcome.NotApplied(result.Message);
        }
    }
}
