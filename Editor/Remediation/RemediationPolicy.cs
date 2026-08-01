namespace Molca.Editor.Remediation
{
    /// <summary>
    /// Which fixes a blanket remediation pass may auto-apply, decided from fix facets rather than a
    /// self-declared "safe" flag.
    /// </summary>
    /// <remarks>
    /// Hoisted from the <c>com.molca.sequence</c> add-on's <c>Molca.Editor.Validation.RemediationPolicy</c>
    /// (Sprints 38/41) so every Molca audit domain gates blanket passes by one vocabulary. The add-on enum
    /// is retained for source compatibility and mapped by its adapter.
    /// <para>Only <see cref="SafeOnly"/> may be applied by a UI affordance without per-fix confirmation.</para>
    /// </remarks>
    public enum RemediationPolicy
    {
        /// <summary>Deterministic, non-destructive, Unity-Undo only — the default safe pass.</summary>
        SafeOnly,

        /// <summary>Deterministic and revertible (Unity-Undo or file-snapshot), including destructive ones.</summary>
        DeterministicReversible,

        /// <summary>Every deterministic fix, regardless of destructiveness or reversibility.</summary>
        All,
    }
}
