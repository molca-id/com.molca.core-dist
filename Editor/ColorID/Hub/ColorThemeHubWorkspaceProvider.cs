#if UNITY_EDITOR
using System.Collections.Generic;
using Molca.Editor.Hub;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Contributes the Themes workspace to the Molca Hub.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Hub/</c>.
    /// <b>Registration:</b> discovered by <c>TypeCache</c> like any
    /// <see cref="MolcaHubWorkspaceProvider"/>, on the same seam a fork or add-on uses.
    /// <para/>
    /// The tab is always available rather than gated on a theme set existing: a project that has not
    /// installed V2 is exactly the one whose author needs to find the install action, and hiding the tab
    /// until after installation would hide the only place that explains it.
    /// </remarks>
    internal sealed class ColorThemeHubWorkspaceProvider : MolcaHubWorkspaceProvider
    {
        /// <inheritdoc/>
        public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
        {
            new MolcaHubWorkspaceItem("themes", "Themes", 20,
                () => new ColorThemeWorkspaceView(),
                icon: "themes",
                group: MolcaHubWorkspaceGroups.Authoring)
        };
    }
}
#endif
