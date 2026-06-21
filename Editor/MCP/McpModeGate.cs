namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Pure mode-gating logic shared by the bridge: given a tool's required <see cref="McpToolMode"/>
    /// and the current play state, returns a clear user-facing error when the editor is in the wrong
    /// state, or null when the tool may run (Sprint 15.7 mode-gating UX). Kept GUI/transport-free so it
    /// is directly unit-testable.
    /// </summary>
    public static class McpModeGate
    {
        /// <summary>
        /// Checks whether a tool may run in the current play state.
        /// </summary>
        /// <param name="mode">The tool's required editor state.</param>
        /// <param name="isPlaying">True if the editor is currently in Play mode.</param>
        /// <returns>A user-facing error message, or null if the tool may run.</returns>
        public static string Check(McpToolMode mode, bool isPlaying)
        {
            switch (mode)
            {
                case McpToolMode.Edit when isPlaying:
                    return "This tool requires Edit mode (the editor is currently in Play mode).";
                case McpToolMode.Play when !isPlaying:
                    return "This tool requires Play mode (the editor is currently in Edit mode).";
                default:
                    return null;
            }
        }
    }
}
