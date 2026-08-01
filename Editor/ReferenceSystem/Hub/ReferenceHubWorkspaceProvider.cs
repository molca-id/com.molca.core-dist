using System.Collections.Generic;
using Molca.Editor.Hub;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// Contributes the Hub's <b>References</b> workspace through the same
    /// <see cref="MolcaHubWorkspaceProvider"/> seam consumers use for their own tabs.
    /// </summary>
    /// <remarks>
    /// <para>Placement: <c>Packages/com.molca.core/Editor/ReferenceSystem/Hub/</c>. Discovered via
    /// <c>TypeCache</c>, and deliberately its own provider rather than an entry in
    /// <c>MolcaHubCoreWorkspaceProvider</c>: the reference system owns this surface, so it registers it,
    /// exactly as <c>com.molca.sequence</c> registers its own tab.</para>
    ///
    /// <para>A workspace and not a Settings leaf, because it owns a toolbar, long-running index builds,
    /// filters, tables, selection state, repair previews and live Play-Mode state. It opts into content
    /// caching so filters, the selected row and a scan in progress survive a trip to another tab.</para>
    /// </remarks>
    internal sealed class ReferenceHubWorkspaceProvider : MolcaHubWorkspaceProvider
    {
        /// <summary>Stable id of the References workspace tab.</summary>
        internal const string WorkspaceId = "references";

        /// <inheritdoc/>
        public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
        {
            new MolcaHubWorkspaceItem(
                WorkspaceId, "References",
                // Order 20 places it after Doctor (10) within Quality: Doctor is the general entry point to
                // project health, and References is the specialist surface you arrive at from it.
                order: 20,
                createContent: () => new ReferenceHubView(),
                icon: "d_Linked",
                group: MolcaHubWorkspaceGroups.Quality,
                cacheContent: true),
        };
    }

    /// <summary>
    /// Cross-navigation into the References workspace, for surfaces outside the Hub.
    /// </summary>
    /// <remarks>
    /// The property drawer's "Open in References" action goes through here. The target site key is stashed
    /// rather than applied directly, because the workspace view may not exist yet — the Hub builds it when the
    /// tab is activated, and it consumes the pending key on attach.
    /// </remarks>
    public static class ReferenceHubWorkspace
    {
        /// <summary>The Hub workspace id of the References tab.</summary>
        public static string Id => ReferenceHubWorkspaceProvider.WorkspaceId;

        /// <summary>
        /// Opens (or focuses) the Hub on the References workspace.
        /// </summary>
        /// <param name="siteKey">
        /// Optional <see cref="ReferenceSiteRecord.SiteKey"/> to select once the workspace is built. When the
        /// key is not a finding, the workspace lands on the References view showing that site.
        /// </param>
        public static void Open(string siteKey = null)
        {
            ReferenceHubView.PendingSiteKey = siteKey;
            MolcaHubWindow.OpenWorkspace(ReferenceHubWorkspaceProvider.WorkspaceId);
        }
    }
}
