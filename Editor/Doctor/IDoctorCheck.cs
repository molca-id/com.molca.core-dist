using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// A single Molca Doctor validation. Implementations must be side-effect free —
    /// they report issues, they never fix them.
    /// </summary>
    public interface IDoctorCheck
    {
        /// <summary>Stable id used in reports and suppressions (kebab-case).</summary>
        string Id { get; }

        /// <summary>One-line description shown in the Doctor window.</summary>
        string Description { get; }

        /// <summary>
        /// Runs the check asynchronously and returns all findings (never null).
        /// </summary>
        /// <param name="context">Shared source-file cache and scan scope.</param>
        /// <param name="cancellationToken">
        /// Cancelled when the user aborts the run or the editor tears down. Checks
        /// must observe it cooperatively so a long scan can be stopped. Throwing
        /// <see cref="System.OperationCanceledException"/> on cancellation is expected
        /// and is not treated as a failure.
        /// </param>
        /// <remarks>
        /// CPU-only checks (text/reflection scans) should hop to a background thread
        /// via <c>Awaitable.BackgroundThreadAsync()</c> so the editor stays responsive.
        /// Checks that touch <c>AssetDatabase</c>, <c>SerializedObject</c>, or the scene
        /// graph must stay on the main thread and yield with
        /// <c>Awaitable.NextFrameAsync(cancellationToken)</c> periodically instead.
        /// </remarks>
        Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken);
    }
}
