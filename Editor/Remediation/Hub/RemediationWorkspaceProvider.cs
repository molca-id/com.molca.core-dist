using System.Collections.Generic;
using Molca.Editor.Hub;

namespace Molca.Editor.Remediation.Hub
{
    /// <summary>
    /// Contributes the Hub's "Remediation" workspace tab — the surface behind "Fix Safe Issues" — through the
    /// same <see cref="MolcaHubWorkspaceProvider"/> seam consumers use.
    /// </summary>
    /// <remarks>
    /// Sits in the Quality group next to Doctor, which is where a user already goes to find out what is
    /// wrong; this is where they act on it. Hidden entirely when no domain is registered, so a fork that
    /// ships none never sees an empty tab.
    /// <para>Content is <b>not</b> cached: the view is cheap to rebuild and its real state lives in the static
    /// <see cref="RemediationHubSession"/>, so a rebuilt view still shows the plan or report the user was
    /// reading.</para>
    /// </remarks>
    internal sealed class RemediationWorkspaceProvider : MolcaHubWorkspaceProvider
    {
        /// <summary>Stable id of the Remediation workspace tab.</summary>
        internal const string WorkspaceId = "remediation";

        /// <inheritdoc/>
        public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
        {
            new MolcaHubWorkspaceItem(
                WorkspaceId, "Remediation", order: 20,
                createContent: () => new RemediationWorkspaceView(),
                isAvailable: () => MolcaRemediationDomains.All.Count > 0,
                icon: "doctor",
                group: MolcaHubWorkspaceGroups.Quality),
        };
    }
}
