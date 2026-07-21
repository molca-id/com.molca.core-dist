using System.Collections.Generic;

namespace Molca.Editor.Hub.Docs
{
    /// <summary>
    /// Contributes the Hub's right-anchored "Docs" workspace tab — the read-only reference-docs browser
    /// (<see cref="DocsWorkspaceView"/>) — through the same <see cref="MolcaHubWorkspaceProvider"/> seam
    /// consumers use for their own tabs.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Docs/</c>. Discovered via <c>TypeCache</c> like any
    /// provider. The tab is anchored to the right of the toolbar (set apart from the primary workspaces) and
    /// is omitted entirely when no docs are contributed, so a fork that ships none never sees an empty tab.
    /// </remarks>
    internal sealed class DocsWorkspaceProvider : MolcaHubWorkspaceProvider
    {
        /// <summary>Stable id of the Docs workspace tab.</summary>
        internal const string WorkspaceId = "docs";

        /// <inheritdoc/>
        public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
        {
            new MolcaHubWorkspaceItem(
                WorkspaceId, "Docs", order: 1000,
                createContent: () => new DocsWorkspaceView(),
                isAvailable: () => MolcaDocsRegistry.GetDocs().Count > 0,
                rightAnchored: true)
        };
    }
}
