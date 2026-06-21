using System.Collections.Generic;
using System.Threading;
using Molca.Editor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Reports <see cref="Molca.Sequence.SequenceController"/>s in the open scene(s) that fail validation
    /// (Sprint 43). Side-effect-free: sweeps loaded scenes only via <see cref="SequenceValidationSweep"/>
    /// (the Sprint-37 registry), never opening or mutating scenes. Stays on the main thread because it
    /// touches the scene graph, and yields cooperatively.
    /// </summary>
    public class SequenceValidationCheck : IDoctorCheck
    {
        /// <inheritdoc/>
        public string Id => "sequence-validation";

        /// <inheritdoc/>
        public string Description =>
            "SequenceControllers in open scenes validate clean (Ref Ids, references, structure).";

        /// <inheritdoc/>
        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            // Scene-graph access → main thread (no BackgroundThreadAsync). The sweep is bounded by the open
            // scenes; we yield only periodically while mapping results, so a small project completes in one
            // tick (and stays testable) while a large one keeps the editor responsive.
            var issues = new List<DoctorIssue>();
            var sweep = SequenceValidationSweep.SweepLoadedScenes(includeFindings: false);

            int processed = 0;
            foreach (var c in sweep.Controllers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (c.ErrorCount > 0)
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                        $"Sequence '{c.ControllerName}' (refId:{c.ControllerRefId}) has {c.ErrorCount} validation "
                        + $"error(s) and {c.WarningCount} warning(s). Run molca_validate_sequence / molca_sequence_remediate.",
                        c.ScenePath));
                else if (c.WarningCount > 0)
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"Sequence '{c.ControllerName}' (refId:{c.ControllerRefId}) has {c.WarningCount} validation warning(s).",
                        c.ScenePath));

                if (++processed % 16 == 0)
                    await Awaitable.NextFrameAsync(cancellationToken);
            }
            return issues;
        }
    }
}
