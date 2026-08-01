using System.Threading;
using Molca.Editor.Doctor;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Remediation
{
    /// <summary>
    /// A pluggable fix for one namespaced finding code from any Molca audit engine.
    /// </summary>
    /// <remarks>
    /// The single remediation contract, unifying the scene-audit <see cref="ISceneFix"/> (Sprint 55) and the
    /// <c>com.molca.sequence</c> add-on's <c>ISequenceValidatorFix</c> (Sprints 38/41). Implementations are
    /// discovered by <c>TypeCache</c> via <see cref="MolcaFixRegistry"/> and indexed by
    /// <see cref="HandledFindingCode"/>, so a fork ships a fix by declaring a parameterless class — prefer
    /// extending <see cref="MolcaFixBase"/> so future facet additions don't break it. Fixes that must wrap an
    /// existing per-domain abstraction are supplied by an <see cref="IMolcaFixContributor"/> instead.
    /// <para><b>Facets.</b> A fix describes itself on three orthogonal axes — <see cref="IsDeterministic"/>
    /// (needs no caller input), <see cref="IsDestructive"/> (discards authored data), and
    /// <see cref="Reversibility"/> — and <see cref="MolcaFixRegistry.PolicyAllows"/> decides which
    /// <see cref="RemediationPolicy"/> may auto-apply it from those facets, never from a self-declared
    /// "safe" flag.</para>
    /// <para><b>Never mutate on a scan.</b> A fix runs only from an explicit remediation pass. No audit,
    /// refresh, workspace open, Inspector draw, or build gate may invoke one.</para>
    /// <para><b>Route through the domain's mutation service.</b> A fix must not open its own
    /// <c>SerializedObject</c> against an asset a domain editing service already owns.</para>
    /// <para>Editor-only; main thread only. Implementations need a public parameterless constructor.</para>
    /// </remarks>
    public interface IMolcaFix
    {
        /// <summary>Stable, globally-unique id (e.g. <c>colorid.regenerate-uss</c>); the registry rejects duplicates.</summary>
        string Id { get; }

        /// <summary>Short human-facing description of what this fix does.</summary>
        string Description { get; }

        /// <summary>
        /// The namespaced <see cref="MolcaFixTarget.FindingCode"/> values this fix remediates, e.g.
        /// <c>network.catalog.schema-migration-required</c>. The registry indexes the fix under each.
        /// </summary>
        /// <remarks>
        /// Usually one code. Several are legitimate when one repair resolves related codes — the add-on's
        /// <c>ISequenceValidatorFix</c> has always been able to declare multiple categories, and a legacy
        /// field renamed in shipped data can produce two codes for one cause.
        /// </remarks>
        System.Collections.Generic.IReadOnlyCollection<string> HandledFindingCodes { get; }

        /// <summary>
        /// Whether this fix needs no caller input, so it can be applied automatically. A non-deterministic
        /// fix requires <c>args</c> and is never run in a blanket pass, whatever the policy.
        /// </summary>
        bool IsDeterministic { get; }

        /// <summary>
        /// Whether this fix discards authored data (e.g. clearing a broken reference). Destructive fixes are
        /// excluded from <see cref="RemediationPolicy.SafeOnly"/> and must be requested explicitly.
        /// </summary>
        bool IsDestructive { get; }

        /// <summary>
        /// How this fix reverts. <see cref="RemediationPolicy.SafeOnly"/> requires
        /// <see cref="FixReversibility.UnityUndo"/>; asset-creating (provisioning) fixes must declare
        /// <see cref="FixReversibility.FileSnapshot"/> because Unity Undo cannot reliably remove created assets.
        /// </summary>
        FixReversibility Reversibility { get; }

        /// <summary>Applies — or, when <paramref name="dryRun"/>, previews — the fix for one target.</summary>
        /// <param name="target">The finding site to repair; its code is <see cref="HandledFindingCode"/>.</param>
        /// <param name="dryRun">When true, report what would change without writing anything.</param>
        /// <param name="args">Caller-supplied arguments for a non-deterministic fix; may be <c>null</c>.</param>
        /// <param name="cancellationToken">Cancellation for long operations (e.g. a texture reimport).</param>
        /// <returns>The outcome of the attempt.</returns>
        MolcaFixOutcome Apply(MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Convenience base for <see cref="IMolcaFix"/> supplying the common facet defaults (deterministic,
    /// non-destructive, <see cref="FixReversibility.UnityUndo"/>) so a fix overrides only what differs and
    /// later facet additions don't break it.
    /// </summary>
    public abstract class MolcaFixBase : IMolcaFix
    {
        /// <inheritdoc/>
        public abstract string Id { get; }

        /// <inheritdoc/>
        public abstract string Description { get; }

        /// <summary>The single finding code this fix remediates — the common case.</summary>
        /// <remarks>
        /// A fix that remediates several codes overrides <see cref="HandledFindingCodes"/> instead and may
        /// return its primary code here.
        /// </remarks>
        public abstract string HandledFindingCode { get; }

        /// <inheritdoc/>
        public virtual System.Collections.Generic.IReadOnlyCollection<string> HandledFindingCodes
            => new[] { HandledFindingCode };

        /// <inheritdoc/>
        public virtual bool IsDeterministic => true;

        /// <inheritdoc/>
        public virtual bool IsDestructive => false;

        /// <inheritdoc/>
        public virtual FixReversibility Reversibility => FixReversibility.UnityUndo;

        /// <inheritdoc/>
        public abstract MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Supplies fixes that cannot be discovered as parameterless types — typically adapters wrapping an
    /// existing per-domain fix abstraction (<see cref="ISceneFix"/>, the add-on's
    /// <c>ISequenceValidatorFix</c>).
    /// </summary>
    /// <remarks>
    /// Contributors themselves are discovered by <c>TypeCache</c> and need a public parameterless
    /// constructor. This is the seam that lets Core adopt add-on fixes without Core referencing any add-on
    /// assembly — the add-on ships the contributor.
    /// </remarks>
    public interface IMolcaFixContributor
    {
        /// <summary>Returns the fixes this contributor supplies; never <c>null</c>.</summary>
        /// <returns>Fix instances, which the registry then de-duplicates by id alongside discovered ones.</returns>
        System.Collections.Generic.IEnumerable<IMolcaFix> Contribute();
    }
}
