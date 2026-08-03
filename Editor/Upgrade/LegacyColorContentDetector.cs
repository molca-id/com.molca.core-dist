using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ColorID.Editor.Upgrade;
using Molca.Editor.Migration;

namespace Molca.Editor.Upgrade
{
    /// <summary>
    /// Finds v1 <c>ColorID</c> components still in project content, without needing the type.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Upgrade/</c>.
    /// <para/>
    /// This is the detector that has to keep working after the class it is about has been deleted, which
    /// is exactly why it goes through <see cref="LegacyComponentIndex"/> and the script GUID rather than
    /// <c>GetComponentsInChildren&lt;ColorID&gt;()</c>. The old audit could not: it reached for the type,
    /// so in the release that removes the type it would not compile, and a consumer arriving with v1
    /// content would be told nothing at all.
    /// </remarks>
    public sealed class LegacyColorContentDetector : IMolcaUpgradeDetector
    {
        /// <summary>
        /// The script GUID of the v1 <c>ColorID</c> MonoBehaviour.
        /// </summary>
        /// <remarks>
        /// A literal on purpose. This is the identity of a type that 2.0 deletes, so it cannot be derived
        /// from the type, and content in a consumer's project references it by exactly this value — the
        /// GUID is what makes their prefabs findable after the class is gone. Changing it would orphan
        /// every v1 project.
        /// </remarks>
        public const string ColorIdScriptGuid = "adc580db3c846564384c716c24acbb8f";

        /// <inheritdoc/>
        public string System => "Colour Theme";

        /// <inheritdoc/>
        public IEnumerable<MolcaUpgradeFinding> Detect()
        {
            // Unqualified, and it has to be: this type's own 'System' property shadows the System
            // namespace, so 'System.StringComparison' resolves against the string property and fails.
            var snapshot = LegacyComponentIndex.Scan(ColorIdScriptGuid,
                path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase));

            if (!snapshot.IsConclusive)
            {
                yield return new MolcaUpgradeFinding(
                    "colorid.scan-inconclusive",
                    $"{snapshot.UnreadableAssets.Count} asset(s) could not be read",
                    "The colour scan is a lower bound rather than an answer while these cannot be read, so "
                    + "'nothing left to migrate' cannot be trusted. Check file permissions and re-run.",
                    MolcaUpgradeSeverity.Warning,
                    snapshot.UnreadableAssets.ToList());
            }

            if (snapshot.All.Count == 0) yield break;

            var assets = snapshot.ByAsset.Select(g => $"{g.Key} ({g.Count()})").OrderBy(s => s).ToList();
            var readiness = ColorThemeUpgradeReadiness.Evaluate();

            if (!readiness.IsReady)
            {
                yield return new MolcaUpgradeFinding(
                    "colorid.theme-prerequisite",
                    "Legacy ColorID content needs a reviewed V2 theme before migration",
                    readiness.Message,
                    MolcaUpgradeSeverity.Blocking,
                    readiness.Locations);
            }

            yield return new MolcaUpgradeFinding(
                "colorid.legacy-components",
                $"{snapshot.All.Count} v1 ColorID component(s) in {assets.Count} asset(s)",
                "These render nothing on 2.0 — the component's script no longer exists, so they show as "
                + "missing scripts. Once the project's reviewed theme is ready, migrating rewrites each "
                + "one as a ColorThemeBinding carrying its mapped canonical token, then removes it.",
                MolcaUpgradeSeverity.Blocking,
                assets,
                fixId: readiness.IsReady ? "upgrade.migrate-colorid-content" : null);
        }
    }
}
