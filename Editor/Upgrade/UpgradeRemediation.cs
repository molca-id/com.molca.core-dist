using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Editor.Doctor;
using Molca.Editor.Remediation;
using Molca.Editor.UI.Tokens;
using Molca.UI.Tokens;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Upgrade
{
    /// <summary>The upgrade finding a fix is being asked to resolve.</summary>
    public sealed class UpgradeFixDomainContext
    {
        /// <summary>The finding.</summary>
        public MolcaUpgradeFinding Finding { get; }

        /// <summary>Creates a context.</summary>
        public UpgradeFixDomainContext(MolcaUpgradeFinding finding) => Finding = finding;
    }

    /// <summary>
    /// Puts the 1.x → 2.x readiness report behind the same button as every other repair.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Upgrade/</c>.
    /// <para/>
    /// The migrations these fixes call already existed; each was reachable only from its own menu item,
    /// in an order documented nowhere. Projecting them as a remediation domain is what makes the ordering
    /// stop being the consumer's problem: the pass re-audits to a fixpoint, so a migration that unblocks
    /// another converges without anyone knowing which came first.
    /// <para/>
    /// Every fix here is <see cref="FixReversibility.FileSnapshot"/> — they save assets — so none of them
    /// runs under <see cref="RemediationPolicy.SafeOnly"/>. That is correct rather than unfortunate: an
    /// upgrade rewrites content, and a policy promising Ctrl+Z should not quietly include it.
    /// </remarks>
    public static class UpgradeRemediationBridge
    {
        /// <summary>The domain id.</summary>
        public const string Domain = "upgrade";

        /// <summary>Projects the readiness report as fixable targets.</summary>
        /// <returns>The projection; never <c>null</c>.</returns>
        public static MolcaAuditProjection Project()
        {
            var report = MolcaUpgradeAudit.Run();

            // Fixable findings stay aggregate so a migration executes once. Human-review findings expand
            // into one target per location: the remediation UI can then group by asset while retaining the
            // file:line/property context needed to make each decision.
            var targets = report.Findings
                .SelectMany(finding => finding.IsAutoFixable || finding.Locations.Count == 0
                    ? new[] { Target(finding, finding.Locations.FirstOrDefault() ?? "(project)") }
                    : finding.Locations.Select(location => Target(finding, location)))
                .ToList();

            return new MolcaAuditProjection(
                targets,
                report.IsConclusive
                    ? null
                    : "some systems could not be scanned, so the report is a lower bound",
                isStale: false);
        }

        private static MolcaFixTarget Target(MolcaUpgradeFinding finding, string location)
        {
            string path = location ?? "(project)";
            string property = null;

            int contextSeparator = path.IndexOf(" — ", StringComparison.Ordinal);
            if (contextSeparator >= 0)
            {
                property = path.Substring(contextSeparator + 3);
                path = path.Substring(0, contextSeparator);
            }

            int lineSeparator = path.LastIndexOf(':');
            if (lineSeparator > 0 && int.TryParse(path.Substring(lineSeparator + 1), out int line))
            {
                property = string.IsNullOrEmpty(property)
                    ? $"line {line}"
                    : $"line {line} · {property}";
                path = path.Substring(0, lineSeparator);
            }

            return new MolcaFixTarget(
                finding.Id,
                path,
                $"{finding.Title}. {finding.Detail}",
                property,
                new UpgradeFixDomainContext(finding));
        }

        /// <summary>Builds a remediation request for this domain.</summary>
        /// <param name="policy">How much the pass is allowed to do.</param>
        /// <param name="fixIdFilter">Restricts which fixes may run.</param>
        /// <returns>The request.</returns>
        public static MolcaRemediationRequest Request(
            RemediationPolicy policy = RemediationPolicy.SafeOnly,
            IReadOnlyCollection<string> fixIdFilter = null)
            => new MolcaRemediationRequest(Domain, Project)
            {
                Policy = policy,
                FixIdFilter = fixIdFilter,
                UndoGroupName = "Molca upgrade",
            };
    }

    // The fix for 'colorid.legacy-references' is deliberately not here. It lives in Molca.App.Editor,
    // beside the components and the migration it drives, and reaches this domain through TypeCache the
    // same way its detector does. Core's editor layer does not reference the App layer above it, and the
    // way to bridge that is a contract this layer owns — not a reflective call by qualified name, which
    // is the same rule broken quietly enough to look like a technique.

    /// <summary>Migrates serialized values onto the LocalizedValue schema.</summary>
    internal sealed class MigrateLocalizationValuesFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "upgrade.migrate-localization-values";

        /// <inheritdoc/>
        public override string Description =>
            "Rewrites values still on the pre-LocalizedValue schema, leaving their text and table entries "
            + "unchanged.";

        /// <inheritdoc/>
        public override string HandledFindingCode => "localization.legacy-values";

        /// <inheritdoc/>
        public override FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var plan = LocalizationValueMigrationService.Preview();

            if (plan == null || !plan.IsExecutable)
            {
                // IsExecutable is false both when there is nothing to do and when the plan has errors, so
                // the errors are surfaced rather than reported as "already clean".
                string errors = plan != null && plan.Errors.Count > 0
                    ? " " + string.Join("; ", plan.Errors)
                    : string.Empty;

                return MolcaFixOutcome.NotApplied(
                    $"No executable localization migration is available.{errors}");
            }

            if (dryRun)
                return new MolcaFixOutcome(true,
                    $"Would migrate {plan.Changes.Count} localized value(s).", "legacy", "LocalizedValue");

            var affectedPaths = plan.Candidates
                .Where(candidate => candidate.IsWritable && !candidate.IsBlockedByInstanceOverride)
                .SelectMany(candidate => new[] { candidate.AssetPath }.Concat(
                    candidate.InstanceOverrides
                        .Where(instanceOverride => instanceOverride.CanBeCarried)
                        .Select(instanceOverride => instanceOverride.ContainingAssetPath)));
            var snapshots = new MolcaFileSnapshotGroup(
                affectedPaths, Id, "Migrate localized values");
            if (!snapshots.IsReady)
                return MolcaFixOutcome.NotApplied(
                    "Could not snapshot every affected asset, so migration was not started.");

            var result = LocalizationValueMigrationService.Execute(plan);

            if (result == null || !result.Succeeded)
            {
                snapshots.Discard();
                return MolcaFixOutcome.NotApplied(
                    result?.Error ?? "The localization migration reported no outcome.");
            }

            if (result.ChangedCount == 0)
            {
                snapshots.Discard();
                return MolcaFixOutcome.NotApplied("The migration reported nothing written.");
            }

            return new MolcaFixOutcome(true, $"Migrated {result.ChangedCount} localized value(s).",
                "legacy", "LocalizedValue", snapshots.EntryId);
        }
    }

    /// <summary>Rewrites a UI token catalog's legacy colour pairs as canonical tokens.</summary>
    internal sealed class MigrateUiTokenCatalogsFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "upgrade.migrate-ui-token-catalogs";

        /// <inheritdoc/>
        public override string Description =>
            "Rewrites each catalog token's legacy colour pair as the canonical token it resolves to.";

        /// <inheritdoc/>
        public override string HandledFindingCode => "uitokens.legacy-colour-pairs";

        /// <inheritdoc/>
        public override FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            int migrated = 0;
            int wouldMigrate = 0;
            var plans = new List<MolcaUiTokenCatalogMigrationPlan>();

            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(MolcaUiTokenCatalog)}"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var catalog = AssetDatabase.LoadAssetAtPath<MolcaUiTokenCatalog>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (catalog == null) continue;

                var plan = MolcaUiTokenCatalogMigration.Plan(catalog);
                if (plan == null || plan.MigratableCount == 0) continue;

                if (dryRun) wouldMigrate += plan.MigratableCount;
                else plans.Add(plan);
            }

            if (dryRun)
            {
                return wouldMigrate > 0
                    ? new MolcaFixOutcome(true, $"Would migrate {wouldMigrate} catalog token(s).",
                        "legacy pair", "canonical token")
                    : MolcaFixOutcome.NotApplied("No catalog token is still on a legacy pair.");
            }

            var snapshots = new MolcaFileSnapshotGroup(
                plans.Select(plan => AssetDatabase.GetAssetPath(plan.Catalog)),
                Id,
                "Migrate UI token catalogs");
            if (!snapshots.IsReady)
                return MolcaFixOutcome.NotApplied(
                    "Could not snapshot every affected catalog, so migration was not started.");

            foreach (var plan in plans)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (migrated == 0)
                    {
                        snapshots.Discard();
                        return MolcaFixOutcome.NotApplied("UI token migration was cancelled before writing.");
                    }

                    return new MolcaFixOutcome(
                        true,
                        $"UI token migration was cancelled after {migrated} catalog token(s); "
                        + "the partial run can be reverted or safely resumed.",
                        "legacy pair",
                        "canonical token",
                        snapshots.EntryId);
                }
                migrated += MolcaUiTokenCatalogMigration.Apply(plan);
            }

            if (migrated == 0) snapshots.Discard();
            return migrated > 0
                ? new MolcaFixOutcome(true, $"Migrated {migrated} catalog token(s).",
                    "legacy pair", "canonical token", snapshots.EntryId)
                : MolcaFixOutcome.NotApplied("No catalog token is still on a legacy pair.");
        }
    }

    /// <summary>Registers the upgrade domain.</summary>
    internal sealed class UpgradeRemediationDomainProvider : IMolcaRemediationDomainProvider
    {
        /// <inheritdoc/>
        public IEnumerable<MolcaRemediationDomain> GetDomains() => new[]
        {
            // First: an upgrade rewrites content the other domains then audit, so running it before them
            // means they see the migrated shape rather than reporting on data about to change.
            new MolcaRemediationDomain(
                UpgradeRemediationBridge.Domain, "Upgrade",
                createRequest: policy => UpgradeRemediationBridge.Request(policy),
                order: 5),
        };
    }

}
