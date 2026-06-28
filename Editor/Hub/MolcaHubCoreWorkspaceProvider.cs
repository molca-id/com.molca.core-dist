using System.Collections.Generic;
using Molca.Editor.Mcp.Assistant;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Core's own Hub workspace tabs — Doctor, Assistant, Sequence — expressed through the same
    /// <see cref="MolcaHubWorkspaceProvider"/> seam consumers use. Settings is the anchored home tab owned
    /// by <see cref="MolcaHubWindow"/> and is not contributed here.
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
                () => new Molca.Editor.Doctor.MolcaDoctorView()),
            new MolcaHubWorkspaceItem("assistant", "Assistant", 20,
                () => new AssistantChatView()),
            new MolcaHubWorkspaceItem("sequence", "Sequence", 30,
                () => new SequenceVisualizerView()),
        };
    }
}
