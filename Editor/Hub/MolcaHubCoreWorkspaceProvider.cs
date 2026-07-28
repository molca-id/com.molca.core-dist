using System.Collections.Generic;
using Molca.Editor.Mcp.Assistant;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Core's own Hub workspace tabs — Doctor, Assistant — expressed through the same
    /// <see cref="MolcaHubWorkspaceProvider"/> seam consumers use. Settings is the anchored home tab owned
    /// by <see cref="MolcaHubWindow"/> and is not contributed here. The Sequence tab (order 30) moved to
    /// <c>com.molca.sequence</c>'s own <c>SequenceHubWorkspaceProvider</c>, on this exact same seam.
    /// </summary>
    /// <remarks>
    /// Discovered via <c>TypeCache</c> like any provider; this keeps Core's tabs on the exact path a fork
    /// uses, so the seam is dogfooded. Each item's content factory builds the existing tool view on demand.
    /// </remarks>
    internal sealed class MolcaHubCoreWorkspaceProvider : MolcaHubWorkspaceProvider
    {
        /// <inheritdoc/>
        public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
        {
            new MolcaHubWorkspaceItem("doctor", "Doctor", 10,
                () => new Molca.Editor.Doctor.MolcaDoctorView(), icon: "doctor",
                group: MolcaHubWorkspaceGroups.Quality),
            new MolcaHubWorkspaceItem("assistant", "Assistant", 10,
                () => new AssistantChatView(), icon: "mcp",
                group: MolcaHubWorkspaceGroups.Assistance),
        };
    }
}
