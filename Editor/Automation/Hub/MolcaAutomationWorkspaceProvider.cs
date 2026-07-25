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
            new MolcaHubWorkspaceItem("automation", "Automation", 40,
                () => new MolcaAutomationView(), icon: "mcp"),
        };
    }
}
