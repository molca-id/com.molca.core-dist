using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.ReferenceSystem;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor
{
    /// <summary>
    /// The global pre-build reference gate. Runs for every build entry point — Molca
    /// <see cref="BuildManager"/>, <b>File &gt; Build</b>, and batch-mode CI — because it is a
    /// <see cref="IPreprocessBuildWithReport"/> rather than a call site inside one build path.
    /// </summary>
    /// <remarks>
    /// <para>Previously reference validation was only invoked from Molca's own build command, so
    /// documenting it as "the build gate" overstated it: a developer using Unity's own Build button, or a
    /// CI job calling <c>BuildPipeline.BuildPlayer</c>, shipped without any reference validation at
    /// all.</para>
    ///
    /// <para>The preprocessor cannot see a caller-supplied scene list, so it validates the scenes the running
    /// build declared — the Molca build profile's scene set when there is one, otherwise the enabled Editor
    /// Build Settings scenes. <see cref="ProcessedSceneAudit"/> closes the remaining gap from the other side: it
    /// observes the scenes the build <i>actually</i> processes and fails the build if one was never
    /// validated, rather than letting an explicit scene list bypass the gate silently.</para>
    ///
    /// <para>Nothing here prompts. In batch mode a dialog would hang CI until timeout, and even
    /// interactively a build gate that asks a question is a gate that gets clicked through.</para>
    /// </remarks>
    public sealed class ReferenceBuildGate : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        /// <summary>
        /// Runs early, before player-setting mutations by other callbacks, so an abort needs no restore.
        /// This is the principle the whole gate band is ordered on; see
        /// <see cref="MolcaBuildCallbackOrder"/>.
        /// </summary>
        public int callbackOrder => MolcaBuildCallbackOrder.ReferenceGate;

        // Set by SceneReferenceBuildValidator.Validate so BuildManager's own early gate is not repeated
        // by this callback moments later. Cleared as soon as it is consumed, and never trusted across a
        // build: a stale latch would silently disable the gate.
        private static ReferenceAuditSnapshot _validatedSnapshot;
        private static HashSet<string> _validatedScenes;

        /// <summary>
        /// Records that <paramref name="snapshot"/> already validated this build's scenes, so the
        /// preprocessor can skip a duplicate audit.
        /// </summary>
        /// <param name="snapshot">A snapshot with no error findings.</param>
        internal static void MarkValidated(ReferenceAuditSnapshot snapshot)
        {
            _validatedSnapshot = snapshot;
            _validatedScenes = new HashSet<string>(
                snapshot?.Scope?.ExplicitScenePaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        /// <inheritdoc/>
        public void OnPreprocessBuild(BuildReport report)
        {
            var isDevelopment = report?.summary.options.HasFlag(BuildOptions.Development)
                ?? EditorUserBuildSettings.development;

            var snapshot = ConsumeValidatedSnapshot() ?? AuditForCurrentBuild(isDevelopment);

            // Keep the validated scene set for ProcessedSceneAudit to compare against.
            _validatedScenes = new HashSet<string>(
                snapshot.Scope?.ExplicitScenePaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (snapshot.Errors.Count == 0)
            {
                if (snapshot.Warnings.Count > 0)
                {
                    Debug.LogWarning(
                        $"[ReferenceBuildGate] {snapshot.Warnings.Count} reference warning(s) in this build:\n  "
                        + string.Join("\n  ", snapshot.Warnings.Select(w => w.ToMessage())));
                }

                return;
            }

            MolcaBuildRefusal.Record(MolcaBuildReasonCode.ReferenceGate);
            throw new BuildFailedException(BuildFailureMessage(snapshot, isDevelopment));
        }

        /// <summary>
        /// Drops the validated scene set once the build finishes.
        /// </summary>
        /// <remarks>
        /// The set must not outlive its build. An Addressables content build invokes
        /// <see cref="IProcessSceneWithReport"/> without ever running a build <i>preprocessor</i>, so a set
        /// left behind by an earlier player build would be consulted against unrelated scenes and fail that
        /// content build for no reason.
        /// </remarks>
        public void OnPostprocessBuild(BuildReport report)
        {
            _validatedScenes = null;
            _validatedSnapshot = null;
        }

        /// <summary>
        /// Audits the scenes this build ships, when the gate has to audit for itself.
        /// </summary>
        /// <param name="isDevelopment">True to apply the relaxed severity policy.</param>
        /// <returns>The snapshot for the running build.</returns>
        /// <remarks>
        /// A preprocessor is handed no scene list, so this used to audit the enabled Editor Build Settings
        /// scenes unconditionally. A build profile may now declare its own scene set, and for such a build
        /// the enabled list is the wrong question — the audit would cover scenes that are not shipping and
        /// miss ones that are, and <see cref="ReferenceBuildSceneAudit"/> would then fail the build for
        /// processing an unvalidated scene. <see cref="MolcaBuildSession"/> exists for exactly this: the
        /// running Molca build's profile is readable even from a callback Unity discovered by type. Outside
        /// a Molca build there is no profile, and the enabled list is again the only thing the gate can know.
        /// </remarks>
        private static ReferenceAuditSnapshot AuditForCurrentBuild(bool isDevelopment)
        {
            var profile = MolcaBuildSession.Current?.Profile;
            if (profile != null && profile.HasSceneOverride &&
                profile.TryResolveScenePaths(out var scenePaths, out _) && scenePaths != null)
            {
                return SceneReferenceBuildValidator.Audit(
                    isDevelopment, SceneReferenceBuildValidator.ScenesToAudit(scenePaths));
            }

            return SceneReferenceBuildValidator.Audit(isDevelopment);
        }

        /// <summary>
        /// Builds the abort message: every error with its code and location, plus what to do next.
        /// </summary>
        private static string BuildFailureMessage(ReferenceAuditSnapshot snapshot, bool isDevelopment)
        {
            var lines = new List<string>
            {
                $"[ReferenceBuildGate] Build aborted: {snapshot.Errors.Count} reference error(s) "
                + $"({snapshot.State}, coverage {snapshot.Coverage.DescribeGaps()}).",
            };

            lines.AddRange(snapshot.Errors.Select(e => "  " + e.ToMessage()));

            lines.Add("Fix these in Molca Doctor or the Reference Manager Settings inspector and rebuild.");

            if (!isDevelopment && !snapshot.Coverage.IsComplete)
            {
                lines.Add(
                    "Coverage gaps are errors for a production build by design: a reference audit that "
                    + "could not look everywhere cannot certify that a build is reference-safe.");
            }

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Takes the snapshot left by <see cref="SceneReferenceBuildValidator.Validate"/>, if it is still
        /// trustworthy.
        /// </summary>
        /// <returns>The snapshot, or null when the gate must audit for itself.</returns>
        /// <remarks>
        /// A latch is only ever consumed once, and only while the project has not changed since it was
        /// produced. A stale latch would silently disable the gate for that build — the one failure mode a
        /// gate must not have.
        /// </remarks>
        private static ReferenceAuditSnapshot ConsumeValidatedSnapshot()
        {
            var snapshot = _validatedSnapshot;
            _validatedSnapshot = null;

            return snapshot != null && !ReferenceAuditService.IsStale ? snapshot : null;
        }

        /// <summary>True when <paramref name="scenePath"/> was inside the audited scene set.</summary>
        internal static bool WasSceneValidated(string scenePath) =>
            _validatedScenes != null && _validatedScenes.Contains(scenePath);

        /// <summary>True when a build-scoped audit has run and recorded its scene set.</summary>
        internal static bool HasValidatedSceneSet => _validatedScenes != null;
    }

    /// <summary>
    /// Fails the build if it processes a scene the reference gate never validated.
    /// </summary>
    /// <remarks>
    /// A build driven by an explicit <c>BuildPlayerOptions.scenes</c> list can include scenes that are not
    /// enabled in Editor Build Settings, which is all <see cref="ReferenceBuildGate"/> can see from a
    /// preprocessor. Rather than pretending the gate covered them, the mismatch is reported as a build
    /// failure so the caller either enables the scene in Build Settings or audits it explicitly.
    ///
    /// A top-level type rather than a nested one, so Unity's build-callback discovery finds it through a
    /// documented-supported shape.
    /// </remarks>
    internal sealed class ReferenceBuildSceneAudit : IProcessSceneWithReport
    {
        /// <inheritdoc/>
        public int callbackOrder => -1000;

        /// <inheritdoc/>
        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // A null report means a play-mode scene load. `isBuildingPlayer` excludes content builds, which
            // process scenes without ever running a build preprocessor and so have no validated set of their
            // own to be compared against.
            if (report == null || !BuildPipeline.isBuildingPlayer || !ReferenceBuildGate.HasValidatedSceneSet)
                return;

            if (string.IsNullOrEmpty(scene.path) || ReferenceBuildGate.WasSceneValidated(scene.path))
                return;

            MolcaBuildRefusal.Record(MolcaBuildReasonCode.ReferenceGate);
            throw new BuildFailedException(
                $"[ReferenceBuildGate] Scene '{scene.path}' is being built but was not covered by the "
                + "reference audit, which validated the scenes this build declared — the build profile's "
                + "scene set, or the enabled Editor Build Settings scenes when it declares none. "
                + "Add the scene to the profile (or enable it in Build Settings), or audit it explicitly via "
                + $"{nameof(SceneReferenceBuildValidator)}.{nameof(SceneReferenceBuildValidator.Audit)} "
                + "before building.");
        }
    }
}
