using System.Collections.Generic;
using System.Linq;
using Molca.ColorID;
using Molca.ColorID.Editor;
using Molca.ColorID.Editor.Upgrade;
using Molca.Editor.Upgrade;
using UnityEngine;

namespace Molca.App.UI.Editor
{
    /// <summary>
    /// Finds serialized <c>ColorIDReference</c> pairs on App components that now hold canonical tokens.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/AppUI/Migration/</c> — it lives in
    /// <c>Molca.App.Editor</c> with the components it speaks for. <c>TypeCache</c> finds a detector in any
    /// assembly, so contributing to the upgrade report costs a system nothing but implementing the
    /// interface where its own knowledge already is.
    /// <para/>
    /// Reports what <see cref="ColorTokenReferenceMigration"/> would do, without doing it. The two halves
    /// of that migration report differently on purpose: a field whose legacy pair has no alias is a
    /// decision for a person, and averaging it into the migratable count would let an incomplete upgrade
    /// look finished.
    /// </remarks>
    public sealed class ColorTokenReferenceUpgradeDetector : IMolcaUpgradeDetector
    {
        /// <inheritdoc/>
        public string System => "Colour Theme";

        /// <inheritdoc/>
        public IEnumerable<MolcaUpgradeFinding> Detect()
        {
            var readiness = ColorThemeUpgradeReadiness.Evaluate();
            bool ownsPlanningTheme = !readiness.IsReady;
            var planningTheme = readiness.ThemeSet ?? ColorThemeVocabulary.Build();
            ColorTokenReferencePlan plan;
            try
            {
                // Inventory no-theme projects against an unsaved map. This discovers the fields without
                // authorizing a migration or choosing palette values for the owner.
                plan = ColorTokenReferenceMigration.Plan(planningTheme);
            }
            finally
            {
                if (ownsPlanningTheme) Object.DestroyImmediate(planningTheme);
            }

            if (!plan.IsConclusive)
            {
                yield return new MolcaUpgradeFinding(
                    "colorid.reference-scan-inconclusive",
                    $"{plan.UnreadableAssets.Count} asset(s) could not be read",
                    "While these cannot be read the scan is a lower bound, so a clean result cannot be "
                    + "trusted. Check file permissions and re-run.",
                    MolcaUpgradeSeverity.Warning,
                    plan.UnreadableAssets.ToList());
            }

            var migratable = plan.Migrated.ToList();
            var refused = plan.Refused.ToList();
            if (!readiness.IsReady && migratable.Count + refused.Count > 0)
            {
                yield return new MolcaUpgradeFinding(
                    "colorid.reference-theme-prerequisite",
                    "Legacy colour references need a reviewed V2 theme before migration",
                    readiness.Message,
                    MolcaUpgradeSeverity.Blocking,
                    readiness.Locations);
            }

            if (migratable.Count > 0)
            {
                yield return new MolcaUpgradeFinding(
                    "colorid.legacy-references",
                    $"{migratable.Count} legacy colour reference(s) on App components",
                    readiness.IsReady
                        ? "Each (swatch, colorId) pair becomes the canonical token it already resolves to "
                          + "through the reviewed alias map."
                        : "These fields are inventoried now, but migration stays disabled until the project "
                          + "has one validated and installed theme; the upgrade will not invent palette values.",
                    MolcaUpgradeSeverity.Blocking,
                    migratable
                        .Select(f => $"{f.ContainingAssetPath} — {f.FieldName} "
                                     + $"({f.LegacyKey.SwatchName}.{f.LegacyKey.ColorId})")
                        .ToList(),
                    fixId: readiness.IsReady ? "upgrade.migrate-colour-references" : null);
            }

            if (refused.Count > 0)
            {
                yield return new MolcaUpgradeFinding(
                    "colorid.unmapped-references",
                    $"{refused.Count} colour reference(s) whose pair has no alias",
                    "The theme set declares no canonical token for these, so migrating would mean "
                    + "inventing a colour. Add the alias to the vocabulary, or repoint the field.",
                    MolcaUpgradeSeverity.Blocking,
                    refused
                        .Select(f => $"{f.ContainingAssetPath} — {f.FieldName} "
                                     + $"({f.LegacyKey.SwatchName}.{f.LegacyKey.ColorId}): {f.Reason}")
                        .ToList());
            }
        }
    }
}
