using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Remediation
{
    /// <summary>
    /// The one driver every "Fix Safe Issues" affordance runs: plan → apply → re-audit to a fixpoint →
    /// report, for any Molca audit domain.
    /// </summary>
    /// <remarks>
    /// <para><b>Invariants enforced here, not per domain:</b></para>
    /// <list type="bullet">
    /// <item>A pass is always an explicit action — nothing in this class is called from a scan, a workspace
    /// open, an Inspector draw or a build gate.</item>
    /// <item><see cref="Plan"/> mutates nothing: every candidate fix runs in dry-run.</item>
    /// <item>One Unity Undo group per pass, spanning every iteration, so "it just works" has a matching
    /// single "undo that".</item>
    /// <item>A stale snapshot is refused outright; an incomplete one proceeds with the gap reported.</item>
    /// <item>Every finding the pass saw is accounted for exactly once — applied, or declined with a reason.</item>
    /// <item>Termination is guaranteed: a finding site is attempted at most once per pass, and
    /// <see cref="MaxIterations"/> is a loud backstop rather than a silent truncation.</item>
    /// </list>
    /// <para>Editor-only; main thread only.</para>
    /// </remarks>
    public static class MolcaRemediationPass
    {
        /// <summary>
        /// Maximum audit → fix iterations before the pass gives up and reports non-convergence. Fixing one
        /// finding can expose another, so a pass re-audits; a fix pair that re-creates each other's findings
        /// must fail loudly instead of spinning.
        /// </summary>
        public const int MaxIterations = 4;

        /// <summary>
        /// Previews what a pass would do without changing anything: every eligible fix is invoked in dry-run,
        /// and every other finding is recorded with the reason it would be declined.
        /// </summary>
        /// <param name="request">The domain, policy and audit projection to plan against.</param>
        /// <returns>The plan; empty-but-valid when the domain reports no findings.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="request"/> or its audit is null.</exception>
        public static MolcaRemediationPlan Plan(MolcaRemediationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Audit == null) throw new ArgumentNullException(nameof(request.Audit));

            var projection = request.Audit();
            var fixable = new List<MolcaPlannedFix>();
            var declined = new List<MolcaDeclinedFinding>();

            foreach (var target in projection.Targets)
            {
                if (target == null) continue;

                if (projection.IsStale)
                {
                    declined.Add(new MolcaDeclinedFinding(target, MolcaDeclineReason.BlockedByInvariant,
                        "The domain snapshot is stale (a scanned category failed); re-run the audit before fixing."));
                    continue;
                }

                var fix = MolcaFixRegistry.SelectFor(target, request.Policy, request.FixIdFilter);
                if (fix == null)
                {
                    declined.Add(Explain(target, request));
                    continue;
                }

                var outcome = Invoke(fix, target, dryRun: true, request, out var failure);
                if (failure != null) { declined.Add(failure); continue; }

                if (outcome.Applied) fixable.Add(new MolcaPlannedFix(target, fix, outcome));
                else declined.Add(new MolcaDeclinedFinding(target, MolcaDeclineReason.FixReportedNotApplied,
                    outcome.Message, fix.Id));
            }

            return new MolcaRemediationPlan(
                request.Domain, request.Policy, fixable, declined, projection.CoverageNote);
        }

        /// <summary>
        /// Runs the pass: applies every eligible fix in one Undo group, re-auditing after each round until no
        /// newly-eligible finding remains, then reports what was applied and what still needs a human.
        /// </summary>
        /// <param name="request">The domain, policy and audit projection to remediate.</param>
        /// <returns>The report; every finding the pass saw appears exactly once.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="request"/> or its audit is null.</exception>
        public static MolcaRemediationReport Apply(MolcaRemediationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Audit == null) throw new ArgumentNullException(nameof(request.Audit));

            var report = new MolcaRemediationReport(request.Domain, request.Policy);

            MolcaAuditProjection initial;
            try
            {
                initial = request.Audit();
            }
            catch (Exception ex)
            {
                // Nothing has been touched yet, so this is simply "the domain could not be read".
                report.AuditError = ex.Message;
                Debug.LogError($"[MolcaRemediationPass] Audit for '{request.Domain}' threw: {ex}");
                return report;
            }

            report.CoverageNote = initial.CoverageNote;

            if (initial.IsStale)
            {
                // Mirrors the reference repair executor: a snapshot whose run could not finish a category it
                // attempted is not a basis for mutation. Report every finding as blocked, change nothing.
                report.RefusedStaleSnapshot = true;
                foreach (var target in initial.Targets.Where(t => t != null))
                    report.AddDeclined(new MolcaDeclinedFinding(target, MolcaDeclineReason.BlockedByInvariant,
                        "The domain snapshot is stale (a scanned category failed); re-run the audit before fixing."));
                return report;
            }

            int undoGroup = Undo.GetCurrentGroup();
            var undoGroupName = request.UndoGroupName ?? $"Molca remediation: {request.Domain}";

            // A site is attempted at most once per pass. This is what guarantees termination — the iteration
            // cap below is a backstop for a fix that re-creates a *differently keyed* finding, not the
            // primary defence.
            var attempted = new HashSet<string>(StringComparer.Ordinal);
            var projection = initial;
            var iteration = 0;

            try
            {
                while (iteration < MaxIterations)
                {
                    iteration++;

                    var pending = projection.Targets
                        .Where(t => t != null && !attempted.Contains(t.Signature))
                        .ToList();
                    if (pending.Count == 0) break;

                    var appliedThisRound = 0;
                    foreach (var target in pending)
                    {
                        // Checked before marking the site attempted, so a cancelled finding is reported as
                        // cancelled rather than as a fix that ran and achieved nothing.
                        if (request.CancellationToken.IsCancellationRequested) break;
                        attempted.Add(target.Signature);

                        var fix = MolcaFixRegistry.SelectFor(target, request.Policy, request.FixIdFilter);
                        if (fix == null) continue; // Accounted for by the final reconciliation pass.

                        var outcome = Invoke(fix, target, dryRun: false, request, out var failure);
                        if (failure != null) { report.AddDeclined(failure); continue; }

                        if (!outcome.Applied) continue; // Reconciliation records the fix's own message.

                        report.AddApplied(new MolcaPlannedFix(target, fix, outcome));
                        appliedThisRound++;
                    }

                    if (request.CancellationToken.IsCancellationRequested) break;
                    if (appliedThisRound == 0) break;

                    // Something changed: re-audit so a finding this round exposed can be fixed too. A throw
                    // here must not escape — fixes have already been applied, and losing the report would
                    // leave the user with a mutated project and no record of what changed.
                    try
                    {
                        projection = request.Audit();
                    }
                    catch (Exception ex)
                    {
                        report.AuditError = ex.Message;
                        Debug.LogError(
                            $"[MolcaRemediationPass] Re-audit for '{request.Domain}' threw after "
                            + $"{report.Applied.Count} fix(es) were applied: {ex}");
                        break;
                    }

                    report.CoverageNote = projection.CoverageNote;
                    if (projection.IsStale) break;
                }
            }
            finally
            {
                // Named here rather than before the loop: every domain editing service sets its own group
                // name as it writes, so a name set up front is overwritten by whichever fix happened to run
                // last. The user would then find one undo entry for a whole-domain pass labelled after a
                // single fix. Setting it immediately before collapsing makes the label describe the group.
                Undo.SetCurrentGroupName(undoGroupName);
                Undo.CollapseUndoOperations(undoGroup);
            }

            report.Iterations = iteration;
            Reconcile(request, report, attempted, projection);
            return report;
        }

        /// <summary>
        /// Accounts for every finding the pass did not repair. Runs one final audit so the declined list
        /// reflects reality after the mutations, then records a reason for each remaining finding.
        /// </summary>
        private static void Reconcile(
            MolcaRemediationRequest request,
            MolcaRemediationReport report,
            HashSet<string> attempted,
            MolcaAuditProjection lastProjection)
        {
            MolcaAuditProjection final;
            try
            {
                final = report.Applied.Count > 0 ? request.Audit() : lastProjection;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MolcaRemediationPass] Final audit for '{request.Domain}' threw: {ex}");
                final = lastProjection;
            }

            report.CoverageNote = final.CoverageNote;

            var appliedSignatures = new HashSet<string>(
                report.Applied.Select(a => a.Target.Signature), StringComparer.Ordinal);
            var declinedSignatures = new HashSet<string>(
                report.Declined.Select(d => d.Target.Signature), StringComparer.Ordinal);

            var stillEligible = new List<string>();

            foreach (var target in final.Targets.Where(t => t != null))
            {
                if (appliedSignatures.Contains(target.Signature)) continue;
                if (!declinedSignatures.Add(target.Signature)) continue;

                if (request.CancellationToken.IsCancellationRequested
                    && !attempted.Contains(target.Signature))
                {
                    report.AddDeclined(new MolcaDeclinedFinding(target, MolcaDeclineReason.Cancelled,
                        "The pass was cancelled before this finding was reached."));
                    continue;
                }

                var fix = MolcaFixRegistry.SelectFor(target, request.Policy, request.FixIdFilter);
                if (fix != null)
                {
                    // A fix was eligible yet the finding survives. Either it ran and reported no change, or
                    // the pass never got to it because the fixpoint loop ran out of iterations. Both must be
                    // visible, and they are different problems.
                    if (attempted.Contains(target.Signature))
                        report.AddDeclined(new MolcaDeclinedFinding(
                            target, MolcaDeclineReason.FixReportedNotApplied,
                            "The fix ran but the finding persists — it reported no change, or the change did "
                            + "not resolve it.", fix.Id));
                    else
                        report.AddDeclined(new MolcaDeclinedFinding(
                            target, MolcaDeclineReason.NotConverged,
                            "The pass stopped before reaching this finding; re-run to continue.", fix.Id));

                    stillEligible.Add(target.FindingCode);
                    continue;
                }

                report.AddDeclined(Explain(target, request));
            }

            if (report.Iterations >= MaxIterations && stillEligible.Count > 0)
            {
                report.HitIterationCap = true;
                report.UnconvergedCodes = stillEligible.Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
                Debug.LogWarning(
                    $"[MolcaRemediationPass] '{request.Domain}' did not converge after {MaxIterations} "
                    + $"iterations; still-eligible codes: {string.Join(", ", report.UnconvergedCodes)}. "
                    + "This usually means two fixes re-create each other's findings.");
            }
        }

        /// <summary>
        /// Builds the declined entry for a target no fix was selected for, distinguishing "no fix exists"
        /// from "a fix exists but this policy/filter excludes it" — the difference between a judgment finding
        /// and one click away.
        /// </summary>
        private static MolcaDeclinedFinding Explain(MolcaFixTarget target, MolcaRemediationRequest request)
        {
            var candidates = MolcaFixRegistry.FixesFor(target.FindingCode);
            if (candidates.Count == 0)
                return new MolcaDeclinedFinding(target, MolcaDeclineReason.NoFixExists,
                    "No fix is registered for this finding code — it needs a human decision.");

            var nonDeterministic = candidates.FirstOrDefault(f => !f.IsDeterministic);
            var policyBlocked = candidates.FirstOrDefault(
                f => f.IsDeterministic && !MolcaFixRegistry.PolicyAllows(request.Policy, f));

            if (policyBlocked != null)
                return new MolcaDeclinedFinding(target, MolcaDeclineReason.PolicyExcluded,
                    $"'{policyBlocked.Id}' is excluded by policy {request.Policy} "
                    + $"(destructive: {policyBlocked.IsDestructive}, reverts by: {policyBlocked.Reversibility}). "
                    + "Review it explicitly to apply.",
                    policyBlocked.Id);

            if (nonDeterministic != null)
                return new MolcaDeclinedFinding(target, MolcaDeclineReason.NotDeterministic,
                    $"'{nonDeterministic.Id}' needs input, so a blanket pass cannot run it: "
                    + nonDeterministic.Description,
                    nonDeterministic.Id);

            var filtered = candidates.FirstOrDefault(
                f => request.FixIdFilter != null && !request.FixIdFilter.Contains(f.Id));
            if (filtered != null)
                return new MolcaDeclinedFinding(target, MolcaDeclineReason.NotRequested,
                    $"'{filtered.Id}' was not among the requested fixes.", filtered.Id);

            return new MolcaDeclinedFinding(target, MolcaDeclineReason.NoFixExists,
                "No registered fix was applicable to this finding.");
        }

        /// <summary>
        /// Invokes a fix, converting a thrown exception into a declined entry rather than letting it abort the
        /// pass. A fix that throws is never silently skipped — the message reaches the report.
        /// </summary>
        private static MolcaFixOutcome Invoke(
            IMolcaFix fix,
            MolcaFixTarget target,
            bool dryRun,
            MolcaRemediationRequest request,
            out MolcaDeclinedFinding failure)
        {
            failure = null;
            try
            {
                return fix.Apply(target, dryRun, null, request.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                failure = new MolcaDeclinedFinding(target, MolcaDeclineReason.Cancelled,
                    "Cancelled while applying.", fix.Id);
                return MolcaFixOutcome.NotApplied("cancelled");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MolcaRemediationPass] Fix '{fix.Id}' threw on '{target.FindingCode}' "
                               + $"({target.Path}): {ex}");
                failure = new MolcaDeclinedFinding(target, MolcaDeclineReason.FixThrew, ex.Message, fix.Id);
                return MolcaFixOutcome.NotApplied(ex.Message);
            }
        }
    }
}
