using System.Collections.Generic;
using System.Threading;

namespace Molca.Editor.Starter
{
    /// <summary>The result of running (or previewing) one starter step.</summary>
    public readonly struct MolcaStarterOutcome
    {
        /// <summary>Whether the step changed anything — or, in preview, would change anything.</summary>
        public bool Changed { get; }

        /// <summary>What it did, or why it did nothing. Shown to the user verbatim.</summary>
        public string Message { get; }

        /// <summary>
        /// Project-relative paths the step created, so the run can be reverted by deleting them.
        /// </summary>
        public IReadOnlyList<string> CreatedPaths { get; }

        /// <summary>Creates an outcome.</summary>
        /// <param name="changed">Whether anything changed (or would, in preview).</param>
        /// <param name="message">Human-readable result.</param>
        /// <param name="createdPaths">Paths created, if any.</param>
        public MolcaStarterOutcome(bool changed, string message, IReadOnlyList<string> createdPaths = null)
        {
            Changed = changed;
            Message = message;
            CreatedPaths = createdPaths ?? System.Array.Empty<string>();
        }

        /// <summary>Convenience for "already done / nothing to do".</summary>
        /// <param name="message">Why nothing happened.</param>
        /// <returns>An unchanged outcome.</returns>
        public static MolcaStarterOutcome NoChange(string message) => new MolcaStarterOutcome(false, message);
    }

    /// <summary>
    /// One step of the opinionated project setup: "install the recommended configuration", as opposed to
    /// remediation's "repair what is broken".
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not a remediation fix.</b> Remediation repairs misconfiguration, and a project
    /// that ships without telemetry is not misconfigured — it made a choice. For a fix to set that up, the
    /// audit would have to report "you could enable more things", which manufactures findings and teaches
    /// people to stop reading them. The starter is explicitly opinionated instead: it says what a
    /// fully-featured Molca project looks like and offers to create it, and it is never swept, never
    /// automatic, and never implied by a finding.</para>
    /// <para><b>Nothing is copied out of a package.</b> Every asset a step produces is generated from code —
    /// <c>ScriptableObject.CreateInstance</c> plus the type's own field initializers. A packaged
    /// <c>.asset</c> template would have to be re-GUID'd on copy, would drift from the schema as fields are
    /// added, and would be an editable file inside an immutable package that the next upgrade replaces.</para>
    /// <para><b>Steps are idempotent.</b> <see cref="IsSatisfied"/> lets a step report that it has nothing
    /// to do, so the starter is safe to re-run on a partially configured project.</para>
    /// <para>Editor-only; main thread. Implementations need a public parameterless constructor.</para>
    /// </remarks>
    public interface IMolcaStarterStep
    {
        /// <summary>Stable, unique id (e.g. <c>starter.global-settings</c>).</summary>
        string Id { get; }

        /// <summary>Short title for the step's row.</summary>
        string Title { get; }

        /// <summary>What running it will create or configure, in the user's terms.</summary>
        string Description { get; }

        /// <summary>Sort order; steps that others depend on run first.</summary>
        int Order { get; }

        /// <summary>Whether this step's work is already done.</summary>
        /// <returns><c>true</c> when running it would change nothing.</returns>
        bool IsSatisfied();

        /// <summary>Runs — or, when <paramref name="dryRun"/>, previews — the step.</summary>
        /// <param name="dryRun">When true, describe what would happen and write nothing.</param>
        /// <param name="cancellationToken">Cancellation for long steps.</param>
        /// <returns>The outcome.</returns>
        MolcaStarterOutcome Apply(bool dryRun, CancellationToken cancellationToken);
    }
}
