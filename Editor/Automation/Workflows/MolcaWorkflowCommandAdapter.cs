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
        /// <summary>Command category for a code-defined workflow (the built-ins).</summary>
        public const string Category = "workflow";

        /// <summary>
        /// Command category for a saved <see cref="MolcaComposedWorkflow"/>. Distinct from
        /// <see cref="Category"/> so discovery UIs can label it, but any surface that lists "workflows"
        /// must include <b>both</b> — treating only <see cref="Category"/> as a workflow is what made saved
        /// workflows invisible in the Hub.
        /// </summary>
        public const string ComposedCategory = "workflow-composed";

        /// <summary>Whether <paramref name="category"/> is any kind of workflow (code-defined or composed).</summary>
        /// <param name="category">A <see cref="MolcaCommandDefinition.Category"/> value.</param>
        /// <returns>True for both workflow categories.</returns>
        public static bool IsWorkflowCategory(string category)
            => category == Category || category == ComposedCategory;

        /// <summary>Builds the neutral command for a workflow.</summary>
        /// <param name="workflow">The workflow to expose.</param>
        /// <returns>The equivalent <see cref="MolcaCommandDefinition"/>.</returns>
        public static MolcaCommandDefinition ToCommand(MolcaWorkflowDefinition workflow)
            => new MolcaCommandDefinition(
                id: workflow.Id,
                displayName: workflow.DisplayName,
                description: workflow.Description,
                executeAsync: ctx => MolcaWorkflowRunner.RunAsync(workflow, ctx),
                category: Category,
                mode: workflow.Mode,
                kind: workflow.Kind,
                reversibility: workflow.Reversibility,
                resourceClaims: workflow.ResourceClaims,
                supportsCancellation: true,
                requiresConfirmation: workflow.RequiresConfirmation);
    }
}
