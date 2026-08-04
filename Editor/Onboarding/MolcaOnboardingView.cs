using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Hub;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using UnityEngine.UIElements;

namespace Molca.Editor.Onboarding
{
    /// <summary>
    /// The onboarding checklist as a reusable <see cref="VisualElement"/>: every registered
    /// <see cref="MolcaOnboardingItem"/>, grouped by whether it states a fact about the project or an
    /// opinion, each showing where the project stands and what advances it.
    /// </summary>
    /// <remarks>
    /// <para>Placement: <c>Packages/com.molca.core/Editor/Onboarding/</c>.
    /// Base class: <see cref="VisualElement"/>. Hosted by the Hub's Onboarding workspace and by the
    /// standalone <see cref="MolcaOnboardingWindow"/> (window/view split, per
    /// <c>EDITOR_DESIGN_LANGUAGE.md</c>).</para>
    /// <para><b>This replaced a wizard, and the difference is the point.</b> A wizard has steps, an order,
    /// and a completion record — three things that go stale the moment a teammate deletes the asset that
    /// satisfied step 3. A checklist re-derives every row from the project on each refresh, so it cannot
    /// claim something is done that is not. Nothing here is sequential and nothing has to be run.</para>
    /// <para><b>It renders; it does not decide.</b> Every row, status, and action comes from
    /// <see cref="MolcaOnboardingChecklist"/>. The view holds no opinion about what a configured project
    /// looks like, and adding one here would put a second answer next to the starter's.</para>
    /// <para>Evaluation happens when the view is attached and when Refresh is pressed — never on a
    /// schedule. Because of that the header states the time it last evaluated: rows do not update themselves
    /// when the project changes underneath them, and a status with no timestamp cannot be told apart from a
    /// stale one.</para>
    /// </remarks>
    public sealed class MolcaOnboardingView : VisualElement
    {
        private readonly MolcaWorkspaceHeader _header;
        private readonly VisualElement _list;

        /// <summary>Builds the view and evaluates once.</summary>
        public MolcaOnboardingView()
        {
            AddToClassList("molca-workspace");
            MolcaEditorUi.Apply(this);

            _header = new MolcaWorkspaceHeader("Onboarding");
            var refresh = MolcaButtons.Toolbar("Refresh", Refresh);
            refresh.tooltip = "Re-checks every item against the project. Nothing is modified.";
            _header.AddAction(refresh);
            Add(_header);

            var intro = new Label(
                "Where this project stands against what Molca expects. Every row is re-checked from the "
                + "project itself, so nothing here can claim to be done when it is not.");
            intro.AddToClassList("molca-muted");
            intro.style.whiteSpace = WhiteSpace.Normal;
            intro.style.marginBottom = 8;
            Add(intro);

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            _list = new VisualElement();
            _list.AddToClassList("molca-list");
            scroll.Add(_list);
            Add(scroll);

            Refresh();
        }

        /// <summary>Re-evaluates the checklist and rebuilds the list.</summary>
        private void Refresh() => Render(MolcaOnboardingChecklist.Evaluate());

        private void Render(MolcaOnboardingSnapshot snapshot)
        {
            _list.Clear();

            // Nothing re-evaluates when the project changes, so every row is only as true as its evaluation.
            // Stamping it is what lets a reader distinguish a live status from one left over from before they
            // fixed the thing it names. Wall-clock, not "n minutes ago": the latter would itself go stale.
            _header.SetSummary($"{snapshot.Summarize()} · checked {snapshot.EvaluatedAt:HH:mm:ss}");

            if (snapshot.Entries.Count == 0)
            {
                var empty = new Label("No onboarding items are registered.");
                empty.AddToClassList("molca-empty-state");
                _list.Add(empty);
                return;
            }

            AddGroup(
                snapshot,
                MolcaOnboardingSeverity.Required,
                "Required",
                "Reported by a Molca audit — the project is asserted to have got these wrong.");

            AddGroup(
                snapshot,
                MolcaOnboardingSeverity.Recommended,
                "Recommended",
                "What a fully-featured Molca project looks like. Declining any of these is a choice, not a fault.");
        }

        /// <summary>
        /// Renders one severity group, or nothing when it has no rows.
        /// </summary>
        /// <remarks>
        /// The two groups are never merged and never re-sorted into one list. An audit finding and a
        /// suggestion look identical once they are adjacent rows with tick boxes, and a user who learns that
        /// half the list is optional stops trusting the other half.
        /// </remarks>
        private void AddGroup(
            MolcaOnboardingSnapshot snapshot,
            MolcaOnboardingSeverity severity,
            string title,
            string summary)
        {
            var entries = snapshot.Entries.Where(e => e.Item.Severity == severity).ToList();
            if (entries.Count == 0) return;

            var outstanding = entries.Count(e => e.Check.IsOutstanding);

            // Error is reserved for a confirmed finding. A Required group whose rows are merely unchecked
            // is amber, for the same reason the rows themselves are: nothing has accused the project yet.
            var confirmed = entries.Any(e => e.Check.Status == MolcaOnboardingStatus.Todo);
            var status = outstanding == 0
                ? MolcaStatusKind.Ok
                : severity == MolcaOnboardingSeverity.Required && confirmed
                    ? MolcaStatusKind.Error
                    : MolcaStatusKind.Warning;

            var group = new MolcaListGroup(
                title,
                summary,
                status,
                outstanding == 0 ? "All clear" : $"{outstanding} outstanding",
                // A group with nothing left to do is exactly the group nobody needs to read.
                expanded: outstanding > 0);

            foreach (var entry in entries)
                group.Body.Add(BuildRow(entry));

            _list.Add(group);
        }

        private VisualElement BuildRow(MolcaOnboardingEntry entry)
        {
            var item = entry.Item;
            var row = new MolcaListRow(item.Title, item.Summary);

            row.AddMetadata(new MolcaStatusBadge(StatusKind(entry), StatusText(entry.Check.Status)));

            if (!string.IsNullOrWhiteSpace(item.ActionLabel) && item.Act != null)
            {
                // Left enabled in every state: a satisfied starter step reports "already set up" rather than
                // acting twice, and a row whose action is navigation is still worth following once it is done.
                var action = MolcaButtons.Mini(item.ActionLabel, () =>
                {
                    item.Act();
                    Refresh();
                });
                action.tooltip = item.Summary;
                row.AddAction(action);
            }

            if (!string.IsNullOrWhiteSpace(item.DocId))
                row.AddAction(MolcaButtons.Mini("Docs", () => MolcaHubWindow.OpenDoc(item.DocId)));

            foreach (var detail in DetailLines(entry))
                row.AddDetail(detail);

            row.SetExpanded(entry.Check.IsOutstanding);
            return row;
        }

        private static IEnumerable<VisualElement> DetailLines(MolcaOnboardingEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.Check.Detail))
            {
                var detail = new Label(entry.Check.Detail);
                detail.AddToClassList("molca-list-note");
                detail.style.whiteSpace = WhiteSpace.Normal;
                yield return detail;
            }

            if (!string.IsNullOrWhiteSpace(entry.Item.Why))
            {
                var why = new Label(entry.Item.Why);
                why.AddToClassList("molca-muted");
                why.style.whiteSpace = WhiteSpace.Normal;
                yield return why;
            }
        }

        private static MolcaStatusKind StatusKind(MolcaOnboardingEntry entry)
        {
            switch (entry.Check.Status)
            {
                case MolcaOnboardingStatus.Done:
                    return MolcaStatusKind.Ok;
                case MolcaOnboardingStatus.Todo:
                    return entry.Item.Severity == MolcaOnboardingSeverity.Required
                        ? MolcaStatusKind.Error
                        : MolcaStatusKind.Warning;
                // Blocked is never an error, whatever the severity: the project may be fine and the check
                // simply unable to say so yet.
                case MolcaOnboardingStatus.Blocked:
                    return MolcaStatusKind.Warning;
                default:
                    return MolcaStatusKind.Idle;
            }
        }

        private static string StatusText(MolcaOnboardingStatus status)
        {
            switch (status)
            {
                case MolcaOnboardingStatus.Done: return "Done";
                case MolcaOnboardingStatus.Todo: return "To do";
                case MolcaOnboardingStatus.Blocked: return "Blocked";
                default: return "Not applicable";
            }
        }
    }
}
