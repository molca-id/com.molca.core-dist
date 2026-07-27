using System;
using System.Collections.Generic;

namespace Molca.Editor.Automation.BuiltIn
{
    /// <summary>
    /// The exact set of command ids Molca Core ships, used wherever a caller must decide whether a
    /// command's author-controlled free text (a progress message, a description) has been reviewed by
    /// Molca — principally the Remote companion projection, which reports status and progress for any
    /// command but omits <c>message</c> for commands it does not trust (§8.6).
    /// </summary>
    /// <remarks>
    /// Deliberately an explicit id list rather than a <c>molca.</c> prefix test. Namespace ownership is
    /// unique per provider but command-id prefixing is only a convention
    /// (<see cref="MolcaCommandProvider.Namespace"/>), so a third-party provider could claim a
    /// <c>molca.</c>-looking id. Trust must not be inferable from a string an add-on chooses. Adding a
    /// built-in workflow to <see cref="CoreAutomationCommandProvider.GetCommands"/> means adding it here;
    /// <c>CoreAutomationCommandsTests</c> fails if the two drift.
    /// </remarks>
    internal static class CoreShippedCommands
    {
        private static readonly HashSet<string> Ids = new HashSet<string>(StringComparer.Ordinal)
        {
            "molca.preflight",
            "molca.content-verify",
            "molca.runtime-smoke",
            "molca.build",
            "molca.dev-player-smoke",
        };

        /// <summary>Whether <paramref name="commandId"/> is one of Core's own reviewed commands.</summary>
        /// <param name="commandId">The command id to classify; null or unknown is untrusted.</param>
        /// <returns>True when Core ships the command and its authored text is reviewed.</returns>
        internal static bool IsTrusted(string commandId) =>
            !string.IsNullOrEmpty(commandId) && Ids.Contains(commandId);

        /// <summary>The trusted ids, for the drift test.</summary>
        internal static IReadOnlyCollection<string> All => Ids;
    }

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
