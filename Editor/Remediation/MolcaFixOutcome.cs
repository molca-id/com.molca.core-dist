namespace Molca.Editor.Remediation
{
    /// <summary>The outcome of applying (or previewing) one <see cref="IMolcaFix"/> against one target.</summary>
    /// <remarks>
    /// The union of the two pre-existing outcome structs — the scene-audit <c>SceneFixOutcome</c>
    /// (before/after/undo-entry) and the sequence add-on's <c>SequenceFixOutcome</c>
    /// (requires-scene-reload) — so one report shape can describe every domain.
    /// </remarks>
    public readonly struct MolcaFixOutcome
    {
        /// <summary>Whether the fix changed anything — or, in dry-run, would change anything.</summary>
        public bool Applied { get; }

        /// <summary>Human-readable result message (why it did or did not apply).</summary>
        public string Message { get; }

        /// <summary>Before-state description, e.g. <c>"maxTextureSize: 4096"</c>; optional.</summary>
        public string Before { get; }

        /// <summary>After-state description, e.g. <c>"maxTextureSize: 2048"</c>; optional.</summary>
        public string After { get; }

        /// <summary>
        /// For <see cref="Molca.Editor.Doctor.FixReversibility.FileSnapshot"/> fixes, the
        /// <c>McpUndoStack</c> entry id created so the change can be reverted via
        /// <c>molca_undo_last_action</c>; <c>null</c> otherwise.
        /// </summary>
        /// <remarks>
        /// A provisioning fix records the paths it created here (through its undo entry), so reverting
        /// deletes exactly those assets — Unity's Undo stack cannot reliably remove created assets.
        /// </remarks>
        public string UndoEntryId { get; }

        /// <summary>True when the change is only visible after a scene reload (e.g. a YAML rewrite).</summary>
        public bool RequiresSceneReload { get; }

        /// <summary>Creates an outcome.</summary>
        /// <param name="applied">Whether anything changed (or would change, in dry-run).</param>
        /// <param name="message">Result message.</param>
        /// <param name="before">Before-state description.</param>
        /// <param name="after">After-state description.</param>
        /// <param name="undoEntryId">File-snapshot undo entry id, if any.</param>
        /// <param name="requiresSceneReload">True if a scene reload is needed to see the change.</param>
        public MolcaFixOutcome(
            bool applied,
            string message,
            string before = null,
            string after = null,
            string undoEntryId = null,
            bool requiresSceneReload = false)
        {
            Applied = applied;
            Message = message;
            Before = before;
            After = after;
            UndoEntryId = undoEntryId;
            RequiresSceneReload = requiresSceneReload;
        }

        /// <summary>Convenience for "nothing to do / could not apply".</summary>
        /// <param name="message">Why the fix did not apply. Surfaced verbatim in the declined report.</param>
        /// <returns>A non-applied outcome.</returns>
        public static MolcaFixOutcome NotApplied(string message) => new MolcaFixOutcome(false, message);
    }
}
