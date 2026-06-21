using System;
using UnityEngine.UIElements;

namespace Molca.Editor.UI.Components
{
    /// <summary>
    /// Shared section-card <see cref="VisualElement"/> implementing the Molca editor design language.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: instantiate from any editor window/view; this component has no global registry or
    /// runtime state. Add controls into <see cref="Body"/>. Styling comes from
    /// <c>MolcaEditorComponents.uss</c> (loaded by <see cref="MolcaEditorUi.Apply"/>); do not place a
    /// card inside another card per <c>EDITOR_DESIGN_LANGUAGE.md</c>.
    /// Promoted in Sprint 27.2 from the Hub-only <c>MolcaHubSectionCard</c> (since removed).
    /// </remarks>
    public class MolcaSectionCard : VisualElement
    {
        private readonly Label _titleLabel;
        private readonly Label _subtitleLabel;
        private readonly VisualElement _statusDot;
        private readonly Label _statusLabel;
        private readonly VisualElement _actions;
        private readonly Button _helpButton;

        /// <summary>Container for controls rendered inside the card body.</summary>
        public VisualElement Body { get; }

        /// <summary>Creates a section card with optional status, help, and subtitle metadata.</summary>
        /// <param name="title">Header title text.</param>
        /// <param name="subtitle">Optional muted subtitle under the title; hidden when null/empty.</param>
        /// <param name="status">Status dot kind; <see cref="MolcaStatusKind.None"/> hides the dot.</param>
        /// <param name="statusText">Optional inline status caption next to the dot.</param>
        /// <param name="helpTooltip">Optional tooltip; shows a "?" help button when provided.</param>
        public MolcaSectionCard(
            string title,
            string subtitle = null,
            MolcaStatusKind status = MolcaStatusKind.None,
            string statusText = null,
            string helpTooltip = null)
        {
            AddToClassList("molca-card");

            var header = new VisualElement();
            header.AddToClassList("molca-card__header");
            Add(header);

            var titleStack = new VisualElement();
            titleStack.AddToClassList("molca-card__title-stack");
            header.Add(titleStack);

            _titleLabel = new Label(title ?? string.Empty);
            _titleLabel.AddToClassList("molca-card__title");
            titleStack.Add(_titleLabel);

            _subtitleLabel = new Label(subtitle ?? string.Empty);
            _subtitleLabel.AddToClassList("molca-card__subtitle");
            _subtitleLabel.style.display = string.IsNullOrEmpty(subtitle) ? DisplayStyle.None : DisplayStyle.Flex;
            titleStack.Add(_subtitleLabel);

            _statusDot = new VisualElement();
            _statusDot.AddToClassList("molca-status-dot");
            header.Add(_statusDot);

            _statusLabel = new Label();
            _statusLabel.AddToClassList("molca-card__status-label");
            header.Add(_statusLabel);

            _helpButton = new Button { text = "?", tooltip = helpTooltip ?? string.Empty };
            _helpButton.AddToClassList("molca-help-button");
            _helpButton.style.display = string.IsNullOrEmpty(helpTooltip) ? DisplayStyle.None : DisplayStyle.Flex;
            header.Add(_helpButton);

            _actions = new VisualElement();
            _actions.AddToClassList("molca-card__actions");
            header.Add(_actions);

            Body = new VisualElement();
            Body.AddToClassList("molca-card__body");
            Add(Body);

            SetStatus(status, statusText);
        }

        /// <summary>Updates the card title.</summary>
        public void SetTitle(string title) => _titleLabel.text = title ?? string.Empty;

        /// <summary>Updates or hides the card subtitle.</summary>
        public void SetSubtitle(string subtitle)
        {
            _subtitleLabel.text = subtitle ?? string.Empty;
            _subtitleLabel.style.display = string.IsNullOrEmpty(subtitle) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>Sets the status dot and optional inline status text.</summary>
        public void SetStatus(MolcaStatusKind status, string statusText = null)
        {
            RemoveStatusClasses();

            if (status == MolcaStatusKind.None)
            {
                _statusDot.style.display = DisplayStyle.None;
                _statusLabel.style.display = DisplayStyle.None;
                _statusLabel.text = string.Empty;
                return;
            }

            _statusDot.style.display = DisplayStyle.Flex;
            _statusDot.AddToClassList(StatusClass(status));

            _statusLabel.text = statusText ?? string.Empty;
            _statusLabel.style.display = string.IsNullOrEmpty(statusText) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>Adds an action button or custom control to the right side of the card header.</summary>
        public void AddHeaderAction(VisualElement action)
        {
            if (action == null) return;
            _actions.Add(action);
        }

        /// <summary>Sets the help button callback and tooltip.</summary>
        public void SetHelp(string tooltip, Action onClick = null)
        {
            _helpButton.tooltip = tooltip ?? string.Empty;
            _helpButton.style.display = string.IsNullOrEmpty(tooltip) ? DisplayStyle.None : DisplayStyle.Flex;

            if (onClick != null)
                _helpButton.clicked += onClick;
        }

        /// <summary>Dims and disables only the body, leaving the header visible.</summary>
        public void SetBodyEnabled(bool enabled)
        {
            Body.SetEnabled(enabled);
            Body.EnableInClassList("molca-card__body--disabled", !enabled);
        }

        private void RemoveStatusClasses()
        {
            _statusDot.RemoveFromClassList("molca-status-dot--ok");
            _statusDot.RemoveFromClassList("molca-status-dot--idle");
            _statusDot.RemoveFromClassList("molca-status-dot--warn");
            _statusDot.RemoveFromClassList("molca-status-dot--error");
        }

        private static string StatusClass(MolcaStatusKind status)
        {
            return status switch
            {
                MolcaStatusKind.Ok => "molca-status-dot--ok",
                MolcaStatusKind.Warning => "molca-status-dot--warn",
                MolcaStatusKind.Error => "molca-status-dot--error",
                _ => "molca-status-dot--idle",
            };
        }
    }
}
