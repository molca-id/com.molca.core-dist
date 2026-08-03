using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Starter;
using UnityEngine;

namespace Molca.Editor.Onboarding.Sources
{
    /// <summary>
    /// Projects the project starter's steps into the onboarding checklist as the
    /// <see cref="MolcaOnboardingSeverity.Recommended"/> half.
    /// </summary>
    /// <remarks>
    /// <para>Every row here is an opinion — "this is what a fully-featured Molca project looks like" — and is
    /// labelled as one. Nothing in this source may ever be promoted to
    /// <see cref="MolcaOnboardingSeverity.Required"/>: doing so would need an audit to report that the
    /// project could enable more things, which is exactly the manufactured finding
    /// <see cref="IMolcaStarterStep"/> exists to avoid.</para>
    /// <para>The source adds no steps of its own. A layer that wants a row here ships an
    /// <see cref="IMolcaStarterStep"/>, which <see cref="MolcaStarter"/> already discovers.</para>
    /// </remarks>
    internal sealed class StarterOnboardingSource : IMolcaOnboardingItemProvider
    {
        /// <inheritdoc/>
        public IEnumerable<MolcaOnboardingItem> GetItems() =>
            MolcaStarter.Steps.Select(ToItem).ToList();

        private static MolcaOnboardingItem ToItem(IMolcaStarterStep step)
        {
            var id = step.Id;

            return new MolcaOnboardingItem(
                id: "onboarding." + id,
                title: step.Title,
                summary: step.Description,
                check: () => CheckStep(id),
                severity: MolcaOnboardingSeverity.Recommended,
                // Offset past the audit rows so the recommended block keeps the starter's own dependency
                // order (GlobalSettings before the modules that register into it) within itself.
                order: 1000 + step.Order,
                actionLabel: "Set Up",
                act: () => RunStep(id),
                docId: "GETTING_STARTED");
        }

        /// <summary>
        /// Reports where one step stands, re-resolving it by id so a recompile that replaces the step
        /// instance cannot leave the row bound to a stale object.
        /// </summary>
        /// <remarks>
        /// Uses the step's own dry run for the detail line: it is contractually read-only, and it is the only
        /// thing that can say <em>what</em> is missing rather than merely that something is.
        /// </remarks>
        private static MolcaOnboardingCheck CheckStep(string id)
        {
            var step = Find(id);
            if (step == null)
                return MolcaOnboardingCheck.NotApplicable("This step is no longer registered.");

            if (step.IsSatisfied())
                return MolcaOnboardingCheck.Done("Already set up.");

            var preview = step.Apply(dryRun: true, default);
            return preview.Changed
                ? MolcaOnboardingCheck.Todo(preview.Message)
                // A step that is unsatisfied yet would change nothing is waiting on something it cannot do
                // itself — a RuntimeManager prefab that does not exist yet, for instance.
                : MolcaOnboardingCheck.Blocked(preview.Message);
        }

        private static void RunStep(string id)
        {
            var report = MolcaStarter.InstallStep(id);
            Debug.Log($"[Molca Onboarding] {id}: {report.Summarize()}");
        }

        private static IMolcaStarterStep Find(string id) =>
            MolcaStarter.Steps.FirstOrDefault(s => s.Id == id);
    }
}
