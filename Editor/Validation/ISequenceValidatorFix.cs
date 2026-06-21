using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Validation
{
    /// <summary>
    /// The outcome of applying an <see cref="ISequenceValidatorFix"/> to a single finding.
    /// </summary>
    public readonly struct SequenceFixOutcome
    {
        /// <summary>Whether the fix actually changed anything.</summary>
        public bool Applied { get; }

        /// <summary>Human-readable result message (why it did or didn't apply).</summary>
        public string Message { get; }

        /// <summary>
        /// True when the change is only visible after a scene reload (e.g. the broken-auxiliary YAML
        /// rewrite). The orchestrating tool surfaces this so the caller knows to reload.
        /// </summary>
        public bool RequiresSceneReload { get; }

        /// <param name="applied">Whether the fix changed anything.</param>
        /// <param name="message">Result message.</param>
        /// <param name="requiresSceneReload">True if a scene reload is needed to see the change.</param>
        public SequenceFixOutcome(bool applied, string message, bool requiresSceneReload = false)
        {
            Applied = applied;
            Message = message;
            RequiresSceneReload = requiresSceneReload;
        }

        /// <summary>Convenience for "nothing to do / could not apply".</summary>
        /// <param name="message">Why the fix did not apply.</param>
        public static SequenceFixOutcome NotApplied(string message) => new(false, message);
    }

    /// <summary>
    /// How an <see cref="ISequenceValidatorFix"/> reverts. Validation-local (so the layer doesn't depend
    /// on the MCP tool enums); mirrors the same three modes.
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

    /// <summary>
    /// A pluggable fix for a sequence-validation finding (Sprint 38; facets Sprint 41). Mirrors
    /// <see cref="ISequenceValidator"/>: implementations are discovered by <c>TypeCache</c> via
    /// <see cref="SequenceFixRegistry"/> and indexed by the finding categories they handle, so a fork
    /// ships a fix for its own validator's findings simply by declaring a parameterless class — prefer
    /// extending <see cref="SequenceValidatorFixBase"/> so future facet additions don't break it.
    /// </summary>
    /// <remarks>
    /// <para><b>Facets.</b> A fix describes itself on three orthogonal axes — <see cref="IsDeterministic"/>
    /// (needs no caller input), <see cref="IsDestructive"/> (discards data), and <see cref="Reversibility"/>
    /// — and the registry selects which fixes a given <see cref="RemediationPolicy"/> may auto-apply from
    /// those facets, rather than a single self-declared "safe" flag. The blanket
    /// <see cref="SequenceFixRegistry.ApplyFixes"/> pass under <see cref="RemediationPolicy.SafeOnly"/> runs
    /// only deterministic, non-destructive, Unity-Undo fixes; destructive or parameterized fixes must be
    /// requested explicitly.</para>
    /// <para>Implementations must have a public parameterless constructor.</para>
    /// </remarks>
    public interface ISequenceValidatorFix
    {
        /// <summary>Stable, globally-unique id (e.g. <c>core.legacy-autofix</c>); the registry rejects duplicates.</summary>
        string Id { get; }

        /// <summary>Short human-facing description of what this fix does.</summary>
        string Description { get; }

        /// <summary>
        /// Whether this fix needs no caller input — it can be applied automatically (e.g. regenerating an
        /// empty Ref Id). A non-deterministic fix requires arguments and is never run in a blanket pass.
        /// </summary>
        bool IsDeterministic { get; }

        /// <summary>
        /// Whether this fix discards data (e.g. clearing a broken reference). Destructive fixes are
        /// excluded from <see cref="RemediationPolicy.SafeOnly"/> and must be requested explicitly.
        /// </summary>
        bool IsDestructive { get; }

        /// <summary>How this fix reverts. <see cref="RemediationPolicy.SafeOnly"/> requires <see cref="FixReversibility.UnityUndo"/>.</summary>
        FixReversibility Reversibility { get; }

        /// <summary>
        /// The finding <see cref="SequenceValidationFinding.Category"/> values this fix can repair.
        /// The registry indexes fixes by these.
        /// </summary>
        IReadOnlyCollection<string> HandledCategories { get; }

        /// <summary>
        /// Applies the fix for <paramref name="finding"/>.
        /// </summary>
        /// <param name="finding">The finding to repair (its category is one of <see cref="HandledCategories"/>).</param>
        /// <param name="context">The validation context for the run (controller, steps, scene index).</param>
        /// <param name="args">Optional caller-supplied arguments (e.g. a replacement type name); may be null.</param>
        /// <returns>The outcome of the attempt.</returns>
        SequenceFixOutcome Apply(SequenceValidationFinding finding, SequenceValidationContext context, JObject args);
    }

    /// <summary>
    /// Convenience base for <see cref="ISequenceValidatorFix"/> that supplies the common facet defaults
    /// (deterministic, non-destructive, <see cref="FixReversibility.UnityUndo"/>). A fork overrides only the
    /// facets that differ, so adding new facets later doesn't break existing fixes.
    /// </summary>
    public abstract class SequenceValidatorFixBase : ISequenceValidatorFix
    {
        /// <inheritdoc/>
        public abstract string Id { get; }

        /// <inheritdoc/>
        public abstract string Description { get; }

        /// <inheritdoc/>
        public abstract IReadOnlyCollection<string> HandledCategories { get; }

        /// <inheritdoc/>
        public virtual bool IsDeterministic => true;

        /// <inheritdoc/>
        public virtual bool IsDestructive => false;

        /// <inheritdoc/>
        public virtual FixReversibility Reversibility => FixReversibility.UnityUndo;

        /// <inheritdoc/>
        public abstract SequenceFixOutcome Apply(SequenceValidationFinding finding, SequenceValidationContext context, JObject args);
    }
}
