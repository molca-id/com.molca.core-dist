using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Hub;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Surfaces the running (or just-finished) <see cref="DoctorRunSession"/> as a chip in the Molca Hub's
    /// bottom activity rail, so a long Doctor scan stays visible — and clickable back to the Doctor tab —
    /// no matter which Hub tab the user is on. The first consumer of the <see cref="MolcaHubActivityProvider"/>
    /// seam.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Doctor/</c>. Discovered via <c>TypeCache</c>; the rail
    /// instantiates one, subscribes to <see cref="MolcaHubActivityProvider.Changed"/>, and disposes it on
    /// Hub teardown. It observes the static <see cref="DoctorRunSession"/>, so <see cref="Dispose"/> detaches
    /// those handlers. While a run is in flight it emits a progress chip; after a run it emits a dismissible
    /// result chip (cleared automatically when the next run starts). Editor-only; main thread.
    /// </remarks>
    internal sealed class DoctorActivityProvider : MolcaHubActivityProvider
    {
        /// <summary>Hub workspace id of the Doctor tab (see <c>MolcaHubCoreWorkspaceProvider</c>).</summary>
        private const string DoctorWorkspaceId = "doctor";

        // True once the user dismisses the post-run result chip; reset when a new run starts so the next
        // result surfaces again.
        private bool _resultDismissed;

        public DoctorActivityProvider()
        {
            var session = DoctorRunSession.Instance;
            session.RunStarted += OnRunStarted;
            session.ProgressReported += OnProgress;
            session.CheckCompleted += OnCheckCompleted;
            session.RunFinished += OnRunFinished;
        }

        public override void Dispose()
        {
            var session = DoctorRunSession.Instance;
            session.RunStarted -= OnRunStarted;
            session.ProgressReported -= OnProgress;
            session.CheckCompleted -= OnCheckCompleted;
            session.RunFinished -= OnRunFinished;
        }

        private void OnRunStarted()
        {
            _resultDismissed = false; // a fresh run gets a fresh result chip
            NotifyChanged();
        }

        private void OnProgress(DoctorProgress _) => NotifyChanged();
        private void OnCheckCompleted(DoctorCheckReport _) => NotifyChanged();
        private void OnRunFinished(bool _) => NotifyChanged();

        public override IEnumerable<MolcaHubActivity> GetActivities()
        {
            var session = DoctorRunSession.Instance;

            if (session.IsRunning)
            {
                yield return RunningChip(session);
                yield break;
            }

            if (session.HasRun && !_resultDismissed)
                yield return ResultChip(session);
        }

        private static MolcaHubActivity RunningChip(DoctorRunSession session)
        {
            int done = session.Reports.Count;
            int total = session.TotalCount;
            var current = session.CurrentProgress?.CurrentCheck?.Id;

            // Between checks CurrentProgress is null; fall back to the completed count so the chip never blanks.
            string status = current != null ? $"{done + 1}/{total} · {current}" : $"{done}/{total}";
            float? progress = total > 0 ? (float)done / total : (float?)null;

            return new MolcaHubActivity(
                id: "doctor",
                label: "Doctor",
                status: status,
                state: MolcaHubActivityState.Running,
                progress: progress,
                workspaceId: DoctorWorkspaceId,
                // Core-authored caption: a check count and a Molca check id, both bounded.
                remoteSafe: true);
        }

        private MolcaHubActivity ResultChip(DoctorRunSession session)
        {
            int errors = session.Issues.Count(i => i.Severity == DoctorSeverity.Error);
            int warnings = session.Issues.Count(i => i.Severity == DoctorSeverity.Warning);
            int findings = session.Issues.Count;

            MolcaHubActivityState state;
            string status;
            if (session.WasCanceled)
            {
                state = MolcaHubActivityState.Idle;
                status = "canceled";
            }
            else if (errors > 0)
            {
                state = MolcaHubActivityState.Error;
                status = $"{errors} error{(errors == 1 ? "" : "s")}"
                       + (warnings > 0 ? $", {warnings} warning{(warnings == 1 ? "" : "s")}" : "");
            }
            else if (warnings > 0)
            {
                state = MolcaHubActivityState.Warning;
                status = $"{warnings} warning{(warnings == 1 ? "" : "s")}";
            }
            else
            {
                state = MolcaHubActivityState.Ok;
                status = findings == 0 ? "clean" : $"{findings} finding{(findings == 1 ? "" : "s")}";
            }

            return new MolcaHubActivity(
                id: "doctor",
                label: "Doctor",
                status: status,
                state: state,
                workspaceId: DoctorWorkspaceId,
                onDismiss: () => { _resultDismissed = true; NotifyChanged(); },
                // Core-authored caption: issue counts only, never an issue message.
                remoteSafe: true);
        }
    }
}
