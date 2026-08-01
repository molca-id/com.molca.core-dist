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
        /// Logical group this check belongs to. Organizes the Doctor window into sections and is the
        /// unit by which a run can be scoped to a related subset of checks.
        /// </summary>
        /// <remarks>
        /// Defaults to a value derived from <see cref="Id"/>'s kebab-case prefix
        /// (<see cref="DoctorCategories.Derive"/>), so the built-in checks group correctly with no
        /// per-check wiring. Override only when the id prefix would place the check in the wrong group;
        /// a check whose id matches no known prefix falls back to <see cref="DoctorCategories.General"/>.
        /// </remarks>
        string Category => DoctorCategories.Derive(Id);

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
        /// <para>
        /// A check that hops off the main thread <b>must hop back before it returns</b> —
        /// <c>try { … } finally { await Awaitable.MainThreadAsync(); }</c>. A Unity
        /// <c>Awaitable</c> is a pooled object with a native counterpart, so completing (or faulting)
        /// this state machine on the ThreadPool thread the scan ran on raises the native
        /// <c>Scripting object is not properly attached</c> assert. Nothing throws — the assert is
        /// logged asynchronously, and the test framework charges it to whichever test happens to be
        /// open when it lands, which is why it surfaced as unrelated flaky failures in the OAuth
        /// loopback suite.
        /// </para>
        /// </remarks>
        Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken);
    }
}
