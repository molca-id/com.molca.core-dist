namespace Molca.Editor.Automation
{
    /// <summary>
    /// Editor state a command requires in order to run. The kernel gates invocation on this so a
    /// command that reads runtime data returns a clear "needs Play mode" refusal instead of empty
    /// results, and a command that mutates assets is not run mid-play. Mirrors the released
    /// <see cref="Molca.Editor.Mcp.McpToolMode"/> so adapted MCP tools map one-to-one (Phase 1 §9.1).
    /// </summary>
    public enum MolcaCommandMode
    {
        /// <summary>Runnable regardless of play state (e.g. <c>molca.status</c>).</summary>
        Any,

        /// <summary>Requires the editor to be in Edit mode (not playing).</summary>
        Edit,

        /// <summary>Requires the editor to be in Play mode (live runtime state available).</summary>
        Play
    }

    /// <summary>
    /// Whether a command only reads editor/runtime state or performs a mutating action. Read-only
    /// commands may run concurrently where their work permits; actions are serialized and gated by
    /// policy, confirmation, and audit (§13). Mirrors <see cref="Molca.Editor.Mcp.McpToolKind"/>.
    /// </summary>
    public enum MolcaCommandKind
    {
        /// <summary>Reads state only; safe to expose to any transport and to run concurrently.</summary>
        ReadOnly,

        /// <summary>Mutates project/editor state; requires policy authorization and audit.</summary>
        Action
    }

    /// <summary>
    /// How (or whether) an <see cref="MolcaCommandKind.Action"/> command's effect can be reverted.
    /// Surfaced in confirmation prompts and used to decide whether a rollback path exists (§8, §13).
    /// Extends the MCP reversibility set with an explicit <see cref="None"/> and a
    /// <see cref="CompensatingAction"/> case for the closed-loop workflows (§13, Phase 4).
    /// </summary>
    public enum MolcaCommandReversibility
    {
        /// <summary>The effect cannot be undone (e.g. a build runs an external process). Irreversible.</summary>
        None,

        /// <summary>The command mutates in-memory objects through Unity's <c>Undo</c> stack (Ctrl+Z reverts).</summary>
        UnityUndo,

        /// <summary>The command backs up the files it edits and can be reverted from that snapshot.</summary>
        FileSnapshot,

        /// <summary>The command declares a compensating action that logically reverses its effect.</summary>
        CompensatingAction
    }

    /// <summary>
    /// Terminal or in-flight status of a single command/workflow run, serialized verbatim into
    /// <see cref="MolcaCommandResult.Status"/> (§8). String values are the wire contract — callers
    /// branch on these, never on log strings.
    /// </summary>
    public enum MolcaCommandStatus
    {
        /// <summary>Accepted and waiting for its resources or its turn.</summary>
        Queued,

        /// <summary>Currently executing.</summary>
        Running,

        /// <summary>Completed and its postcondition (if any) held.</summary>
        Succeeded,

        /// <summary>Ran but failed, or its verification failed even though the delegate returned.</summary>
        Failed,

        /// <summary>Cancelled by the caller or a lifetime token before completion.</summary>
        Cancelled,

        /// <summary>Rejected by policy/mode before any effect (not an error, an authorization outcome).</summary>
        Refused,

        /// <summary>Requires interactive confirmation before it will run (irreversible action).</summary>
        NeedsConfirmation,

        /// <summary>Could not acquire its required resources; the caller may retry or queue.</summary>
        Blocked,

        /// <summary>
        /// Was in flight when its session ended (domain reload, editor crash) so its outcome was never
        /// recorded (§12, Phase 4). Reconciled from a persisted <see cref="Running"/>/<see cref="Queued"/>
        /// record on load — the run did not complete and its effect, if any, is of unknown state.
        /// </summary>
        Interrupted
    }

    /// <summary>
    /// A shared, serializable Unity/Editor resource a command claims for the duration of its run. The
    /// execution coordinator (§13) uses these to allow read-only overlap while serializing every
    /// mutating access, acquiring in a fixed global order to prevent deadlocks. Declaring accurate
    /// claims is what makes autonomous parallelism safe.
    /// </summary>
    public enum MolcaResourceClaim
    {
        /// <summary>Reads project/asset state without mutating it. Multiple may overlap.</summary>
        ProjectRead,

        /// <summary>Mutates or imports the AssetDatabase. Exclusive.</summary>
        AssetDatabaseWrite,

        /// <summary>Mutates open scene/prefab contents. Exclusive.</summary>
        SceneWrite,

        /// <summary>Pushes onto Unity's Undo stack. Exclusive.</summary>
        UndoStack,

        /// <summary>Transitions or observes Play Mode. Exclusive.</summary>
        PlayMode,

        /// <summary>Switches the active build target. Excludes all other mutating commands.</summary>
        BuildTarget,

        /// <summary>Drives the build pipeline. Excludes all other mutating commands.</summary>
        BuildPipeline,

        /// <summary>Changes <c>Packages/manifest.json</c>. Exclusive and always explicitly authorized.</summary>
        PackageManifest,

        /// <summary>Performs outbound network requests to allowlisted hosts.</summary>
        ExternalNetwork,

        /// <summary>Spawns an external process.</summary>
        ExternalProcess,

        /// <summary>Communicates with a connected development Player.</summary>
        DevelopmentPlayer
    }
}
