using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using UnityEngine.UIElements;

namespace Molca.Editor.Remediation.Hub
{
    /// <summary>Presentation-only shape for one remediation finding.</summary>
    /// <remarks>
    /// Kept separate from the audit model so the Hub can group paths and suppress repeated generic review
    /// reasons without losing any of the finding's actionable context.
    /// </remarks>
    internal sealed class RemediationWorkspaceRow
    {
        internal RemediationWorkspaceRow(
            string code, string path, string context, string detail = null, string reviewReason = null)
        {
            Code = code ?? string.Empty;
            Path = string.IsNullOrWhiteSpace(path) ? "(project)" : path;
            Context = string.IsNullOrWhiteSpace(context) ? "No additional context was provided." : context;
            Detail = detail;
            ReviewReason = reviewReason;
        }

        internal string Code { get; }
        internal string Path { get; }
        internal string Context { get; }
        internal string Detail { get; }
        internal string ReviewReason { get; }

        internal string DisplayDetail => string.IsNullOrWhiteSpace(Detail)
            ? Context
            : $"{Context}\n  {Detail}";

        internal bool Matches(string filter) =>
            string.IsNullOrWhiteSpace(filter)
            || Contains(Code, filter)
            || Contains(Path, filter)
            || Contains(Context, filter)
            || Contains(Detail, filter)
            || Contains(ReviewReason, filter);

        private static bool Contains(string value, string filter) =>
            (value ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// The Hub surface behind "Fix Safe Issues": one row per project-wide audit domain, each showing what a
    /// safe pass would repair and — after a run — exactly what it left and why.
    /// </summary>
    /// <remarks>
    /// <para>The declined list is the product, not a footnote. A pass that repairs 4 of 12 findings and shows
    /// only a tick is worse than no pass, so this view always accounts for the remainder.</para>
    /// <para><b>Long lists.</b> A real project can produce hundreds of findings, and a flat expanded list of
    /// them is unreadable and slow to build. Findings are grouped by code, then by asset. Repeated groups
    /// start collapsed with exact finding/asset counts and a review reason on the header; expanding shows
    /// each finding's property and originating message. Exact duplicate contexts collapse to one line with a
    /// multiplier. Expand/Collapse all and filtering remain available, and filtering opens its matches.</para>
    /// <para>Opening the workspace runs nothing. Every audit here is read-only, but even a read-only scan can
    /// open scenes, so it happens on an explicit click.</para>
    /// <para>Editor-only; main thread.</para>
    /// </remarks>
    public sealed class RemediationWorkspaceView : VisualElement
    {
        /// <summary>Rows rendered inside one group before the rest are summarised.</summary>
        /// <remarks>
        /// A cap rather than a scroll: building a thousand labels costs real time in UI Toolkit, and nobody
        /// reads the four hundredth. The overflow line states how many were withheld, so the cap never reads
        /// as "that was all of them".
        /// </remarks>
        private const int MaxRowsPerGroup = 25;

        /// <summary>Total rows in a section below which singleton groups start expanded.</summary>
        private const int AutoExpandThreshold = 12;

        private readonly VisualElement _domainList;
        private readonly MolcaWorkspaceHeader _header;
        private readonly List<Foldout> _foldouts = new List<Foldout>();
        private string _filter = string.Empty;

        /// <summary>Builds the view.</summary>
        public RemediationWorkspaceView()
        {
            // The Hub root already carries the design language, but a hostable view must not depend on its
            // host for tokens — the editor design language explicitly allows the same VisualElement to be
            // hosted standalone. Apply is idempotent, so doing it here costs nothing inside the Hub.
            MolcaEditorUi.Apply(this);
            AddToClassList("molca-workspace");

            _header = BuildHeader();
            Add(_header);
            Add(BuildFilterRow());

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            _domainList = new VisualElement();
            _domainList.AddToClassList("molca-list");
            scroll.Add(_domainList);
            Add(scroll);

            RegisterCallback<AttachToPanelEvent>(_ => RemediationHubSession.Changed += Rebuild);
            RegisterCallback<DetachFromPanelEvent>(_ => RemediationHubSession.Changed -= Rebuild);

            Rebuild();
        }

        private MolcaWorkspaceHeader BuildHeader()
        {
            var header = new MolcaWorkspaceHeader("Remediation");
            var check = MolcaButtons.Toolbar("Check All", CheckAll);
            check.tooltip = "Runs every domain's read-only audit and previews what a safe pass would fix.";
            header.AddAction(check);

            var fix = MolcaButtons.Primary("Fix Safe Issues", FixAll);
            fix.tooltip = "Applies every unambiguously safe fix across all domains, one undo group per "
                          + "domain, then reports what still needs a decision.";
            header.AddAction(fix);

            return header;
        }

        private VisualElement BuildFilterRow()
        {
            var row = new MolcaWorkspaceToolbar();
            var filter = new MolcaSearchField(
                "Filter by code, asset, property, message, or review reason…");
            filter.OnSearchChanged += value =>
            {
                _filter = value ?? string.Empty;
                Rebuild();
            };
            row.Content.Add(filter);

            row.AddAction(MolcaButtons.Toolbar("Expand all", () => SetAllFoldouts(true)));
            row.AddAction(MolcaButtons.Toolbar("Collapse all", () => SetAllFoldouts(false)));

            return row;
        }

        private void SetAllFoldouts(bool expanded)
        {
            foreach (var foldout in _foldouts) foldout.value = expanded;
        }

        private void CheckAll()
        {
            foreach (var domain in MolcaRemediationDomains.All)
                RemediationHubSession.Plan(domain, RemediationPolicy.SafeOnly);
        }

        private void FixAll()
        {
            // Sequenced rather than batched: each domain keeps its own undo group and its own report
            // section, so a user can revert one domain's pass without unwinding the others.
            foreach (var domain in MolcaRemediationDomains.All)
                RemediationHubSession.Apply(domain, RemediationPolicy.SafeOnly);
        }

        private void Rebuild()
        {
            _domainList.Clear();
            _foldouts.Clear();

            var domains = MolcaRemediationDomains.All;
            if (domains.Count == 0)
            {
                var empty = Muted("No remediation domains are registered in this project.");
                empty.AddToClassList("molca-empty-state");
                _domainList.Add(empty);
                _header.SetSummary(string.Empty);
                return;
            }

            int fixable = 0, needsReview = 0, applied = 0;
            foreach (var domain in domains)
            {
                _domainList.Add(BuildDomainRow(domain));

                var plan = RemediationHubSession.PlanFor(domain.Id);
                if (plan != null) { fixable += plan.Fixable.Count; needsReview += plan.Declined.Count; }

                var report = RemediationHubSession.ReportFor(domain.Id);
                if (report != null) { applied += report.Applied.Count; needsReview += report.Declined.Count; }
            }

            _header.SetSummary(applied > 0
                ? $"{applied} applied · {needsReview} need review"
                : fixable + needsReview > 0
                    ? $"{fixable} fixable · {needsReview} need review"
                    : "Nothing checked yet.");

            _domainList.Add(BuildReferencesNote());
        }

        private VisualElement BuildDomainRow(MolcaRemediationDomain domain)
        {
            var box = new MolcaListGroup(
                domain.Label,
                DescribeStatus(domain),
                StatusOf(domain),
                StatusText(domain));

            var check = MolcaButtons.Mini("Check",
                () => RemediationHubSession.Plan(domain, RemediationPolicy.SafeOnly));
            check.tooltip = "Runs this domain's read-only audit and previews the safe pass.";
            box.AddHeaderAction(check);
            box.AddHeaderAction(MolcaButtons.Mini("Fix Safe Issues",
                () => RemediationHubSession.Apply(domain, RemediationPolicy.SafeOnly)));

            var body = box.Body;

            var plan = RemediationHubSession.PlanFor(domain.Id);
            var report = RemediationHubSession.ReportFor(domain.Id);

            var coverage = plan?.CoverageNote ?? report?.CoverageNote;
            if (!string.IsNullOrEmpty(coverage)) body.Add(Muted($"Coverage: {coverage}"));

            if (report != null && report.RefusedStaleSnapshot)
                body.Add(Muted("Refused: the audit is stale. Re-run it before fixing."));

            if (report != null && report.HitIterationCap)
                body.Add(Muted(
                    "Did not converge — two fixes appear to re-create each other's findings: "
                    + string.Join(", ", report.UnconvergedCodes)));

            if (plan != null)
                AddGroupedRows(body, "Would fix", plan.Fixable.Select(Row));
            if (report != null)
                AddGroupedRows(body, "Applied", report.Applied.Select(Row));

            var declined = report?.Declined ?? plan?.Declined;
            if (declined != null)
                AddGroupedRows(body, "Needs your decision", declined.Select(Row));

            if (plan != null || report != null)
            {
                var yellow = OtherFixesFor(domain);
                if (yellow.Count > 0) body.Add(BuildOtherFixes(domain, yellow));
            }

            return box;
        }

        /// <summary>
        /// Renders a section as finding-code foldouts containing asset groups. The asset path and generic
        /// review reason are each shown once; the individual lines retain the finding message and property.
        /// </summary>
        private void AddGroupedRows(
            VisualElement parent, string title, IEnumerable<RemediationWorkspaceRow> rows)
        {
            var all = rows.Where(row => row.Matches(_filter)).ToList();
            if (all.Count == 0) return;

            var groups = all
                .GroupBy(r => r.Code, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            var section = new Foldout { text = $"{title} ({all.Count})", value = true };
            _foldouts.Add(section);

            // A repeated cause is already summarised by its header. Keep it collapsed until requested;
            // a filter is different because hiding its matches would make search feel broken.
            var hasFilter = !string.IsNullOrWhiteSpace(_filter);

            foreach (var group in groups)
            {
                var groupRows = group.ToList();
                var assetCount = groupRows
                    .Select(row => row.Path)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                var reason = SummarizeReviewReasons(groupRows);
                var foldout = new Foldout
                {
                    text = GroupHeader(group.Key, groupRows.Count, assetCount, reason),
                    value = ShouldExpandGroup(all.Count, groupRows.Count, hasFilter),
                };
                foldout.AddToClassList("molca-list-nested");
                _foldouts.Add(foldout);

                int rendered = 0;
                foreach (var asset in groupRows
                             .GroupBy(row => row.Path, StringComparer.Ordinal)
                             .OrderByDescending(asset => asset.Count())
                             .ThenBy(asset => asset.Key, StringComparer.Ordinal))
                {
                    if (rendered >= MaxRowsPerGroup) break;

                    var assetRows = asset.ToList();
                    var assetHeader = new Label(AssetHeader(asset.Key, assetRows.Count));
                    assetHeader.AddToClassList("molca-list-detail-heading");
                    foldout.Add(assetHeader);

                    foreach (var context in assetRows
                                 .GroupBy(row => row.DisplayDetail, StringComparer.Ordinal)
                                 .OrderByDescending(context => context.Count())
                                 .ThenBy(context => context.Key, StringComparer.Ordinal))
                    {
                        if (rendered >= MaxRowsPerGroup) break;

                        var duplicateCount = context.Count();
                        var suffix = duplicateCount > 1 ? $" (×{duplicateCount})" : string.Empty;
                        var detail = new Label("• " + context.Key + suffix);
                        detail.AddToClassList("molca-list-detail-text");
                        foldout.Add(detail);
                        rendered += duplicateCount;
                    }
                }

                if (rendered < groupRows.Count)
                    foldout.Add(Muted($"… and {groupRows.Count - rendered} more findings with the same cause."));

                section.Add(foldout);
            }

            parent.Add(section);
        }

        internal static bool ShouldExpandGroup(int sectionRows, int groupRows, bool hasFilter) =>
            hasFilter || (sectionRows <= AutoExpandThreshold && groupRows == 1);

        internal static string GroupHeader(string code, int findings, int assets, string reason)
        {
            var header = $"{code} ({findings} {Plural(findings, "finding", "findings")} · "
                         + $"{assets} {Plural(assets, "asset", "assets")})";
            return string.IsNullOrWhiteSpace(reason) ? header : header + " · " + reason;
        }

        private static string AssetHeader(string path, int findings) => findings == 1
            ? path
            : $"{path} ({findings} findings)";

        private static string Plural(int count, string singular, string plural) =>
            count == 1 ? singular : plural;

        private static string SummarizeReviewReasons(IReadOnlyCollection<RemediationWorkspaceRow> rows)
        {
            var reasons = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ReviewReason))
                .GroupBy(row => row.ReviewReason, StringComparer.Ordinal)
                .OrderByDescending(reason => reason.Count())
                .ThenBy(reason => reason.Key, StringComparer.Ordinal)
                .ToList();

            if (reasons.Count == 0) return null;
            if (reasons.Count == 1) return reasons[0].Key;
            return $"{reasons.Count} review reasons";
        }

        /// <summary>
        /// The reviewed opt-in path: fixes a wider policy would allow, each individually checkable, applied
        /// only on an explicit click. Nothing here is ever swept.
        /// </summary>
        private VisualElement BuildOtherFixes(MolcaRemediationDomain domain, IReadOnlyList<IMolcaFix> fixes)
        {
            var foldout = new Foldout { text = $"Review other fixes ({fixes.Count})", value = false };
            _foldouts.Add(foldout);
            var chosen = new HashSet<string>();

            foreach (var fix in fixes)
            {
                var toggle = new Toggle(fix.Description)
                {
                    value = false,
                    tooltip = $"{fix.Id} — reverts by {fix.Reversibility}"
                              + (fix.IsDestructive ? ", discards authored data" : string.Empty),
                };
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue) chosen.Add(fix.Id); else chosen.Remove(fix.Id);
                });
                foldout.Add(toggle);
            }

            foldout.Add(new Button(() =>
            {
                if (chosen.Count == 0) return;
                RemediationHubSession.Apply(
                    domain, RemediationPolicy.DeterministicReversible, chosen.ToList());
            })
            {
                text = "Apply checked",
                tooltip = "Applies only the checked fixes. Destructive and file-rewriting fixes live here.",
            });

            return foldout;
        }

        /// <summary>
        /// Fixes registered for this domain's declined findings that a wider policy would permit — the
        /// Yellow set. Derived from what was actually declined, so the list is never hypothetical.
        /// </summary>
        private static IReadOnlyList<IMolcaFix> OtherFixesFor(MolcaRemediationDomain domain)
        {
            var declined = RemediationHubSession.ReportFor(domain.Id)?.Declined
                           ?? RemediationHubSession.PlanFor(domain.Id)?.Declined;
            if (declined == null) return Array.Empty<IMolcaFix>();

            return declined
                .Where(d => d.Reason == MolcaDeclineReason.PolicyExcluded)
                .Select(d => MolcaFixRegistry.ById(d.FixId))
                .Where(f => f != null)
                .GroupBy(f => f.Id)
                .Select(g => g.First())
                .OrderBy(f => f.Id, StringComparer.Ordinal)
                .ToList();
        }

        private static string DescribeStatus(MolcaRemediationDomain domain)
        {
            var report = RemediationHubSession.ReportFor(domain.Id);
            if (report != null) return report.Summarize();

            var plan = RemediationHubSession.PlanFor(domain.Id);
            if (plan == null) return "not checked";

            return plan.TotalFindings == 0
                ? "clean"
                : $"{plan.Fixable.Count} fixable · {plan.Declined.Count} need review";
        }

        private static MolcaStatusKind StatusOf(MolcaRemediationDomain domain)
        {
            var report = RemediationHubSession.ReportFor(domain.Id);
            if (report != null)
            {
                if (report.RefusedStaleSnapshot || report.HitIterationCap) return MolcaStatusKind.Error;
                if (report.Declined.Count > 0) return MolcaStatusKind.Warning;
                return MolcaStatusKind.Ok;
            }

            var plan = RemediationHubSession.PlanFor(domain.Id);
            if (plan == null) return MolcaStatusKind.Idle;
            if (plan.TotalFindings == 0) return MolcaStatusKind.Ok;
            return MolcaStatusKind.Warning;
        }

        private static string StatusText(MolcaRemediationDomain domain)
        {
            var status = StatusOf(domain);
            return status switch
            {
                MolcaStatusKind.Ok => "Clean",
                MolcaStatusKind.Warning => "Needs review",
                MolcaStatusKind.Error => "Blocked",
                _ => "Not checked",
            };
        }

        internal static RemediationWorkspaceRow Row(MolcaPlannedFix row) =>
            new RemediationWorkspaceRow(
                row.Target.FindingCode,
                row.Target.Path,
                FindingContext(row.Target),
                DistinctDetail(row.Outcome.Message, row.Target.Message));

        internal static RemediationWorkspaceRow Row(MolcaDeclinedFinding row) =>
            new RemediationWorkspaceRow(
                row.Target.FindingCode,
                row.Target.Path,
                FindingContext(row.Target),
                DeclineDetail(row),
                ReviewReason(row.Reason));

        private static string FindingContext(MolcaFixTarget target)
        {
            var property = string.IsNullOrWhiteSpace(target.PropertyPath)
                ? null
                : $"[{target.PropertyPath}]";
            if (string.IsNullOrWhiteSpace(property)) return target.Message;
            if (string.IsNullOrWhiteSpace(target.Message)) return property;
            return property + " " + target.Message;
        }

        private static string DeclineDetail(MolcaDeclinedFinding row)
        {
            // Explain() deliberately gives every judgment finding the same honest reason. It belongs on the
            // group header; repeating it beside every asset hides the finding-specific context users need.
            if (row.Reason == MolcaDeclineReason.NoFixExists && IsGenericNoFixDetail(row.Detail)) return null;
            return DistinctDetail(row.Detail, row.Target.Message, "Why not fixed: ");
        }

        private static bool IsGenericNoFixDetail(string detail) =>
            string.Equals(detail,
                "No fix is registered for this finding code — it needs a human decision.",
                StringComparison.Ordinal)
            || string.Equals(detail,
                "No registered fix was applicable to this finding.",
                StringComparison.Ordinal)
            || string.Equals(detail,
                "No automatic repair exists for this finding.",
                StringComparison.Ordinal);

        private static string DistinctDetail(string detail, string context, string prefix = null)
        {
            if (string.IsNullOrWhiteSpace(detail)
                || string.Equals(detail, context, StringComparison.Ordinal))
                return null;
            return (prefix ?? string.Empty) + detail;
        }

        private static string ReviewReason(MolcaDeclineReason reason)
        {
            switch (reason)
            {
                case MolcaDeclineReason.NoFixExists: return "no automatic fix";
                case MolcaDeclineReason.PolicyExcluded: return "outside safe policy";
                case MolcaDeclineReason.NotDeterministic: return "needs input";
                case MolcaDeclineReason.AmbiguousTarget: return "ambiguous target";
                case MolcaDeclineReason.BlockedByInvariant: return "blocked by invariant";
                case MolcaDeclineReason.FixReportedNotApplied: return "fix made no change";
                case MolcaDeclineReason.FixThrew: return "fix failed";
                case MolcaDeclineReason.Cancelled: return "cancelled";
                case MolcaDeclineReason.NotConverged: return "did not converge";
                case MolcaDeclineReason.NotRequested: return "not requested";
                default: return "needs review";
            }
        }

        private static Label Muted(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-list-note");
            return label;
        }

        private static VisualElement BuildReferencesNote()
        {
            var note = new VisualElement();
            note.AddToClassList("molca-list-note");
            note.Add(Muted(
                "Reference findings are repaired in Molca Hub → References. Their repair is a "
                + "revision-pinned transaction that refuses a plan built against a project that has moved on, "
                + "so it is approved there rather than swept from here."));
            return note;
        }
    }
}
