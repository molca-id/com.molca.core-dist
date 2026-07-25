namespace Molca.Editor.Automation
{
    /// <summary>
    /// The outcome of an authorization check for one command run: allow, refuse (with a stable code), or
    /// require interactive confirmation before running (§13). Returned by
    /// <see cref="IMolcaAutomationPolicy.Authorize"/> and consumed by the executor.
    /// </summary>
    public readonly struct MolcaAuthorizationDecision
    {
        /// <summary>Whether the command may proceed (possibly after confirmation).</summary>
        public bool Allowed { get; }

        /// <summary>Whether interactive confirmation is required before running.</summary>
        public bool NeedsConfirmation { get; }

        /// <summary>Stable code explaining a refusal (e.g. <c>policy.not_allowlisted</c>), or null.</summary>
        public string Code { get; }

        /// <summary>Human-facing reason, or null.</summary>
        public string Message { get; }

        private MolcaAuthorizationDecision(bool allowed, bool needsConfirmation, string code, string message)
        {
            Allowed = allowed;
            NeedsConfirmation = needsConfirmation;
            Code = code;
            Message = message;
        }

        /// <summary>The command is authorized to run without confirmation.</summary>
        /// <returns>An allow decision.</returns>
        public static MolcaAuthorizationDecision Allow() => new MolcaAuthorizationDecision(true, false, null, null);

        /// <summary>The command is authorized but must be confirmed first.</summary>
        /// <param name="message">Reason confirmation is required.</param>
        /// <returns>A confirmation-required decision.</returns>
        public static MolcaAuthorizationDecision RequireConfirmation(string message) =>
            new MolcaAuthorizationDecision(true, true, "policy.needs_confirmation", message);

        /// <summary>The command is refused by policy.</summary>
        /// <param name="code">Stable refusal code.</param>
        /// <param name="message">Human-facing reason.</param>
        /// <returns>A refusal decision.</returns>
        public static MolcaAuthorizationDecision Refuse(string code, string message) =>
            new MolcaAuthorizationDecision(false, false, code, message);
    }

    /// <summary>
    /// Authorizes command runs against the active policy profile (§9.3, §13). Implemented by
    /// <c>MolcaAutomationPolicy</c> (slice 4) over the union of the legacy MCP allowlist and the new
    /// automation policy asset. The executor calls this before acquiring resources or running anything.
    /// </summary>
    public interface IMolcaAutomationPolicy
    {
        /// <summary>Decides whether <paramref name="command"/> may run in <paramref name="context"/>.</summary>
        /// <param name="command">The command being invoked.</param>
        /// <param name="context">The run context (transport, batch mode, confirmation state).</param>
        /// <returns>The authorization decision.</returns>
        MolcaAuthorizationDecision Authorize(MolcaCommandDefinition command, MolcaCommandContext context);
    }

    /// <summary>
    /// A permissive default policy that allows read-only commands and requires confirmation for
    /// irreversible actions. Used until the real profile-based policy is configured, and by tests that
    /// exercise execution independently of policy.
    /// </summary>
    public sealed class MolcaAllowReadOnlyPolicy : IMolcaAutomationPolicy
    {
        /// <inheritdoc/>
        public MolcaAuthorizationDecision Authorize(MolcaCommandDefinition command, MolcaCommandContext context)
        {
            if (command.Kind == MolcaCommandKind.ReadOnly)
                return MolcaAuthorizationDecision.Allow();
            if (command.RequiresConfirmation && !context.IsConfirmed)
                return MolcaAuthorizationDecision.RequireConfirmation(
                    "This action is irreversible and requires confirmation.");
            return MolcaAuthorizationDecision.Allow();
        }
    }
}
