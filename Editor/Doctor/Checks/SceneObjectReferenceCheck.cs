using System.Collections.Generic;
using System.Threading;
using Molca.Editor.ReferenceSystem;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Reports reference-system findings from the shared read-only audit: unresolvable
    /// <see cref="SceneObjectReference"/> fields, duplicated provider ids, ambiguous compatibility
    /// fallbacks, type mismatches, and incomplete scan coverage.
    /// </summary>
    /// <remarks>
    /// <para>This check owns no scanning logic. It runs <see cref="ReferenceAuditEngine"/> and projects
    /// its findings, so Doctor, the build gate, Sequence validation, the Inspector and MCP cannot disagree
    /// about what "broken" means. The previous implementation had its own scanner and its own rules, and
    /// validated against the cached id lists in <c>ReferenceManagerSettings</c> rather than against the
    /// objects that actually provide the references — so a stale cache produced false findings and a
    /// missing cache entry produced false confidence.</para>
    ///
    /// <para>ScriptableObjects are now scanned. An SO cannot be a runtime <i>target</i>, but it can
    /// certainly hold an outbound reference that resolves a loaded scene object, and conflating the two
    /// is why a real broken reference in this repository went unreported.</para>
    ///
    /// <para>Closed scenes are still not opened: doing so from a validation pass would replace the user's
    /// open scenes. They are reported as skipped coverage instead, which is visible in the Doctor output
    /// rather than silently assumed clean.</para>
    /// </remarks>
    public class SceneObjectReferenceCheck : IDoctorCheck
    {
        /// <inheritdoc/>
        public string Id => "unresolvable-scene-reference";

        /// <inheritdoc/>
        public string Description => "Scene object references resolve, are unambiguous, and are fully covered by the scan";

        /// <inheritdoc/>
        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            var issues = new List<DoctorIssue>();

            context.ReportStatus("Auditing references");

            var scope = ReferenceAuditService
                // mayOpenScenes: false — a validation pass must not replace the user's open scenes.
                .DefaultScope(mayOpenScenes: false)
                .WithIgnoreFilter(context.IsIgnored);

            // The audit is main-thread work (AssetDatabase, SerializedObject, SceneManager), so the async
            // entry point is the one to use: it hands the main thread back periodically, which keeps the
            // Doctor window painting and makes Cancel responsive to about one asset.
            var snapshot = await ReferenceAuditService.RefreshAsync(
                scope,
                (phase, _) => context.ReportStatus(phase),
                cancellationToken);

            foreach (var finding in snapshot.Findings)
            {
                issues.Add(new DoctorIssue(
                    Id,
                    ToDoctorSeverity(finding.Severity),
                    finding.ToMessage(),
                    string.IsNullOrEmpty(finding.AssetPath) ? null : finding.AssetPath));
            }

            return issues;
        }

        private static DoctorSeverity ToDoctorSeverity(ReferenceFindingSeverity severity) => severity switch
        {
            ReferenceFindingSeverity.Error => DoctorSeverity.Error,
            ReferenceFindingSeverity.Warning => DoctorSeverity.Warning,
            _ => DoctorSeverity.Info,
        };
    }
}
