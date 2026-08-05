using System.Collections.Generic;
using Molca.Editor.Hub;

namespace Molca.Editor.Automation.Hub
{
    /// <summary>
    /// Contributes the Molca Hub <b>Automation</b> workspace tab (§12) through the same
    /// <see cref="MolcaHubWorkspaceProvider"/> seam consumers use — discovered via <c>TypeCache</c>, no Core
    /// edit or registration call. The tab hosts <see cref="MolcaAutomationView"/>.
    /// </summary>
    internal sealed class MolcaAutomationWorkspaceProvider : MolcaHubWorkspaceProvider
    {
        /// <inheritdoc/>
        public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
        {
            // Infrastructure, after Network (10): both describe how the project itself is wired — Network
            // how it talks to the world, Automation how it is driven and built — rather than configuring a
            // connection to one external product (Integrations) or authoring content (Authoring).
            new MolcaHubWorkspaceItem("automation", "Automation", 20,
                () => new MolcaAutomationView(), icon: "automation",
                group: MolcaHubWorkspaceGroups.Infrastructure),
        };
    }
}
