using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Hub;
using Molca.Editor.Remediation;
using Molca.Editor.Remediation.Hub;

namespace Molca.Editor.Onboarding.Sources
{
    /// <summary>
    /// Projects every registered remediation domain into the onboarding checklist as the
    /// <see cref="MolcaOnboardingSeverity.Required"/> half.
    /// </summary>
    /// <remarks>
    /// <para><b>Why these are Required.</b> An audit engine emits a finding only for something it asserts the
    /// project got wrong. That is a fact about the project, not a suggestion, so it outranks every opinion in
    /// the list — and it is the only kind of row allowed to.</para>
    /// <para><b>Why the audit does not run here.</b> A domain sweep is a project-wide scan; running one per
    /// domain on every refresh would make opening the checklist the most expensive thing in the editor, and
    /// the Remediation workspace next door already treats planning as an explicit act. So a row reports what
    /// the current <see cref="RemediationHubSession"/> knows — the plan or report the user last ran — and is
    /// honest about knowing nothing when nothing has run. "Not checked yet" is outstanding work, because it
    /// is; it is just not the same claim as "broken".</para>
    /// <para>Consequently no row here can fix anything either: its action opens the workspace that owns the
    /// pass, its policy gating, and its review step.</para>
    /// </remarks>
    internal sealed class RemediationOnboardingSource : IMolcaOnboardingItemProvider
    {
        /// <inheritdoc/>
        public IEnumerable<MolcaOnboardingItem> GetItems() =>
            MolcaRemediationDomains.All.Select(ToItem).ToList();

        private static MolcaOnboardingItem ToItem(MolcaRemediationDomain domain)
        {
            var domainId = domain.Id;
            var label = domain.Label;

            return new MolcaOnboardingItem(
                id: "onboarding.remediation." + domainId,
                title: label,
                summary: $"Audits the project's {label.ToLowerInvariant()} configuration and repairs what has "
                         + "a single correct answer.",
                check: () => CheckDomain(domainId),
                severity: MolcaOnboardingSeverity.Required,
                order: domain.Order,
                actionLabel: "Open Remediation",
                act: () => MolcaHubWindow.OpenWorkspace(RemediationWorkspaceProvider.WorkspaceId),
                why: "A finding here is something Molca asserts is wrong, not a suggestion — the project may "
                     + "not start correctly until it is resolved.",
                docId: "REMEDIATION");
        }

        /// <summary>
        /// Reports a domain's state from the session's last plan or report, without auditing.
        /// </summary>
        /// <remarks>
        /// Re-resolved by id on every call: the domain set is rebuilt on recompile, and a row holding a stale
        /// descriptor would keep reporting on a domain that no longer exists.
        /// </remarks>
        private static MolcaOnboardingCheck CheckDomain(string domainId)
        {
            var domain = MolcaRemediationDomains.ById(domainId);
            if (domain == null)
                return MolcaOnboardingCheck.NotApplicable("This audit domain is no longer registered.");

            var report = RemediationHubSession.ReportFor(domainId);
            if (report != null) return FromReport(report);

            var plan = RemediationHubSession.PlanFor(domainId);
            if (plan != null)
                return plan.TotalFindings == 0
                    ? MolcaOnboardingCheck.Done("The last check found nothing.")
                    : MolcaOnboardingCheck.Todo(
                        $"{plan.TotalFindings} finding(s) from the last check — review and apply.");

            // Blocked, not Todo. Both are outstanding, but only Todo claims the project got something
            // wrong — and a project nobody has audited yet has not been accused of anything. Rendering an
            // unaudited domain in the same red as a real finding is how six honest rows read as six faults
            // on a healthy new project.
            return MolcaOnboardingCheck.Blocked("Not checked yet — run the audit from Remediation.");
        }

        private static MolcaOnboardingCheck FromReport(MolcaRemediationReport report)
        {
            // A refused or failed pass is not a clean bill of health, and a report with zero declined
            // findings from a pass that never looked would read as exactly that.
            if (report.RefusedStaleSnapshot)
                return MolcaOnboardingCheck.Blocked(
                    "The last pass refused a stale snapshot — re-run the audit.");

            if (!string.IsNullOrEmpty(report.AuditError))
                return MolcaOnboardingCheck.Blocked($"The last audit failed: {report.AuditError}");

            if (report.Declined.Count > 0 || report.HitIterationCap)
                return MolcaOnboardingCheck.Todo(report.Summarize());

            return MolcaOnboardingCheck.Done(
                report.Applied.Count == 0
                    ? "Nothing to repair."
                    : $"{report.Applied.Count} repaired · nothing left.");
        }
    }
}
