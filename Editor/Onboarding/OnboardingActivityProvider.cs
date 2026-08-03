using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Hub;

namespace Molca.Editor.Onboarding
{
    /// <summary>
    /// Shows one activity-rail chip while the project has outstanding onboarding work, so a new project
    /// announces itself without a dialog.
    /// </summary>
    /// <remarks>
    /// <para>A badge rather than an interruption. The chip states the count, clicking it opens the workspace,
    /// and dismissing it silences the chip for this project — the checklist itself keeps its rows either
    /// way, because dismissing a reminder is not the same as resolving what it reminded you of.</para>
    /// <para><b>It evaluates at most once per Hub session.</b> The rail rebuilds on every provider change, so
    /// evaluating inside <see cref="GetActivities"/> would re-check the project on unrelated repaints. The
    /// one evaluation happens when the Hub constructs this provider; after that the chip follows
    /// <see cref="MolcaOnboardingChecklist.Changed"/>, which the workspace raises whenever it refreshes.</para>
    /// </remarks>
    internal sealed class OnboardingActivityProvider : MolcaHubActivityProvider
    {
        /// <summary>
        /// Per-project dismissal. Kept in <see cref="MolcaEditorPrefs"/> rather than a project asset: whether
        /// <em>this</em> user wants the nudge is a per-user preference, while whether the work is done is
        /// derived from the project every time and never stored at all.
        /// </summary>
        private const string DismissedPrefKey = "Onboarding.ChipDismissed";

        /// <summary>Subscribes and takes the session's single evaluation.</summary>
        public OnboardingActivityProvider()
        {
            MolcaOnboardingChecklist.Changed += OnChecklistChanged;
            MolcaOnboardingChecklist.EvaluateIfNeeded();
        }

        /// <inheritdoc/>
        public override IEnumerable<MolcaHubActivity> GetActivities()
        {
            if (MolcaEditorPrefs.GetBool(DismissedPrefKey))
                yield break;

            var snapshot = MolcaOnboardingChecklist.LastSnapshot;
            if (snapshot == null || snapshot.IsClear)
                yield break;

            yield return new MolcaHubActivity(
                id: "onboarding",
                label: "Onboarding",
                status: snapshot.Summarize(),
                // Red only for a confirmed finding. A project whose audits simply have not been run yet gets
                // amber — the chip is a nudge, and a red one on a healthy day-one project teaches the user to
                // ignore it.
                state: snapshot.RequiredFindings > 0
                    ? MolcaHubActivityState.Error
                    : MolcaHubActivityState.Warning,
                workspaceId: OnboardingWorkspaceProvider.WorkspaceId,
                onDismiss: Dismiss,
                order: -100,
                // Counts of unmet setup items in the user's own project, with no author-supplied free text:
                // the status string is composed here from integers Core produced.
                remoteSafe: true);
        }

        /// <inheritdoc/>
        public override void Dispose() => MolcaOnboardingChecklist.Changed -= OnChecklistChanged;

        private void Dismiss()
        {
            MolcaEditorPrefs.SetBool(DismissedPrefKey, true);
            NotifyChanged();
        }

        private void OnChecklistChanged() => NotifyChanged();
    }
}
