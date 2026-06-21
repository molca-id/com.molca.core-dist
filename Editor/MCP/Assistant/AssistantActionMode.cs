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
        Auto = 1
    }
}
