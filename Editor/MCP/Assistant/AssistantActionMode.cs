namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// How the assistant authorizes mutating (<see cref="McpToolKind.Action"/>) tool calls. Read-only
    /// tools are unaffected — they always run. The allowlist still gates which action tools are offered
    /// at all; this mode only decides whether an allowlisted action prompts before running.
    /// </summary>
    public enum AssistantActionMode
    {
        /// <summary>Confirm every mutating action with a modal prompt before it runs (default, safest).</summary>
        Ask = 0,

        /// <summary>Run allowlisted mutating actions immediately, with no per-action prompt.</summary>
        Auto = 1,

        /// <summary>
        /// Approve a multi-step task once, then run its allowlisted, undoable actions without further
        /// per-action prompts under a single whole-task undo bracket (Sprint 48). Irreversible actions still
        /// confirm individually; a failed action re-gates the remainder.
        /// </summary>
        Plan = 2,

        /// <summary>
        /// Run every allowlisted mutating action immediately with no prompt at all — including irreversible
        /// ones. Unlike <see cref="Auto"/> (which still confirms irreversible actions individually), this mode
        /// bypasses every per-action and per-task confirmation. The allowlist is still the only gate. Use with
        /// care: irreversible actions run unprompted and cannot be undone.
        /// </summary>
        AutoAll = 3
    }
}
