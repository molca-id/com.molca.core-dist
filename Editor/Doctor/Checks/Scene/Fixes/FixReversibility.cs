namespace Molca.Editor.Doctor
{
    /// <summary>
    /// How a Doctor-registered fix reverts. Shared by <see cref="ISceneFix"/> (scene-audit fixes) and the
    /// <c>com.molca.sequence</c> add-on's <c>ISequenceValidatorFix</c> — neutral so neither depends on the
    /// other, and distinct from the MCP tool layer's own reversibility enum.
    /// </summary>
    public enum FixReversibility
    {
        /// <summary>Mutates in-memory objects through Unity's <c>Undo</c> stack (plain Ctrl+Z reverts).</summary>
        UnityUndo,

        /// <summary>Rewrites files; reverted by restoring a <c>McpUndoStack</c> snapshot, not Unity Undo.</summary>
        FileSnapshot,

        /// <summary>Cannot be reverted automatically.</summary>
        Irreversible,
    }
}
