using System.Collections.Generic;
using System.Linq;
using Molca.Editor.UI.Tokens;
using Molca.UI.Tokens;
using UnityEditor;

namespace Molca.Editor.Upgrade
{
    /// <summary>
    /// Finds <see cref="MolcaUiTokenCatalog"/> assets still holding V1 <c>(swatch, colorId)</c> pairs.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Upgrade/</c>.
    /// <para/>
    /// A catalog names the mechanisms it styles through rather than storing appearance, so a token whose
    /// colour is still a legacy pair keeps resolving only while the alias map does. This reports it once
    /// per catalog, since that is the unit anyone fixes.
    /// </remarks>
    public sealed class UiTokenCatalogUpgradeDetector : IMolcaUpgradeDetector
    {
        /// <inheritdoc/>
        public string System => "UI Tokens";

        /// <inheritdoc/>
        public IEnumerable<MolcaUpgradeFinding> Detect()
        {
            var migratable = new List<string>();
            var blocked = new List<string>();

            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(MolcaUiTokenCatalog)}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var catalog = AssetDatabase.LoadAssetAtPath<MolcaUiTokenCatalog>(path);
                if (catalog == null) continue;

                var plan = MolcaUiTokenCatalogMigration.Plan(catalog);
                if (plan == null || plan.Entries.Count == 0) continue;

                if (plan.MigratableCount > 0) migratable.Add($"{path} ({plan.MigratableCount})");

                // An unmigratable entry is a token whose legacy pair the theme set has no alias for. That
                // is a vocabulary decision, not something a migration may guess at.
                if (plan.BlockedCount > 0) blocked.Add($"{path} ({plan.BlockedCount})");
            }

            if (migratable.Count > 0)
            {
                yield return new MolcaUpgradeFinding(
                    "uitokens.legacy-colour-pairs",
                    $"{migratable.Count} UI token catalog(s) still naming legacy colour pairs",
                    "Each token's (swatch, colorId) pair is rewritten as the canonical token it already "
                    + "resolves to through the alias map, so nothing renders differently.",
                    MolcaUpgradeSeverity.Warning,
                    migratable,
                    fixId: "upgrade.migrate-ui-token-catalogs");
            }

            if (blocked.Count > 0)
            {
                yield return new MolcaUpgradeFinding(
                    "uitokens.unmapped-colour-pairs",
                    $"{blocked.Count} UI token catalog(s) naming a pair with no alias",
                    "The installed theme set declares no canonical token for these pairs, so there is "
                    + "nothing to migrate them to and choosing one would be inventing a colour. Add the "
                    + "alias to the vocabulary, or repoint the token.",
                    MolcaUpgradeSeverity.Blocking,
                    blocked);
            }
        }
    }
}
