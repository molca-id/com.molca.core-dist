using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.Remediation.Hub
{
    /// <summary>
    /// The remediation workspace's run state, held statically so a plan or report survives a Hub tab switch.
    /// </summary>
    /// <remarks>
    /// Mirrors the references workspace's <c>ReferenceHubSession</c>: the view is rebuilt on every tab
    /// selection, so any state living on the view would be silently discarded mid-review — including the
    /// declined list a user is working through.
    /// </remarks>
    public static class RemediationHubSession
    {
        private static readonly Dictionary<string, MolcaRemediationPlan> _plans =
            new Dictionary<string, MolcaRemediationPlan>(StringComparer.Ordinal);

        private static readonly Dictionary<string, MolcaRemediationReport> _reports =
            new Dictionary<string, MolcaRemediationReport>(StringComparer.Ordinal);

        /// <summary>Raised whenever a plan or report changes, so an open view can repaint.</summary>
        public static event Action Changed;

        /// <summary>The most recent plan for a domain, or <c>null</c>.</summary>
        /// <param name="domainId">The domain id.</param>
        /// <returns>The plan, or <c>null</c>.</returns>
        public static MolcaRemediationPlan PlanFor(string domainId) =>
            _plans.TryGetValue(domainId ?? string.Empty, out var plan) ? plan : null;

        /// <summary>The most recent report for a domain, or <c>null</c>.</summary>
        /// <param name="domainId">The domain id.</param>
        /// <returns>The report, or <c>null</c>.</returns>
        public static MolcaRemediationReport ReportFor(string domainId) =>
            _reports.TryGetValue(domainId ?? string.Empty, out var report) ? report : null;

        /// <summary>Every domain that currently has a plan or a report.</summary>
        public static IEnumerable<string> KnownDomains => _plans.Keys.Concat(_reports.Keys).Distinct();

        /// <summary>
        /// Runs a read-only preview for a domain and stores it.
        /// </summary>
        /// <param name="domain">The domain to plan against.</param>
        /// <param name="policy">The policy to plan under.</param>
        /// <returns>The plan, or <c>null</c> when planning threw (the failure is logged).</returns>
        public static MolcaRemediationPlan Plan(MolcaRemediationDomain domain, RemediationPolicy policy)
        {
            if (domain == null) return null;
            try
            {
                var plan = MolcaRemediationPass.Plan(domain.CreateRequest(policy));
                _plans[domain.Id] = plan;
                // A stale report next to a fresh plan reads as if the plan had already been applied.
                _reports.Remove(domain.Id);
                Changed?.Invoke();
                return plan;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[Remediation] Planning '{domain.Id}' failed: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Applies a domain's remediation and stores the report.
        /// </summary>
        /// <param name="domain">The domain to remediate.</param>
        /// <param name="policy">Which fixes may auto-apply.</param>
        /// <param name="fixIdFilter">Restricts the pass to these fix ids; <c>null</c> means all the policy allows.</param>
        /// <returns>The report, or <c>null</c> when the pass threw (the failure is logged).</returns>
        public static MolcaRemediationReport Apply(
            MolcaRemediationDomain domain,
            RemediationPolicy policy,
            IReadOnlyCollection<string> fixIdFilter = null)
        {
            if (domain == null) return null;
            try
            {
                var request = domain.CreateRequest(policy);
                if (fixIdFilter != null) request.FixIdFilter = fixIdFilter;

                var report = MolcaRemediationPass.Apply(request);
                _reports[domain.Id] = report;
                // The plan described the project before the pass; keeping it would invite a second apply
                // against state that no longer exists.
                _plans.Remove(domain.Id);
                Changed?.Invoke();
                return report;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[Remediation] Applying '{domain.Id}' failed: {ex}");
                return null;
            }
        }

        /// <summary>Clears all stored plans and reports.</summary>
        public static void Clear()
        {
            _plans.Clear();
            _reports.Clear();
            Changed?.Invoke();
        }
    }
}
