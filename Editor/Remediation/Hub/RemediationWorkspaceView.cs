using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Remediation.Hub
{
    /// <summary>
    /// The Hub surface behind "Fix Safe Issues": one row per project-wide audit domain, each showing what a
    /// safe pass would repair and — after a run — exactly what it left and why.
    /// </summary>
    /// <remarks>
    /// <para>The declined list is the product, not a footnote. A pass that repairs 4 of 12 findings and shows
    /// only a tick is worse than no pass, so this view always accounts for the remainder.</para>
    /// <para><b>Long lists.</b> A real project can produce hundreds of findings, and a flat expanded list of
    /// them is unreadable and slow to build. Findings are therefore grouped by code — the level at which they
    /// share a cause and a remedy — with the count on the group header, so "34 duplicate providers" is one
    /// line rather than thirty-four. Groups auto-expand only while the total is small enough to read at a
    /// glance; beyond that they start collapsed, with Expand/Collapse all and a filter to navigate. Honesty
    /// is preserved because the count and the reason are on the header, visible without expanding.</para>
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

        /// <summary>Total rows in a section below which every group starts expanded.</summary>
        private const int AutoExpandThreshold = 12;

        private readonly VisualElement _domainList;
        private readonly Label _summary;
        private readonly List<Foldout> _foldouts = new List<Foldout>();
        private string _filter = string.Empty;

        /// <summary>Builds the view.</summary>
        public RemediationWorkspaceView()
        {
            style.flexGrow = 1;
            style.paddingLeft = 8;
            style.paddingRight = 8;
            style.paddingTop = 8;

            Add(BuildHeader(out _summary));
            Add(BuildFilterRow());

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            _domainList = new VisualElement();
            scroll.Add(_domainList);
            Add(scroll);

            RegisterCallback<AttachToPanelEvent>(_ => RemediationHubSession.Changed += Rebuild);
            RegisterCallback<DetachFromPanelEvent>(_ => RemediationHubSession.Changed -= Rebuild);

            Rebuild();
        }

        private VisualElement BuildHeader(out Label summary)
        {
            var header = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 },
            };

            header.Add(new Label("Remediation")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 14, marginRight = 12 },
            });

            summary = new Label(string.Empty) { style = { flexGrow = 1, opacity = 0.8f } };
            header.Add(summary);

            header.Add(new Button(CheckAll)
            {
                text = "Check All",
                tooltip = "Runs every domain's read-only audit and previews what a safe pass would fix.",
            });
            header.Add(new Button(FixAll)
            {
                text = "Fix Safe Issues (All)",
                tooltip = "Applies every unambiguously safe fix across all domains, one undo group per "
                          + "domain, then reports what still needs a decision.",
            });

            return header;
        }

        private VisualElement BuildFilterRow()
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 },
            };

            var filter = new TextField { style = { flexGrow = 1 } };
            filter.textEdition.placeholder = "Filter by finding code or path…";
            filter.RegisterValueChangedCallback(evt =>
            {
                _filter = evt.newValue ?? string.Empty;
                Rebuild();
            });
            row.Add(filter);

            row.Add(new Button(() => SetAllFoldouts(true)) { text = "Expand all" });
            row.Add(new Button(() => SetAllFoldouts(false)) { text = "Collapse all" });

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
                _domainList.Add(Muted("No remediation domains are registered in this project."));
                _summary.text = string.Empty;
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

            _summary.text = applied > 0
                ? $"{applied} applied · {needsReview} need review"
                : fixable + needsReview > 0
                    ? $"{fixable} fixable · {needsReview} need review"
                    : "Nothing checked yet.";

            _domainList.Add(BuildReferencesNote());
        }

        private VisualElement BuildDomainRow(MolcaRemediationDomain domain)
        {
            var box = new VisualElement
            {
                style =
                {
                    marginBottom = 8, paddingLeft = 8, paddingRight = 8, paddingTop = 6, paddingBottom = 6,
                    borderLeftWidth = 2, borderLeftColor = new Color(0.4f, 0.4f, 0.4f, 0.6f),
                },
            };

            var head = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center },
            };
            head.Add(new Label(domain.Label)
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, minWidth = 140 },
            });
            head.Add(new Label(DescribeStatus(domain)) { style = { flexGrow = 1, opacity = 0.85f } });

            head.Add(new Button(() => RemediationHubSession.Plan(domain, RemediationPolicy.SafeOnly))
            {
                text = "Check",
                tooltip = "Runs this domain's read-only audit and previews the safe pass.",
            });
            head.Add(new Button(() => RemediationHubSession.Apply(domain, RemediationPolicy.SafeOnly))
            {
                text = "Fix Safe Issues",
            });

            box.Add(head);

            var plan = RemediationHubSession.PlanFor(domain.Id);
            var report = RemediationHubSession.ReportFor(domain.Id);

            var coverage = plan?.CoverageNote ?? report?.CoverageNote;
            if (!string.IsNullOrEmpty(coverage)) box.Add(Muted($"Coverage: {coverage}"));

            if (report != null && report.RefusedStaleSnapshot)
                box.Add(Muted("Refused: the audit is stale. Re-run it before fixing."));

            if (report != null && report.HitIterationCap)
                box.Add(Muted(
                    "Did not converge — two fixes appear to re-create each other's findings: "
                    + string.Join(", ", report.UnconvergedCodes)));

            if (plan != null)
                AddGroupedRows(box, "Would fix", plan.Fixable.Select(Row));
            if (report != null)
                AddGroupedRows(box, "Applied", report.Applied.Select(Row));

            var declined = report?.Declined ?? plan?.Declined;
            if (declined != null)
                AddGroupedRows(box, "Needs your decision", declined.Select(Row));

            if (plan != null || report != null)
            {
                var yellow = OtherFixesFor(domain);
                if (yellow.Count > 0) box.Add(BuildOtherFixes(domain, yellow));
            }

            return box;
        }

        /// <summary>
        /// Renders a section as one foldout per finding code, so a hundred findings of one cause read as one
        /// line with a count rather than a hundred lines.
        /// </summary>
        private void AddGroupedRows(
            VisualElement parent, string title, IEnumerable<(string Code, string Detail)> rows)
        {
            var all = rows.Where(PassesFilter).ToList();
            if (all.Count == 0) return;

            var groups = all
                .GroupBy(r => r.Code, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            var section = new Foldout { text = $"{title} ({all.Count})", value = true };
            _foldouts.Add(section);

            // Only auto-expand the individual groups while the whole section is readable at a glance.
            var expandGroups = all.Count <= AutoExpandThreshold;

            foreach (var group in groups)
            {
                var groupRows = group.ToList();
                var foldout = new Foldout
                {
                    text = $"{group.Key} ({groupRows.Count})",
                    value = expandGroups,
                    style = { marginLeft = 8 },
                };
                _foldouts.Add(foldout);

                foreach (var row in groupRows.Take(MaxRowsPerGroup))
                    foldout.Add(new Label("• " + row.Detail)
                    {
                        style = { whiteSpace = WhiteSpace.Normal },
                    });

                if (groupRows.Count > MaxRowsPerGroup)
                    foldout.Add(Muted($"… and {groupRows.Count - MaxRowsPerGroup} more with the same cause."));

                section.Add(foldout);
            }

            parent.Add(section);
        }

        private bool PassesFilter((string Code, string Detail) row) =>
            string.IsNullOrWhiteSpace(_filter)
            || row.Code.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
            || (row.Detail ?? string.Empty).IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;

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

        private static (string Code, string Detail) Row(MolcaPlannedFix row) =>
            (row.Target.FindingCode, $"{row.Target.Path} — {row.Outcome.Message}");

        private static (string Code, string Detail) Row(MolcaDeclinedFinding row) =>
            (row.Target.FindingCode, $"{row.Target.Path} — {row.Detail}");

        private static Label Muted(string text) => new Label(text)
        {
            style = { opacity = 0.7f, whiteSpace = WhiteSpace.Normal, marginTop = 2, marginBottom = 2 },
        };

        private static VisualElement BuildReferencesNote()
        {
            var note = new VisualElement { style = { marginTop = 12 } };
            note.Add(Muted(
                "Reference findings are repaired in Molca Hub → References. Their repair is a "
                + "revision-pinned transaction that refuses a plan built against a project that has moved on, "
                + "so it is approved there rather than swept from here."));
            return note;
        }
    }
}
