using System.Collections.Generic;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// Registers the Content workspace tab.
    /// </summary>
    /// <remarks>
    /// The authoring surface lives in the Hub rather than in the <c>ContentPackageSettings</c>
    /// inspector because the Hub already holds the project binding and the developer entitlement,
    /// which plan §10.2 makes the authority for any publish. Keeping the authority and the action in
    /// two different windows is what let an author publish from an inspector that had no idea which
    /// project it was pointed at.
    ///
    /// It is also a better fit mechanically: an inspector edits an asset, while publishing a release
    /// is a task with progress, cancellation, and resume. Forcing that into <c>OnInspectorGUI</c> is
    /// what produced the two-pass width-probing workaround the inspector still carries.
    ///
    /// Cached content: a publish survives a tab switch, which is the whole point of
    /// <see cref="ContentWorkspaceSession"/>.
    /// </remarks>
    internal sealed class ContentWorkspaceProvider : MolcaHubWorkspaceProvider
    {
        /// <inheritdoc/>
        public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
        {
            new MolcaHubWorkspaceItem(
                id: "content",
                label: "Content",
                // Last in Authoring (Localization 10, Themes 20, Sequence 30): packaging and delivering
                // content is what you reach for after the content itself has been authored.
                order: 40,
                createContent: () => new ContentWorkspaceView(),
                icon: "content",
                group: MolcaHubWorkspaceGroups.Authoring,
                cacheContent: true),
        };
    }
}
