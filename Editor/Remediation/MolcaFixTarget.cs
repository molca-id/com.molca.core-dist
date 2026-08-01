namespace Molca.Editor.Remediation
{
    /// <summary>
    /// The domain-neutral projection of one audit finding that a remediation pass may act on.
    /// </summary>
    /// <remarks>
    /// Every Molca audit engine models findings differently (<c>ReferenceFinding</c>,
    /// <c>SequenceValidationFinding</c>, <c>DoctorIssue</c>, <c>ColorThemeFinding</c>, …). A domain projects
    /// its findings into this shape so <see cref="MolcaFixRegistry"/> and <see cref="MolcaRemediationPass"/>
    /// need no knowledge of any of them; the finding's own object graph travels in
    /// <see cref="DomainContext"/> for the fix to cast.
    /// <para>Immutable. Editor-only.</para>
    /// </remarks>
    public sealed class MolcaFixTarget
    {
        /// <summary>
        /// Creates a fix target.
        /// </summary>
        /// <param name="findingCode">Namespaced finding code, e.g. <c>network.catalog.schema-migration-required</c>.</param>
        /// <param name="path">Project-relative asset path, or <c>"scene :: hierarchy/path"</c> for scene objects.</param>
        /// <param name="message">The finding's human-readable message.</param>
        /// <param name="propertyPath">Serialized property path when the finding targets one field.</param>
        /// <param name="domainContext">
        /// The domain's own finding/snapshot object, passed through untouched for the fix to cast.
        /// </param>
        public MolcaFixTarget(
            string findingCode,
            string path,
            string message = null,
            string propertyPath = null,
            object domainContext = null)
        {
            FindingCode = findingCode;
            Path = path;
            Message = message;
            PropertyPath = propertyPath;
            DomainContext = domainContext;
        }

        /// <summary>Namespaced finding code; the key <see cref="MolcaFixRegistry"/> indexes fixes by.</summary>
        public string FindingCode { get; }

        /// <summary>Project-relative asset path, or <c>"scene :: hierarchy/path"</c> for a scene object.</summary>
        public string Path { get; }

        /// <summary>The finding's human-readable message.</summary>
        public string Message { get; }

        /// <summary>Serialized property path when the finding targets one field; otherwise <c>null</c>.</summary>
        public string PropertyPath { get; }

        /// <summary>
        /// The originating domain object (finding, snapshot, validation context). Fixes cast this; the
        /// registry and pass driver never inspect it.
        /// </summary>
        public object DomainContext { get; }

        /// <summary>
        /// Stable identity of the finding site — <see cref="FindingCode"/> + <see cref="Path"/> +
        /// <see cref="PropertyPath"/>. Used by <see cref="MolcaRemediationPass"/> to avoid re-attempting the
        /// same site across fixpoint iterations, and to match applied/declined entries in a report.
        /// </summary>
        public string Signature => $"{FindingCode}|{Path}|{PropertyPath}";
    }
}
