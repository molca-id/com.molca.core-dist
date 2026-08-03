using System.Linq;
using System.Threading;
using Molca.ColorID;
using Molca.ColorID.Editor.Upgrade;
using Molca.Editor.Doctor;
using Molca.Editor.Remediation;
using Newtonsoft.Json.Linq;

namespace Molca.App.UI.Editor
{
    /// <summary>
    /// The upgrade fix that rewrites legacy colour pairs on App components as canonical tokens.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/AppUI/Migration/</c>.
    /// <b>Registration:</b> none — <c>MolcaFixRegistry</c> finds it through <c>TypeCache</c>.
    /// <para/>
    /// It lives here rather than with the other upgrade fixes because Core's editor layer does not
    /// reference the App layer above it, and the answer to that is a contract the lower layer owns which
    /// the higher one implements — not the lower one reaching up by qualified name. Reflection was tried
    /// here first and removed: it is the same rule that Core's reflective reach into
    /// <c>QuickSetupInstaller</c> broke, and it fails the same way, silently, the first time a name moves.
    /// <para/>
    /// <see cref="FixReversibility.FileSnapshot"/> because it saves assets, which keeps it out of
    /// <see cref="RemediationPolicy.SafeOnly"/> — correct for anything that rewrites content.
    /// </remarks>
    internal sealed class ColourReferenceRemediationFix : MolcaFixBase
    {
        /// <inheritdoc/>
        public override string Id => "upgrade.migrate-colour-references";

        /// <inheritdoc/>
        public override string Description =>
            "Rewrites serialized (swatch, colorId) pairs on App components as the canonical colour token "
            + "they already resolve to through the alias map.";

        /// <inheritdoc/>
        public override string HandledFindingCode => "colorid.legacy-references";

        /// <inheritdoc/>
        public override FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        public override MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var readiness = ColorThemeUpgradeReadiness.Evaluate();
            if (!readiness.IsReady) return MolcaFixOutcome.NotApplied(readiness.Message);

            var plan = ColorTokenReferenceMigration.Plan(readiness.ThemeSet);

            if (!plan.IsConclusive)
                return MolcaFixOutcome.NotApplied(
                    $"{plan.UnreadableAssets.Count} asset(s) could not be read, so the plan is a lower "
                    + "bound rather than an answer and applying it could leave content half-migrated.");

            int migratable = plan.Migrated.Count();
            if (migratable == 0)
                return MolcaFixOutcome.NotApplied("No legacy colour reference is left to migrate.");

            if (dryRun)
                return new MolcaFixOutcome(true,
                    $"Would rewrite {migratable} colour reference(s) as canonical tokens.",
                    "(swatch, colorId)", "canonical token");

            var snapshots = new MolcaFileSnapshotGroup(
                plan.Migrated.Select(field => field.ContainingAssetPath),
                Id,
                "Migrate legacy colour references");
            if (!snapshots.IsReady)
                return MolcaFixOutcome.NotApplied(
                    "Could not snapshot every affected asset, so migration was not started.");

            var result = ColorTokenReferenceMigration.Apply(plan);
            int written = result.SourceFieldsWritten + result.OverrideFieldsWritten;

            if (written == 0)
            {
                snapshots.Discard();
                return MolcaFixOutcome.NotApplied(result.Failures.Count > 0
                    ? $"Nothing was migrated: {string.Join("; ", result.Failures)}"
                    : "The migration reported nothing written.");
            }

            string message = $"Rewrote {written} colour reference(s) as canonical tokens.";
            if (result.Failures.Count > 0)
                message += $" {result.Failures.Count} failed: " + string.Join("; ", result.Failures);

            return new MolcaFixOutcome(true, message,
                "(swatch, colorId)", "canonical token", snapshots.EntryId);
        }
    }
}
