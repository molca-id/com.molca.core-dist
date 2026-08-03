using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.Upgrade
{
    /// <summary>
    /// Finds serialized values still on the pre-<c>LocalizedValue</c> schema.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Upgrade/</c>.
    /// <para/>
    /// The migration itself already existed and already knew how to answer this — it just answered only
    /// inside the Localization workspace, where an upgrading consumer has no reason to look. This adapts
    /// the inventory it already produces into the one report they will read.
    /// <para/>
    /// The split between the two findings matters: a candidate blocked by a prefab-instance override is
    /// not a smaller version of a migratable one, it is a different problem with a different owner. The
    /// migration would carry the source's value and quietly discard what the instance author wrote, so it
    /// refuses, and that refusal has to reach a person rather than be averaged into a count.
    /// </remarks>
    public sealed class LocalizationUpgradeDetector : IMolcaUpgradeDetector
    {
        /// <inheritdoc/>
        public string System => "Localization";

        /// <inheritdoc/>
        public IEnumerable<MolcaUpgradeFinding> Detect()
        {
            var inventory = LocalizationValueMigrationService.Inventory();
            if (inventory?.Candidates == null || inventory.Candidates.Count == 0) yield break;

            var blocked = inventory.Candidates.Where(c => c.IsBlockedByInstanceOverride).ToList();
            var migratable = inventory.Candidates.Where(c => !c.IsBlockedByInstanceOverride).ToList();

            if (migratable.Count > 0)
            {
                yield return new MolcaUpgradeFinding(
                    "localization.legacy-values",
                    $"{migratable.Count} legacy localized value(s) awaiting migration",
                    "These still use the pre-LocalizedValue schema. Migrating rewrites them in place; the "
                    + "text and the table entries they point at are unchanged.",
                    MolcaUpgradeSeverity.Warning,
                    migratable.Select(c => $"{c.AssetPath} — {c.PropertyPath}").Distinct().ToList(),
                    fixId: "upgrade.migrate-localization-values");
            }

            if (blocked.Count > 0)
            {
                yield return new MolcaUpgradeFinding(
                    "localization.values-blocked-by-override",
                    $"{blocked.Count} legacy localized value(s) a prefab instance overrides",
                    "Migrating these would carry the source's value and discard what the instance author "
                    + "wrote, so the migration refuses. Reconcile each instance with its prefab — or apply "
                    + "the override — then re-run.",
                    MolcaUpgradeSeverity.Blocking,
                    blocked.Select(c => $"{c.AssetPath} — {c.PropertyPath}").Distinct().ToList());
            }
        }
    }
}
