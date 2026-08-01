using System.Collections.Generic;
using Molca.Editor.Hub;
using Molca.Editor.Hub.Sections;
using UnityEngine.UIElements;

namespace Molca.Editor.Localization
{
    /// <summary>
    /// Contributes Localization as a first-class Molca Hub authoring workspace.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Localization/</c>.
    /// <b>Base class:</b> <see cref="MolcaHubWorkspaceProvider"/>.
    /// <b>Registration:</b> discovered automatically through Unity's <c>TypeCache</c>.
    /// <para>
    /// Localization owns audits, catalog editing, locale transactions, migration previews, pseudo-localized
    /// UI scans, and remote publication. Those workflows need a full, scrollable authoring surface rather
    /// than a nested Settings leaf.
    /// </para>
    /// </remarks>
    internal sealed class LocalizationHubWorkspaceProvider : MolcaHubWorkspaceProvider
    {
        /// <summary>Stable id of the Localization workspace tab.</summary>
        internal const string WorkspaceId = "localization";

        /// <inheritdoc/>
        public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
        {
            new MolcaHubWorkspaceItem(
                WorkspaceId,
                "Localization",
                order: 10,
                createContent: () => new LocalizationHubWorkspaceView(),
                icon: "localization",
                group: MolcaHubWorkspaceGroups.Authoring,
                cacheContent: true),
        };
    }

    /// <summary>
    /// Scrollable host for the shared Localization Hub authoring surface.
    /// </summary>
    /// <remarks>
    /// The content remains the same <see cref="MolcaHubLocalizationSection"/> used by the legacy Settings
    /// route, so audit and authoring behavior cannot diverge while persisted older Hub state is restored.
    /// </remarks>
    internal sealed class LocalizationHubWorkspaceView : ScrollView
    {
        internal LocalizationHubWorkspaceView()
            : base(ScrollViewMode.Vertical)
        {
            name = "localization-workspace";
            style.flexGrow = 1;
            Add(new MolcaHubLocalizationSection(deferInitialScan: true));
        }
    }

    /// <summary>Cross-navigation entry point for opening the Localization workspace.</summary>
    public static class LocalizationHubWorkspace
    {
        /// <summary>Stable Molca Hub workspace id.</summary>
        public static string Id => LocalizationHubWorkspaceProvider.WorkspaceId;

        /// <summary>Opens or focuses Molca Hub on the Localization workspace.</summary>
        public static void Open() =>
            MolcaHubWindow.OpenWorkspace(LocalizationHubWorkspaceProvider.WorkspaceId);
    }
}
