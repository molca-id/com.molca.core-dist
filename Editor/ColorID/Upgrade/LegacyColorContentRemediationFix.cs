using System.Linq;
using System.Threading;
using Molca.Editor.Doctor;
using Molca.Editor.Remediation;
using Newtonsoft.Json.Linq;

namespace Molca.ColorID.Editor.Upgrade
{
    /// <summary>
    /// The upgrade fix that replaces v1 <c>ColorID</c> components with <see cref="ColorThemeBinding"/>.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Upgrade/</c>.
    /// <b>Registration:</b> none — <c>MolcaFixRegistry</c> finds it through <c>TypeCache</c>.
    /// <para/>
    /// The one fix in the upgrade domain that has to keep working in a release where the type it migrates
    /// away from no longer exists. Everything it needs comes from
    /// <see cref="LegacyColorContentMigration"/>, which reads serialized data by script GUID.
    /// </remarks>
    internal sealed class LegacyColorContentRemediationFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "upgrade.migrate-colorid-content";

        /// <inheritdoc/>
        public override string Description =>
            "Replaces each v1 ColorID component with a ColorThemeBinding carrying the canonical token its "
            + "legacy pair resolves to, then removes the retired component.";

        /// <inheritdoc/>
        public override string HandledFindingCode => "colorid.legacy-components";

        /// <inheritdoc/>
        public override FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var readiness = ColorThemeUpgradeReadiness.Evaluate();
            if (!readiness.IsReady) return MolcaFixOutcome.NotApplied(readiness.Message);

            var plan = LegacyColorContentMigration.Plan(readiness.ThemeSet);

            if (!plan.IsConclusive)
                return MolcaFixOutcome.NotApplied(
                    $"{plan.UnreadableAssets.Count} asset(s) could not be read, so the plan is a lower "
                    + "bound and applying it could leave content half-migrated.");

            int migratable = plan.Migratable.Count();
            if (migratable == 0)
            {
                int refused = plan.Refused.Count();
                return MolcaFixOutcome.NotApplied(refused > 0
                    ? $"All {refused} remaining v1 ColorID component(s) need a decision: "
                      + string.Join("; ", plan.Refused.Take(3).Select(s => s.Refusal))
                    : "No v1 ColorID component is left to migrate.");
            }

            if (dryRun)
                return new MolcaFixOutcome(true,
                    $"Would replace {migratable} v1 ColorID component(s) with bindings.",
                    "ColorID", nameof(ColorThemeBinding));

            var snapshots = new MolcaFileSnapshotGroup(
                plan.Migratable.Select(site => site.Record.AssetPath),
                Id,
                "Migrate v1 ColorID components");
            if (!snapshots.IsReady)
                return MolcaFixOutcome.NotApplied(
                    "Could not snapshot every affected asset, so migration was not started.");

            var result = LegacyColorContentMigration.Apply(plan);

            if (result.Migrated == 0)
            {
                snapshots.Discard();
                return MolcaFixOutcome.NotApplied(result.Failures.Count > 0
                    ? $"Nothing was migrated: {string.Join("; ", result.Failures.Take(3))}"
                    : "The migration reported nothing written.");
            }

            string message = $"Replaced {result.Migrated} ColorID component(s) with "
                             + $"{result.BindingsWritten} binding(s).";
            if (result.Failures.Count > 0)
                message += $" {result.Failures.Count} failed: "
                           + string.Join("; ", result.Failures.Take(3));

            return new MolcaFixOutcome(
                true, message, "ColorID", nameof(ColorThemeBinding), snapshots.EntryId);
        }
    }
}
