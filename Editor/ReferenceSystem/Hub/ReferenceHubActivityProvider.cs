using System.Collections.Generic;
using Molca.Editor.Hub;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// Surfaces reference health as a chip in the Hub's bottom activity rail: scan progress while an audit
    /// runs, and afterwards a chip only when there is something to act on.
    /// </summary>
    /// <remarks>
    /// <para>Placement: <c>Packages/com.molca.core/Editor/ReferenceSystem/Hub/</c>. Discovered via
    /// <c>TypeCache</c>; the rail creates one, subscribes to <see cref="MolcaHubActivityProvider.Changed"/>,
    /// and disposes it on Hub teardown — so the static session subscriptions are detached in
    /// <see cref="Dispose"/>.</para>
    ///
    /// <para><b>A clean project shows no chip.</b> A permanent green pill is noise, and noise is what makes a
    /// rail stop being read; the amber and red states are the ones worth interrupting for. Incomplete coverage
    /// and staleness <i>do</i> get a chip, because both mean the last result cannot be trusted as a clean bill
    /// of health, and that is precisely the confusion this system exists to remove.</para>
    ///
    /// <para>The caption is Core-authored and contains counts and states only — never an asset path — so it is
    /// safe to project into a remote session (<see cref="MolcaHubActivity.RemoteSafe"/>). The health model's
    /// <see cref="ReferenceHubHealth.DescribeForActivityRail"/> is the only text source used here, and it is
    /// built from numbers.</para>
    /// </remarks>
    internal sealed class ReferenceHubActivityProvider : MolcaHubActivityProvider
    {
        private const string ChipId = "references";

        private bool _resultDismissed;

        public ReferenceHubActivityProvider()
        {
            var session = ReferenceHubSession.Instance;
            session.RunStarted += OnRunStarted;
            session.ProgressReported += OnProgress;
            session.RunFinished += OnRunFinished;
            session.SnapshotChanged += NotifyChanged;
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            var session = ReferenceHubSession.Instance;
            session.RunStarted -= OnRunStarted;
            session.ProgressReported -= OnProgress;
            session.RunFinished -= OnRunFinished;
            session.SnapshotChanged -= NotifyChanged;
        }

        private void OnRunStarted()
        {
            _resultDismissed = false; // a fresh run earns a fresh result chip
            NotifyChanged();
        }

        private void OnProgress(string phase, float fraction) => NotifyChanged();

        private void OnRunFinished(bool cancelled) => NotifyChanged();

        /// <inheritdoc/>
        public override IEnumerable<MolcaHubActivity> GetActivities()
        {
            var session = ReferenceHubSession.Instance;
            var chip = Chip(
                session.Health, session.HasRun, _resultDismissed,
                () => { _resultDismissed = true; NotifyChanged(); });

            if (chip != null)
                yield return chip;
        }

        /// <summary>
        /// The chip a given health state earns, or <c>null</c> for the states that earn none.
        /// </summary>
        /// <param name="health">The current health.</param>
        /// <param name="hasRun">Whether any audit has completed this session.</param>
        /// <param name="dismissed">Whether the user dismissed the last result chip.</param>
        /// <param name="onDismiss">Dismiss handler attached to a result chip.</param>
        /// <returns>The chip, or null when the rail should stay quiet.</returns>
        /// <remarks>
        /// Pure and separate from <see cref="GetActivities"/> so the rule "a clean project shows no chip" is
        /// tested rather than eyeballed: the chip's whole value is that it appears only when it means
        /// something.
        /// </remarks>
        internal static MolcaHubActivity Chip(
            ReferenceHubHealth health, bool hasRun, bool dismissed, System.Action onDismiss)
        {
            if (health == null)
                return null;

            if (health.State == ReferenceHubHealthState.Scanning)
            {
                return new MolcaHubActivity(
                    id: ChipId,
                    label: "References",
                    status: health.DescribeForActivityRail(),
                    state: MolcaHubActivityState.Running,
                    progress: health.ScanProgress,
                    workspaceId: ReferenceHubWorkspaceProvider.WorkspaceId,
                    remoteSafe: true);
            }

            if (dismissed || !hasRun)
                return null;

            // Clean is the one state with nothing to say. Everything else is either a problem or an admission
            // that the result does not cover what the user probably assumes it covers.
            if (health.State == ReferenceHubHealthState.Clean)
                return null;

            return new MolcaHubActivity(
                id: ChipId,
                label: "References",
                status: health.DescribeForActivityRail(),
                state: StateFor(health.State),
                workspaceId: ReferenceHubWorkspaceProvider.WorkspaceId,
                onDismiss: onDismiss,
                remoteSafe: true);
        }

        private static MolcaHubActivityState StateFor(ReferenceHubHealthState state) => state switch
        {
            ReferenceHubHealthState.Errors => MolcaHubActivityState.Error,
            ReferenceHubHealthState.Warnings => MolcaHubActivityState.Warning,
            ReferenceHubHealthState.Incomplete => MolcaHubActivityState.Warning,
            ReferenceHubHealthState.Stale => MolcaHubActivityState.Warning,
            _ => MolcaHubActivityState.Idle,
        };
    }
}
