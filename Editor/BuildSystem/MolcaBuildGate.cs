using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Editor.Doctor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// The pre-build Molca Doctor gate: which checks decide whether a build may run, and the one way
    /// to run them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/BuildSystem/</c>.
    /// </para>
    /// <para>
    /// <b>One owner.</b> This list existed twice — privately inside <see cref="BuildManager"/> and
    /// copied into the Build automation workflow under a comment noting that it mirrored the other.
    /// Two copies of the policy that decides whether a build ships is one copy too many: the workflow's
    /// evidence bundle would claim a gate the build never ran, and neither copy fails when they drift.
    /// Every caller now reads <see cref="CheckIds"/>.
    /// </para>
    /// <para>
    /// <b>Scope.</b> Build-correctness checks only. The code-convention checks (static singletons,
    /// runtime ScriptableObject writes, async contract) scan every script in the project and belong to
    /// a full Doctor run, not to a gate a person waits on before every build.
    /// </para>
    /// </remarks>
    public static class MolcaBuildGate
    {
        /// <summary>
        /// The Doctor check ids that gate a build. An Error from any of them aborts before the build runs.
        /// </summary>
        public static IReadOnlyCollection<string> CheckIds { get; } = new HashSet<string>
        {
            "build-scenes-valid",
            "version-settings-valid",
            "build-profile-valid",
            "unresolvable-scene-reference",
            "content-package-valid",
            // The catalog decides for itself whether its findings block a build
            // (NetworkCatalog.FailBuildOnValidationError); NetworkCatalogCheck reports Error only when it
            // has opted in. Listing it here therefore adds a surface, not a policy: the gate a person
            // waits on before a build now covers the same ground as the build callback that would have
            // failed them later.
            "network-catalog",
        };

        /// <summary>The gate's outcome.</summary>
        public readonly struct Result
        {
            /// <summary>True when no Error-severity finding was produced.</summary>
            public bool Passed => Errors.Count == 0;

            /// <summary>Error-severity findings; these abort the build.</summary>
            public IReadOnlyList<DoctorIssue> Errors { get; }

            /// <summary>Warning-severity findings; reported, never blocking.</summary>
            public IReadOnlyList<DoctorIssue> Warnings { get; }

            /// <summary>Initializes a gate result.</summary>
            /// <param name="errors">Error-severity findings.</param>
            /// <param name="warnings">Warning-severity findings.</param>
            public Result(IReadOnlyList<DoctorIssue> errors, IReadOnlyList<DoctorIssue> warnings)
            {
                Errors = errors ?? System.Array.Empty<DoctorIssue>();
                Warnings = warnings ?? System.Array.Empty<DoctorIssue>();
            }

            /// <summary>The multi-line abort message naming every blocking finding.</summary>
            /// <returns>A message for the console; empty when the gate passed.</returns>
            public string DescribeFailure() =>
                Passed
                    ? string.Empty
                    : $"[BuildManager] Build aborted: {Errors.Count} pre-build Doctor error(s):\n  " +
                      string.Join("\n  ", Errors.Select(e => e.ToString()));
        }

        /// <summary>Runs the gate.</summary>
        /// <param name="cancellationToken">Cancels the checks.</param>
        /// <returns>The findings, split by severity.</returns>
        /// <remarks>
        /// Async because the Doctor checks are: they marshal to the main thread themselves and some
        /// scan the project. Callers that cannot await (the synchronous <see cref="BuildManager.Build(string)"/>)
        /// document that they run ungated rather than pretending otherwise.
        /// </remarks>
        public static async Awaitable<Result> RunAsync(CancellationToken cancellationToken = default)
        {
            var issues = await MolcaDoctor.RunAllAsync(
                enabledIds: new HashSet<string>(CheckIds), cancellationToken: cancellationToken);

            return new Result(
                issues.Where(i => i.Severity == DoctorSeverity.Error).ToList(),
                issues.Where(i => i.Severity == DoctorSeverity.Warning).ToList());
        }
    }
}
