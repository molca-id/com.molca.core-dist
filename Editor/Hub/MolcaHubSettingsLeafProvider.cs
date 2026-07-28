using System.Collections.Generic;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Editor-only seam for contributing panels to the Hub's Settings rail. Subclass and return one or more
    /// <see cref="MolcaHubSettingsLeafItem"/>; non-abstract subclasses are discovered automatically via
    /// <c>TypeCache</c> (see <see cref="MolcaHubSettingsLeafRegistry"/>) — no Core edit and no registration call.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Subclasses must have a public parameterless
    /// constructor. <see cref="GetLeaves"/> runs on the main thread while the Hub builds its rail; keep it
    /// cheap and side-effect free, deferring real work to each item's
    /// <see cref="MolcaHubSettingsLeafItem.CreateContent"/>.
    /// <para>
    /// This is the smaller of the Hub's two contribution seams. Contribute a <em>workspace tab</em>
    /// (<see cref="MolcaHubWorkspaceProvider"/>) when your surface is a full-window tool with its own toolbar,
    /// its own long-running work, and its own activity chips. Contribute a <em>settings leaf</em> when it is
    /// one panel of configuration or status.
    /// </para>
    /// </remarks>
    public abstract class MolcaHubSettingsLeafProvider
    {
        /// <summary>Returns the Settings-rail leaves this provider contributes.</summary>
        public abstract IEnumerable<MolcaHubSettingsLeafItem> GetLeaves();
    }
}
