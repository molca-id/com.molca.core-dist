using System;
using System.Collections.Generic;
using System.Threading;

namespace Molca.Editor.Remediation
{
    /// <summary>
    /// A domain's audit result projected into the shape a remediation pass consumes.
    /// </summary>
    /// <remarks>
    /// The domain owns its audit; the pass driver only needs the findings, whether the snapshot can be
    /// trusted, and what the snapshot did not cover. Mirrors the reference system's separation of
    /// <c>Skipped</c> (a category the scope never promised → <see cref="CoverageNote"/>) from
    /// <c>Failed</c> (a category the run could not finish → <see cref="IsStale"/>).
    /// </remarks>
    public sealed class MolcaAuditProjection
    {
        /// <summary>Creates a projection.</summary>
        /// <param name="targets">The findings, projected as fix targets.</param>
        /// <param name="coverageNote">What the snapshot did not cover; <c>null</c> when coverage is complete.</param>
        /// <param name="isStale">True when the snapshot cannot be trusted and no fix may be applied from it.</param>
        public MolcaAuditProjection(
            IReadOnlyList<MolcaFixTarget> targets, string coverageNote = null, bool isStale = false)
        {
            Targets = targets ?? Array.Empty<MolcaFixTarget>();
            CoverageNote = coverageNote;
            IsStale = isStale;
        }

        /// <summary>The findings, projected as fix targets.</summary>
        public IReadOnlyList<MolcaFixTarget> Targets { get; }

        /// <summary>What the snapshot did not cover; <c>null</c> when coverage is complete.</summary>
        public string CoverageNote { get; }

        /// <summary>
        /// True when the run attempted a category and could not finish it. A stale snapshot never gets a
        /// blanket pass — an incomplete one still may, with the gap reported.
        /// </summary>
        public bool IsStale { get; }
    }

    /// <summary>
    /// Everything <see cref="MolcaRemediationPass"/> needs to plan or apply one domain's remediation.
    /// </summary>
    /// <remarks>
    /// <see cref="Audit"/> is called once per fixpoint iteration, so it must re-run (or re-read) the domain's
    /// audit rather than return a captured snapshot — otherwise a fix that exposes a new finding is invisible.
    /// It must be a read-only operation: a pass never triggers a scan that mutates.
    /// </remarks>
    public sealed class MolcaRemediationRequest
    {
        /// <summary>Creates a request.</summary>
        /// <param name="domain">Audit domain key, e.g. <c>references</c>; used in reports and undo group names.</param>
        /// <param name="audit">Re-runs the domain's read-only audit and projects it. Called per iteration.</param>
        public MolcaRemediationRequest(string domain, Func<MolcaAuditProjection> audit)
        {
            Domain = domain;
            Audit = audit;
        }

        /// <summary>Audit domain key, e.g. <c>references</c>.</summary>
        public string Domain { get; }

        /// <summary>Re-runs the domain's read-only audit and projects it.</summary>
        public Func<MolcaAuditProjection> Audit { get; }

        /// <summary>Which fixes may auto-apply. Defaults to the only policy a UI may apply unconfirmed.</summary>
        public RemediationPolicy Policy { get; set; } = RemediationPolicy.SafeOnly;

        /// <summary>
        /// Restricts the pass to these fix ids — the mechanism behind "review other fixes", where the user
        /// checks specific Yellow fixes. <c>null</c> means every fix the policy allows.
        /// </summary>
        public IReadOnlyCollection<string> FixIdFilter { get; set; }

        /// <summary>The Unity Undo group name for the pass. Defaults to <c>"Molca remediation: {domain}"</c>.</summary>
        public string UndoGroupName { get; set; }

        /// <summary>Cancellation for the pass; checked between fixes.</summary>
        public CancellationToken CancellationToken { get; set; }
    }
}
