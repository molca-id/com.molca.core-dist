using System.Collections.Generic;

namespace Molca.Editor.Validation
{
    /// <summary>
    /// A pluggable sequence validator. This is the fork extension point for sequence validation: Core
    /// ships its own validators (data integrity, structural flow, reference resolution) and an SDK fork
    /// adds domain validators (e.g. scenario-coverage-vs-spec, backend-upload-contract) simply by
    /// declaring a parameterless implementation — <see cref="SequenceValidatorRegistry"/> discovers it
    /// by <c>TypeCache</c>, with no registration line to edit (mirrors the MCP provider / SettingModule
    /// discovery idioms).
    /// </summary>
    /// <remarks>
    /// <para><b>Layer discipline.</b> Core validators are phrased purely in Sequence/Reference terms and
    /// know nothing of "scenario", a spec, or any backend. Those concepts belong to fork validators.</para>
    /// <para><b>Editor-time only.</b> Implementations run at author time and must not depend on runtime
    /// state (no <c>ResolveAsync</c>, no play mode). Resolve references via
    /// <see cref="SequenceValidationContext.ResolveRefId"/>.</para>
    /// <para>Implementations must have a public parameterless constructor.</para>
    /// </remarks>
    public interface ISequenceValidator
    {
        /// <summary>
        /// Stable, globally-unique id for this validator (e.g. <c>core.structural-flow</c>). Used to
        /// order validators deterministically and to tag every finding; the registry rejects duplicates.
        /// </summary>
        string Id { get; }

        /// <summary>Short human-facing description of what this validator checks.</summary>
        string Description { get; }

        /// <summary>
        /// Validates the sequence described by <paramref name="context"/> and returns its findings.
        /// </summary>
        /// <param name="context">Shared, read-only input for the run (controller, steps, scene Ref-Id index).</param>
        /// <returns>The findings; an empty sequence (never null) when nothing is wrong.</returns>
        IEnumerable<SequenceValidationFinding> Validate(SequenceValidationContext context);
    }
}
