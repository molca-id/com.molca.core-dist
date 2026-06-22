using System.Threading;
using Molca.Editor.Validation;

namespace Molca.Editor.Doctor
{
    /// <summary>The outcome of applying an <see cref="ISceneFix"/> to one scene-audit finding target.</summary>
    public readonly struct SceneFixOutcome
    {
        /// <summary>Whether the fix actually changed (or, in dry-run, would change) anything.</summary>
        public bool Applied { get; }

        /// <summary>Human-readable result message.</summary>
        public string Message { get; }

        /// <summary>The before-state description, e.g. <c>"maxTextureSize: 4096"</c> (optional).</summary>
        public string Before { get; }

        /// <summary>The after-state description, e.g. <c>"maxTextureSize: 2048"</c> (optional).</summary>
        public string After { get; }

        /// <summary>
        /// For <see cref="FixReversibility.FileSnapshot"/> fixes, the <see cref="Molca.Editor.Mcp.McpUndoStack"/>
        /// entry id created so the change can be reverted via <c>molca_undo_last_action</c>; <c>null</c> otherwise.
        /// </summary>
        public string UndoEntryId { get; }

        /// <param name="applied">Whether anything changed (or would change in dry-run).</param>
        /// <param name="message">Result message.</param>
        /// <param name="before">Before-state description.</param>
        /// <param name="after">After-state description.</param>
        /// <param name="undoEntryId">File-snapshot undo entry id, if any.</param>
        public SceneFixOutcome(bool applied, string message, string before = null, string after = null, string undoEntryId = null)
        {
            Applied = applied;
            Message = message;
            Before = before;
            After = after;
            UndoEntryId = undoEntryId;
        }

        /// <summary>Convenience for "nothing to do / could not apply".</summary>
        public static SceneFixOutcome NotApplied(string message) => new SceneFixOutcome(false, message);
    }

    /// <summary>
    /// A pluggable fix for one <b>mechanical</b> scene-performance finding (Sprint 55). Mirrors the Sprints
    /// 38/41 sequence-remediation abstraction (<see cref="ISequenceValidatorFix"/>) for the scene-audit
    /// domain: implementations are discovered by <c>TypeCache</c> via <see cref="SceneFixRegistry"/> and
    /// indexed by the <see cref="HandledCheckId"/> they remediate, so a fork ships a fix by declaring a
    /// parameterless class. Only single-answer findings get a fix; judgment findings stay report-only.
    /// </summary>
    /// <remarks>Editor-only; main thread only (mutates assets / scene objects). Applies one target at a time.</remarks>
    public interface ISceneFix
    {
        /// <summary>Stable, globally-unique fix id (e.g. <c>scene.enable-instancing</c>); the registry rejects duplicates.</summary>
        string Id { get; }

        /// <summary>Short human-facing description of what this fix does.</summary>
        string Description { get; }

        /// <summary>The scene-audit check id (e.g. <c>scene-instancing-budget</c>) whose findings this fix remediates.</summary>
        string HandledCheckId { get; }

        /// <summary>How this fix reverts (Unity Undo for object edits, File snapshot for importer changes).</summary>
        FixReversibility Reversibility { get; }

        /// <summary>
        /// Applies the fix to <paramref name="target"/> — the finding's <see cref="DoctorIssue.Path"/>
        /// (an asset path, or a <c>"scene :: hierarchy/path"</c> for scene objects).
        /// </summary>
        /// <param name="target">The finding target to repair.</param>
        /// <param name="dryRun">When true, report what would change without writing.</param>
        /// <param name="cancellationToken">Cancellation for long operations (e.g. texture reimport).</param>
        SceneFixOutcome Apply(string target, bool dryRun, CancellationToken cancellationToken);
    }
}
