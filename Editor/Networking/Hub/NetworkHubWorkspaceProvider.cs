using System.Collections.Generic;
using Molca.Editor.Hub;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// Contributes the Hub's <b>Network</b> workspace through the same
    /// <see cref="MolcaHubWorkspaceProvider"/> seam consumers use for their own tabs.
    /// </summary>
    /// <remarks>
    /// Its own provider rather than an entry in <c>MolcaHubCoreWorkspaceProvider</c>: the networking
    /// subsystem owns this surface, so it registers it — the same way the reference system registers
    /// References.
    /// <para>
    /// A workspace and not a Settings leaf, because it owns a toolbar, a navigation rail, ten views,
    /// selection state, an authoring preview environment, and validation runs. The existing Network
    /// settings leaf stays as a compact runtime status with a link here (plan §7.1).
    /// </para>
    /// </remarks>
    internal sealed class NetworkHubWorkspaceProvider : MolcaHubWorkspaceProvider
    {
        /// <summary>Stable id of the Network workspace tab.</summary>
        internal const string WorkspaceId = NetworkHubNavigationTarget.WorkspaceKey;

        /// <inheritdoc/>
        public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
        {
            new MolcaHubWorkspaceItem(
                WorkspaceId, "Network",
                // Only member of Infrastructure today; 10 leaves room below it for future infrastructure
                // surfaces without renumbering.
                order: 10,
                createContent: () => new NetworkHubView(),
                // Family name only — MolcaEditorIcons.Family prepends the "molca-" prefix and the .png.
                icon: "networking",
                group: MolcaHubWorkspaceGroups.Infrastructure,
                // Cached: the rail selection, the preview environment, the selected entity in each view,
                // and a validation report all survive a trip to another tab.
                cacheContent: true),
        };
    }

    /// <summary>
    /// Cross-navigation into the Network workspace, for surfaces outside the Hub.
    /// </summary>
    /// <remarks>
    /// The settings leaf, Doctor findings, and MCP results all arrive through here. The target is stashed
    /// rather than applied directly, because the workspace view may not exist yet — the Hub builds it when
    /// the tab is activated, and it consumes the pending target on attach.
    /// </remarks>
    public static class NetworkHubWorkspace
    {
        /// <summary>The Hub workspace id of the Network tab.</summary>
        public static string Id => NetworkHubWorkspaceProvider.WorkspaceId;

        /// <summary>
        /// Opens (or focuses) the Hub on the Network workspace.
        /// </summary>
        /// <param name="target">
        /// Where to land. The default lands on whichever view the workspace last showed, which is what a
        /// bare "open Network" should do.
        /// </param>
        public static void Open(NetworkHubNavigationTarget target = default)
        {
            NetworkHubView.PendingTarget = target;
            MolcaHubWindow.OpenWorkspace(NetworkHubWorkspaceProvider.WorkspaceId);
        }

        /// <summary>
        /// Opens the workspace at a target expressed in the serialized deep-link form.
        /// </summary>
        /// <param name="link">
        /// A string like <c>workspace=network&amp;view=services&amp;entity=identity</c>.
        /// </param>
        /// <returns><c>false</c> when the link does not name this workspace; nothing is opened.</returns>
        public static bool OpenLink(string link)
        {
            if (!NetworkHubNavigationTarget.TryParse(link, out var target))
                return false;

            Open(target);
            return true;
        }
    }
}
