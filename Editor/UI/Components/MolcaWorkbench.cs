using System;
using UnityEngine.UIElements;

namespace Molca.Editor.UI.Components
{
    /// <summary>Consistent title, summary, and action row for a Molca editor workspace.</summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Base class: <see cref="VisualElement"/>. Registration: create inside any root that has received
    /// <see cref="MolcaEditorUi.Apply"/>. Styling comes from <c>MolcaEditorComponents.uss</c>.
    /// </remarks>
    public sealed class MolcaWorkspaceHeader : VisualElement
    {
        /// <summary>The workspace title label.</summary>
        public Label TitleLabel { get; }

        /// <summary>The compact count/state summary shown beside the title.</summary>
        public Label SummaryLabel { get; }

        /// <summary>Right-aligned action host.</summary>
        public VisualElement Actions { get; }

        /// <summary>Creates a workspace header.</summary>
        public MolcaWorkspaceHeader(string title, string summary = null)
        {
            AddToClassList("molca-workspace-header");

            var heading = new VisualElement();
            heading.AddToClassList("molca-workspace-header__heading");
            Add(heading);

            TitleLabel = new Label(title ?? string.Empty);
            TitleLabel.AddToClassList("molca-workspace-header__title");
            heading.Add(TitleLabel);

            SummaryLabel = new Label(summary ?? string.Empty);
            SummaryLabel.AddToClassList("molca-workspace-header__summary");
            SummaryLabel.style.display = string.IsNullOrWhiteSpace(summary) ? DisplayStyle.None : DisplayStyle.Flex;
            heading.Add(SummaryLabel);

            Actions = new VisualElement();
            Actions.AddToClassList("molca-workspace-header__actions");
            Add(Actions);
        }

        /// <summary>Updates the summary and hides it when empty.</summary>
        public void SetSummary(string summary)
        {
            SummaryLabel.text = summary ?? string.Empty;
            SummaryLabel.style.display = string.IsNullOrWhiteSpace(summary) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>Adds a right-aligned header action.</summary>
        public void AddAction(Button action)
        {
            if (action != null) Actions.Add(action);
        }
    }

    /// <summary>Compact local-control bar used immediately above a list or workspace body.</summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Base class: <see cref="VisualElement"/>. The <see cref="Content"/> host grows, while
    /// <see cref="Actions"/> remains content-sized.
    /// </remarks>
    public sealed class MolcaWorkspaceToolbar : VisualElement
    {
        /// <summary>Flexible host for search, filters, or a local selector.</summary>
        public VisualElement Content { get; }

        /// <summary>Trailing action host.</summary>
        public VisualElement Actions { get; }

        /// <summary>Creates an empty workspace toolbar.</summary>
        public MolcaWorkspaceToolbar()
        {
            AddToClassList("molca-workspace-toolbar");

            Content = new VisualElement();
            Content.AddToClassList("molca-workspace-toolbar__content");
            Add(Content);

            Actions = new VisualElement();
            Actions.AddToClassList("molca-workspace-toolbar__actions");
            Add(Actions);
        }

        /// <summary>Adds a trailing toolbar action.</summary>
        public void AddAction(Button action)
        {
            if (action != null) Actions.Add(action);
        }
    }

    /// <summary>Text-labelled status indicator for rows and group headers.</summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Base class: <see cref="VisualElement"/>. The dot and text always travel together so state is never
    /// conveyed by colour alone.
    /// </remarks>
    public sealed class MolcaStatusBadge : VisualElement
    {
        private static readonly string[] StatusSuffixes = { "ok", "warn", "error", "idle" };
        private readonly VisualElement _dot;

        /// <summary>The badge text label.</summary>
        public Label Label { get; }

        /// <summary>Creates a status badge.</summary>
        public MolcaStatusBadge(MolcaStatusKind status = MolcaStatusKind.Idle, string text = null)
        {
            AddToClassList("molca-status-badge");
            _dot = new VisualElement();
            _dot.AddToClassList("molca-status-badge__dot");
            Add(_dot);

            Label = new Label(text ?? string.Empty);
            Label.AddToClassList("molca-status-badge__label");
            Add(Label);
            SetStatus(status, text);
        }

        /// <summary>Updates status semantics and label text.</summary>
        public void SetStatus(MolcaStatusKind status, string text = null)
        {
            RemoveStatusClasses();
            var suffix = status switch
            {
                MolcaStatusKind.Ok => "ok",
                MolcaStatusKind.Warning => "warn",
                MolcaStatusKind.Error => "error",
                _ => "idle",
            };
            AddToClassList("molca-status-badge--" + suffix);
            _dot.AddToClassList("molca-status-badge__dot--" + suffix);
            Label.text = text ?? string.Empty;
            style.display = status == MolcaStatusKind.None || string.IsNullOrWhiteSpace(text)
                ? DisplayStyle.None
                : DisplayStyle.Flex;
        }

        private void RemoveStatusClasses()
        {
            foreach (var suffix in StatusSuffixes)
            {
                RemoveFromClassList("molca-status-badge--" + suffix);
                _dot.RemoveFromClassList("molca-status-badge__dot--" + suffix);
            }
        }
    }

    /// <summary>Collapsible semantic group inside a <c>molca-list</c> workbench list.</summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Base class: <see cref="VisualElement"/>. Registration: add to a parent carrying the
    /// <c>molca-list</c> class. Group headers expose summary, status, and actions without turning every row
    /// into a separate card.
    /// </remarks>
    public sealed class MolcaListGroup : VisualElement
    {
        private readonly Button _toggle;

        /// <summary>Group content host.</summary>
        public VisualElement Body { get; }

        /// <summary>Header action host.</summary>
        public VisualElement Actions { get; }

        /// <summary>Status shown at the trailing edge of the header.</summary>
        public MolcaStatusBadge Status { get; }

        /// <summary>Whether the body is currently expanded.</summary>
        public bool Expanded { get; private set; }

        /// <summary>Creates a collapsible list group.</summary>
        public MolcaListGroup(string title, string summary = null, MolcaStatusKind status = MolcaStatusKind.None,
            string statusText = null, bool expanded = true)
        {
            AddToClassList("molca-list-group");

            var header = new VisualElement();
            header.AddToClassList("molca-list-group__header");
            Add(header);

            _toggle = new Button(() => SetExpanded(!Expanded));
            _toggle.AddToClassList("molca-list-group__toggle");
            header.Add(_toggle);

            var heading = new VisualElement();
            heading.AddToClassList("molca-list-group__heading");
            header.Add(heading);

            var titleLabel = new Label(title ?? string.Empty);
            titleLabel.AddToClassList("molca-list-group__title");
            heading.Add(titleLabel);

            if (!string.IsNullOrWhiteSpace(summary))
            {
                var summaryLabel = new Label(summary);
                summaryLabel.AddToClassList("molca-list-group__summary");
                heading.Add(summaryLabel);
            }

            Status = new MolcaStatusBadge(status, statusText);
            header.Add(Status);

            Actions = new VisualElement();
            Actions.AddToClassList("molca-list-group__actions");
            header.Add(Actions);

            Body = new VisualElement();
            Body.AddToClassList("molca-list-group__body");
            Add(Body);

            SetExpanded(expanded);
        }

        /// <summary>Adds a compact header action.</summary>
        public void AddHeaderAction(Button action)
        {
            if (action != null) Actions.Add(action);
        }

        /// <summary>Shows or hides the group body.</summary>
        public void SetExpanded(bool expanded)
        {
            Expanded = expanded;
            _toggle.text = expanded ? "▾" : "▸";
            _toggle.tooltip = expanded ? "Collapse group" : "Expand group";
            Body.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            EnableInClassList("molca-list-group--collapsed", !expanded);
        }
    }

    /// <summary>One dense, optionally expandable row in a Molca workbench list.</summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Base class: <see cref="VisualElement"/>. The row owns stable main, metadata, action, and detail slots;
    /// domain views remain responsible for the actual columns and commands they place into those slots.
    /// </remarks>
    public sealed class MolcaListRow : VisualElement
    {
        private Button _detailToggle;

        /// <summary>Primary text label.</summary>
        public Label TitleLabel { get; }

        /// <summary>Secondary identity/context label.</summary>
        public Label SubtitleLabel { get; }

        /// <summary>Flexible metadata/value host.</summary>
        public VisualElement Metadata { get; }

        /// <summary>Trailing action host.</summary>
        public VisualElement Actions { get; }

        /// <summary>Expandable detail host.</summary>
        public VisualElement Details { get; }

        /// <summary>Whether the detail host is currently expanded.</summary>
        public bool Expanded { get; private set; }

        /// <summary>Creates a dense list row.</summary>
        public MolcaListRow(string title, string subtitle = null)
        {
            AddToClassList("molca-list-row");

            var line = new VisualElement();
            line.AddToClassList("molca-list-row__line");
            Add(line);

            var leading = new VisualElement();
            leading.AddToClassList("molca-list-row__leading");
            line.Add(leading);

            var main = new VisualElement();
            main.AddToClassList("molca-list-row__main");
            line.Add(main);

            TitleLabel = new Label(title ?? string.Empty);
            TitleLabel.AddToClassList("molca-list-row__title");
            main.Add(TitleLabel);

            SubtitleLabel = new Label(subtitle ?? string.Empty);
            SubtitleLabel.AddToClassList("molca-list-row__subtitle");
            SubtitleLabel.style.display = string.IsNullOrWhiteSpace(subtitle) ? DisplayStyle.None : DisplayStyle.Flex;
            main.Add(SubtitleLabel);

            Metadata = new VisualElement();
            Metadata.AddToClassList("molca-list-row__metadata");
            line.Add(Metadata);

            Actions = new VisualElement();
            Actions.AddToClassList("molca-list-row__actions");
            line.Add(Actions);

            Details = new VisualElement();
            Details.AddToClassList("molca-list-row__details");
            Details.style.display = DisplayStyle.None;
            Add(Details);
        }

        /// <summary>Adds a value, badge, or other metadata cell.</summary>
        public void AddMetadata(VisualElement metadata)
        {
            if (metadata != null) Metadata.Add(metadata);
        }

        /// <summary>Adds a trailing row action.</summary>
        public void AddAction(Button action)
        {
            if (action != null) Actions.Add(action);
        }

        /// <summary>Adds detail content and enables the leading disclosure control.</summary>
        public void AddDetail(VisualElement detail)
        {
            if (detail == null) return;
            Details.Add(detail);
            EnsureDetailToggle();
        }

        /// <summary>Shows or hides the detail host.</summary>
        public void SetExpanded(bool expanded)
        {
            Expanded = expanded && _detailToggle != null;
            Details.style.display = Expanded ? DisplayStyle.Flex : DisplayStyle.None;
            if (_detailToggle != null)
            {
                _detailToggle.text = Expanded ? "▾" : "▸";
                _detailToggle.tooltip = Expanded ? "Hide details" : "Show details";
            }
            EnableInClassList("molca-list-row--expanded", Expanded);
        }

        /// <summary>Applies selected-row emphasis without changing expansion.</summary>
        public void SetSelected(bool selected) =>
            EnableInClassList("molca-list-row--selected", selected);

        private void EnsureDetailToggle()
        {
            if (_detailToggle != null) return;
            _detailToggle = new Button(() => SetExpanded(!Expanded));
            _detailToggle.AddToClassList("molca-list-row__toggle");
            this.Q<VisualElement>(className: "molca-list-row__leading").Add(_detailToggle);
            SetExpanded(false);
        }
    }
}
