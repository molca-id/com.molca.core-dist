namespace Molca.Editor.Automation
{
    /// <summary>
    /// Projects a <see cref="MolcaWorkflowDefinition"/> into a neutral <see cref="MolcaCommandDefinition"/>
    /// so a workflow is discovered, policy-gated, resource-coordinated, progress-reported, and audited
    /// exactly like any other command (§11). The command's async body runs the workflow through
    /// <see cref="MolcaWorkflowRunner"/>.
    /// </summary>
    public static class MolcaWorkflowCommandAdapter
    {
        /// <summary>Builds the neutral command for a workflow.</summary>
        /// <param name="workflow">The workflow to expose.</param>
        /// <returns>The equivalent <see cref="MolcaCommandDefinition"/>.</returns>
        public static MolcaCommandDefinition ToCommand(MolcaWorkflowDefinition workflow)
            => new MolcaCommandDefinition(
                id: workflow.Id,
                displayName: workflow.DisplayName,
                description: workflow.Description,
                executeAsync: ctx => MolcaWorkflowRunner.RunAsync(workflow, ctx),
                category: "workflow",
                mode: workflow.Mode,
                kind: workflow.Kind,
                reversibility: workflow.Reversibility,
                resourceClaims: workflow.ResourceClaims,
                supportsCancellation: true,
                requiresConfirmation: workflow.RequiresConfirmation);
    }
}
