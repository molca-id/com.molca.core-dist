namespace Molca.Settings.Integration.ClickUp
{
    /// <summary>
    /// Where <see cref="ClickUpIntegrationProvider"/> reports build and release activity.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/ClickUp/</c>.
    /// Serialized on the provider asset, so <see cref="NewTaskInList"/> is deliberately <c>0</c>: an asset
    /// authored before this field existed deserializes to it and keeps its original behavior.
    /// </remarks>
    public enum ClickUpPushTarget
    {
        /// <summary>
        /// Create a new task in the configured target list for every build/release. The original behavior.
        /// </summary>
        /// <remarks>
        /// Complete but noisy on an active project — one task per build accumulates fast, and none of them are
        /// work anybody planned. Prefer a comment mode unless the list exists specifically as a build log.
        /// </remarks>
        NewTaskInList = 0,

        /// <summary>
        /// Comment on the task currently focused via <see cref="ClickUpTaskFocus"/>, and skip the push entirely
        /// when nothing is focused.
        /// </summary>
        /// <remarks>
        /// The quietest option: build activity lands on the ticket the work belongs to, and an unfocused working
        /// copy reports nothing rather than creating noise.
        /// </remarks>
        CommentOnFocusedTask = 1,

        /// <summary>
        /// Comment on the focused task when one is set, otherwise fall back to creating a task in the target
        /// list.
        /// </summary>
        /// <remarks>
        /// Use when activity must never be lost: attributed to the focused ticket when there is one, still
        /// recorded in the list when there is not.
        /// </remarks>
        CommentOnFocusedTaskOrNewTask = 2
    }
}
