using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.ReferenceSystem;
using UnityEditor;

namespace Molca.Editor
{
    /// <summary>
    /// Pre-build reference gate: audits the scenes going into the player and reports every finding that
    /// must not ship.
    /// </summary>
    /// <remarks>
    /// <para>This is a thin facade over <see cref="ReferenceAuditEngine"/>. It deliberately owns no
    /// scanning or analysis of its own — the previous implementation had both, and its rules disagreed
    /// with the runtime's: it detected duplicates on the exact <c>(RefType, RefId)</c> key but tested
    /// resolvability on the Ref Id alone, so a reference whose Ref Type no longer matched any provider
    /// passed the gate and then failed at runtime, and a reference matching two providers by id passed the
    /// gate while the runtime refused the ambiguity.</para>
    ///
    /// <para>It also <b>fails closed</b>. The old validator returned "no errors" when it could not scan —
    /// when open scenes had unsaved state, when the save prompt was cancelled, or when its own analysis
    /// threw — so an unscannable project built green. Coverage gaps and scan failures are now findings in
    /// their own right, and errors for a production build.</para>
    /// </remarks>
    public static class SceneReferenceBuildValidator
    {
        /// <summary>
        /// Runs a build-scoped audit.
        /// </summary>
        /// <param name="developmentBuild">
        /// When true, the relaxed severity policy applies: codes that only describe stale or incomplete
        /// state drop below error so iteration builds are not blocked. Duplicate providers, ambiguous
        /// fallbacks and wrong target types stay errors either way — they are runtime failures, not
        /// hygiene.
        /// </param>
        /// <param name="scenePaths">
        /// Scenes going into the player. Null uses <see cref="ScenesToAudit"/> — the enabled Build Settings
        /// scenes plus every scene the declared load sets mention, since an additively-loaded scene is
        /// usually not in the enabled list and its providers must still be discovered.
        /// </param>
        /// <returns>The snapshot. Never null.</returns>
        public static ReferenceAuditSnapshot Audit(bool developmentBuild = false, IEnumerable<string> scenePaths = null)
        {
            var settings = ReferenceAuditService.FindSettings();
            var scope = ReferenceAuditScope.ForBuild(
                scenePaths ?? ScenesToAudit(),
                settings?.PrefabScanPaths,
                developmentBuild ? ReferenceSeverityPolicy.Relaxed : ReferenceSeverityPolicy.Default);

            return ReferenceAuditService.Refresh(scope);
        }

        /// <summary>
        /// Validates the enabled build scenes and returns one message per problem found.
        /// </summary>
        /// <returns>One <c>REFnnn</c>-prefixed message per error. An empty list means build-safe.</returns>
        /// <remarks>
        /// Kept in this shape so <see cref="BuildManager"/> can gate before it mutates any player setting.
        /// Calling it marks the reference gate satisfied for the build that follows, so
        /// <see cref="ReferenceBuildGate"/> does not repeat the work.
        /// </remarks>
        public static List<string> Validate()
        {
            var snapshot = Audit(EditorUserBuildSettings.development);
            var errors = snapshot.Errors.Select(f => f.ToMessage()).ToList();

            if (errors.Count == 0)
                ReferenceBuildGate.MarkValidated(snapshot);

            return errors;
        }

        /// <summary>Enabled, non-empty scene paths from Editor Build Settings, de-duplicated in order.</summary>
        public static IReadOnlyList<string> EnabledBuildScenes() =>
            EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// Every scene the declared load sets mention, unioned with the enabled build scenes.
        /// </summary>
        /// <remarks>
        /// <para>The audit still <i>scans</i> one combined set, because a provider has to be discovered
        /// before anything can be said about it. What load sets change is the <i>conclusion</i>: whether a
        /// discovered provider is actually reachable from a given owner is decided by
        /// <see cref="ReferenceLoadSetStore.Evaluate"/> inside the analyzer, which is why the gate no
        /// longer implies that co-scanning means co-loading.</para>
        ///
        /// <para>The union matters because a load set may name a scene that is not enabled in Build
        /// Settings — an additively-loaded level usually is not. Scanning only the enabled list would
        /// leave that scene's providers undiscovered, and every reference into it would be reported as
        /// missing rather than as deferred.</para>
        /// </remarks>
        public static IReadOnlyList<string> ScenesToAudit()
        {
            var scenes = new List<string>(EnabledBuildScenes());

            foreach (var set in ReferenceLoadSetStore.Sets)
            {
                foreach (var scene in set.AllScenes)
                {
                    if (!string.IsNullOrEmpty(scene))
                        scenes.Add(scene);
                }
            }

            return scenes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
