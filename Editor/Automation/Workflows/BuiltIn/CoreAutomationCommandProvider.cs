using System.Collections.Generic;

namespace Molca.Editor.Automation.BuiltIn
{
    /// <summary>
    /// Contributes Core's own neutral automation commands — the built-in workflows (§11) — to the kernel
    /// registry, discovered via <c>TypeCache</c> alongside the MCP adapter. Each flagship workflow is
    /// projected to a command through <see cref="MolcaWorkflowCommandAdapter"/> so it shares one
    /// policy/mode/audit model; the remaining Consumer Validate workflow (§11.3) runs outside the active
    /// project and is not exposed here.
    /// </summary>
    public sealed class CoreAutomationCommandProvider : MolcaCommandProvider
    {
        /// <inheritdoc/>
        public override string Namespace => "molca";

        /// <inheritdoc/>
        public override string DisplayName => "Molca Core Workflows";

        /// <inheritdoc/>
        public override IEnumerable<MolcaCommandDefinition> GetCommands() => new[]
        {
            MolcaWorkflowCommandAdapter.ToCommand(PreflightWorkflow.Create()),
            MolcaWorkflowCommandAdapter.ToCommand(ContentVerifyWorkflow.Create()),
            MolcaWorkflowCommandAdapter.ToCommand(RuntimeSmokeWorkflow.Create()),
            MolcaWorkflowCommandAdapter.ToCommand(BuildWorkflow.Create()),
            MolcaWorkflowCommandAdapter.ToCommand(DevPlayerSmokeWorkflow.Create()),
        };
    }
}
