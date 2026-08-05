using System.Collections.Generic;
using Molca.Editor.Hub;

namespace Molca.Editor.Onboarding
{
    /// <summary>
    /// Contributes the Hub's "Onboarding" workspace tab through the same
    /// <see cref="MolcaHubWorkspaceProvider"/> seam consumers use.
    /// </summary>
    /// <remarks>
    /// <para>Sits first in the Quality group, ahead of Doctor and Remediation: it is the surface that says
    /// which of them a new project needs, so reaching it before them is the point.</para>
    /// <para>Content is <b>not</b> cached. The view's whole contract is that it re-derives every row from the
    /// project when it attaches, and a cached instance would restore a snapshot from before whatever the user
    /// went away to do.</para>
    /// <para>Hidden when no item is registered, so a fork that ships none never sees an empty tab.</para>
    /// </remarks>
    internal sealed class OnboardingWorkspaceProvider : MolcaHubWorkspaceProvider
    {
        /// <summary>Stable id of the Onboarding workspace tab.</summary>
        internal const string WorkspaceId = "onboarding";

        /// <inheritdoc/>
        public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
        {
            new MolcaHubWorkspaceItem(
                WorkspaceId, "Onboarding", order: 0,
                createContent: () => new MolcaOnboardingView(),
                isAvailable: () => MolcaOnboardingChecklist.Items.Count > 0,
                // Its own family icon, not the generic "window" mark: that one is the Hub's own window
                // icon, so sharing it made Onboarding indistinguishable from the brand once the strip
                // collapsed to icon-only — which is precisely when the icon is all the reader has.
                icon: "onboarding",
                group: MolcaHubWorkspaceGroups.Quality),
        };
    }
}
