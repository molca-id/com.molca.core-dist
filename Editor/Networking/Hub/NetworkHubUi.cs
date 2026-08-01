using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Networking.Configuration;
using Molca.Editor.Networking.Validation;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// The small vocabulary every Network view is built from: a master/detail split, list rows, key-value
    /// rows, badges, and empty notes.
    /// </summary>
    /// <remarks>
    /// Shared so the seven views cannot drift into seven slightly different row layouts. Everything here
    /// is token-based USS from <c>NetworkHubView.uss</c> and the Hub's own component sheet — no inline
    /// colours, so light and dark both work without a second code path.
    /// </remarks>
    internal static class NetworkHubUi
    {
        /// <summary>Width below which a master/detail split stacks vertically.</summary>
        internal const float NarrowWidth = 820f;

        /// <summary>
        /// A master/detail split whose orientation follows the measured width.
        /// </summary>
        /// <remarks>
        /// The split direction is decided by measurement rather than by assuming the Hub's size — the
        /// workspace is docked wherever the user put it, and a 400px-wide dock must not render a
        /// side-by-side layout that clips.
        /// </remarks>
        internal sealed class Split : VisualElement
        {
            /// <summary>The list pane.</summary>
            internal VisualElement Master { get; }

            /// <summary>The detail pane.</summary>
            internal VisualElement Detail { get; }

            private bool _isNarrow;

            /// <summary>Creates a split.</summary>
            internal Split()
            {
                AddToClassList("molca-network__split");
                style.flexGrow = 1;

                Master = new VisualElement();
                Master.AddToClassList("molca-network__master");
                Add(Master);

                Detail = new ScrollView();
                Detail.AddToClassList("molca-network__detail");
                Add(Detail);

                RegisterCallback<GeometryChangedEvent>(evt => ApplyLayout(evt.newRect.width));
            }

            private void ApplyLayout(float width)
            {
                bool narrow = width > 0f && width < NarrowWidth;
                if (narrow == _isNarrow) return;

                _isNarrow = narrow;
                EnableInClassList("molca-network__split--narrow", narrow);
            }
        }

        /// <summary>A card with the workspace's standard spacing.</summary>
        /// <param name="title">Card title.</param>
        /// <param name="subtitle">Optional subtitle.</param>
        /// <param name="status">Status shown in the card header.</param>
        /// <param name="statusText">Status label, or <c>null</c> for the status's default.</param>
        internal static MolcaSectionCard Card(
            string title,
            string subtitle = null,
            MolcaStatusKind status = MolcaStatusKind.None,
            string statusText = null)
        {
            var card = new MolcaSectionCard(title, subtitle, status, statusText);
            card.AddToClassList("molca-network__card");
            return card;
        }

        /// <summary>A label/value row, the workspace's unit of detail.</summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">Field value; empty renders an em dash.</param>
        /// <param name="tooltip">Optional tooltip on the whole row.</param>
        internal static VisualElement Field(string label, string value, string tooltip = null)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-network__field");
            if (!string.IsNullOrEmpty(tooltip)) row.tooltip = tooltip;

            var key = new Label(label);
            key.AddToClassList("molca-network__field-label");
            row.Add(key);

            var val = new Label(string.IsNullOrEmpty(value) ? "—" : value);
            val.AddToClassList("molca-network__field-value");
            if (string.IsNullOrEmpty(value)) val.AddToClassList("molca-network__muted");
            row.Add(val);

            return row;
        }

        /// <summary>
        /// A field whose value carries provenance — the layer that supplied it.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The effective value.</param>
        /// <param name="layer">The layer that supplied it.</param>
        /// <param name="note">Optional explanation, e.g. why an override could not weaken a rule.</param>
        internal static VisualElement EffectiveField(
            string label,
            string value,
            NetworkConfigurationLayer layer,
            string note = null)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-network__field");

            var key = new Label(label);
            key.AddToClassList("molca-network__field-label");
            row.Add(key);

            var val = new Label(value);
            val.AddToClassList("molca-network__field-value");
            row.Add(val);

            var source = new Label(LayerLabel(layer));
            source.AddToClassList("molca-network__provenance");
            // The provenance chip is the answer to "why is it this?", so it says the whole precedence
            // chain in its tooltip rather than only the winning layer.
            source.tooltip = note ?? $"Supplied by the {LayerLabel(layer)} layer.\n\n" +
                             "Precedence: Library default → Catalog → Environment → Service → Endpoint → Send override.";
            row.Add(source);

            return row;
        }

        /// <summary>The human label for a configuration layer.</summary>
        /// <param name="layer">The layer.</param>
        internal static string LayerLabel(NetworkConfigurationLayer layer)
        {
            switch (layer)
            {
                case NetworkConfigurationLayer.LibraryDefault: return "Library default";
                case NetworkConfigurationLayer.CatalogDefault: return "Catalog";
                case NetworkConfigurationLayer.Environment: return "Environment";
                case NetworkConfigurationLayer.Service: return "Service";
                case NetworkConfigurationLayer.Endpoint: return "Endpoint";
                case NetworkConfigurationLayer.SendOverride: return "Send override";
                default: return layer.ToString();
            }
        }

        /// <summary>A small inline badge.</summary>
        /// <param name="text">Badge text.</param>
        /// <param name="status">Status colouring.</param>
        internal static Label Badge(string text, MolcaStatusKind status = MolcaStatusKind.None)
        {
            var badge = new Label(text);
            badge.AddToClassList("molca-network__badge");
            badge.AddToClassList("molca-network__badge--" + status.ToString().ToLowerInvariant());
            return badge;
        }

        /// <summary>A status dot.</summary>
        /// <param name="status">Status colouring.</param>
        /// <param name="tooltip">Optional tooltip explaining the status.</param>
        internal static VisualElement Dot(MolcaStatusKind status, string tooltip = null)
        {
            var dot = new VisualElement();
            dot.AddToClassList("molca-network__dot");
            dot.AddToClassList("molca-network__dot--" + status.ToString().ToLowerInvariant());
            if (!string.IsNullOrEmpty(tooltip)) dot.tooltip = tooltip;
            return dot;
        }

        /// <summary>
        /// Opens one of Core's reference documents in the Hub's Docs workspace.
        /// </summary>
        /// <param name="docId">
        /// The document's id, which defaults to its file stem — <c>NETWORKING_CATALOG</c> for
        /// <c>Documentation~/reference/NETWORKING_CATALOG.md</c>.
        /// </param>
        internal static void OpenDoc(string docId) => Molca.Editor.Hub.MolcaHubWindow.OpenDoc(docId);

        /// <summary>A muted note, for empty states and explanations.</summary>
        /// <param name="text">The note.</param>
        internal static Label Note(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-network__muted");
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        /// <summary>A heading inside a detail pane.</summary>
        /// <param name="text">The heading.</param>
        internal static Label Heading(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-network__heading");
            return label;
        }

        /// <summary>A horizontal row of actions.</summary>
        /// <param name="actions">The buttons to lay out.</param>
        internal static VisualElement Actions(params VisualElement[] actions)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-network__actions");
            foreach (var action in actions)
            {
                if (action != null) row.Add(action);
            }
            return row;
        }

        /// <summary>
        /// A selectable master-list row: a status dot, a title, an optional subtitle, and trailing badges.
        /// </summary>
        /// <param name="title">Primary text, usually the entity's display name.</param>
        /// <param name="subtitle">Secondary text, usually the stable ID.</param>
        /// <param name="status">Status dot colouring.</param>
        /// <param name="statusTooltip">What the status dot means.</param>
        /// <param name="selected">Whether this row is the current selection.</param>
        /// <param name="onClick">Invoked when the row is chosen.</param>
        /// <param name="badges">Trailing badges, e.g. protocols or a default marker.</param>
        internal static VisualElement ListRow(
            string title,
            string subtitle,
            MolcaStatusKind status,
            string statusTooltip,
            bool selected,
            Action onClick,
            params VisualElement[] badges)
        {
            var row = new Button(() => onClick?.Invoke());
            row.AddToClassList("molca-network__row");
            row.EnableInClassList("molca-network__row--selected", selected);
            row.text = string.Empty;

            row.Add(Dot(status, statusTooltip));

            var text = new VisualElement();
            text.AddToClassList("molca-network__row-text");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("molca-network__row-title");
            text.Add(titleLabel);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var subtitleLabel = new Label(subtitle);
                subtitleLabel.AddToClassList("molca-network__row-subtitle");
                text.Add(subtitleLabel);
            }

            row.Add(text);

            if (badges != null)
            {
                var trailing = new VisualElement();
                trailing.AddToClassList("molca-network__row-badges");
                foreach (var badge in badges)
                {
                    if (badge != null) trailing.Add(badge);
                }
                row.Add(trailing);
            }

            return row;
        }

        /// <summary>
        /// The worst severity among findings scoped to one entity, as a status kind.
        /// </summary>
        /// <param name="report">The validation report.</param>
        /// <param name="kind">Entity kind to match.</param>
        /// <param name="entityId">Entity id to match.</param>
        /// <returns><see cref="MolcaStatusKind.Ok"/> when nothing is reported.</returns>
        internal static MolcaStatusKind StatusOf(
            NetworkValidationReport report,
            NetworkValidationEntityKind kind,
            string entityId)
        {
            var worst = MolcaStatusKind.Ok;
            if (report?.Findings == null) return worst;

            foreach (var finding in report.Findings)
            {
                if (finding.EntityKind != kind) continue;
                if (!string.Equals(finding.EntityId, entityId, StringComparison.Ordinal)) continue;

                if (finding.Severity == NetworkValidationSeverity.Error)
                    return MolcaStatusKind.Error;

                if (finding.Severity == NetworkValidationSeverity.Warning)
                    worst = MolcaStatusKind.Warning;
            }
            return worst;
        }

        /// <summary>Findings scoped to one entity, in report order.</summary>
        /// <param name="report">The validation report.</param>
        /// <param name="kind">Entity kind to match.</param>
        /// <param name="entityId">Entity id to match.</param>
        internal static List<NetworkValidationFinding> FindingsFor(
            NetworkValidationReport report,
            NetworkValidationEntityKind kind,
            string entityId)
        {
            var result = new List<NetworkValidationFinding>();
            if (report?.Findings == null) return result;

            foreach (var finding in report.Findings)
            {
                if (finding.EntityKind == kind &&
                    string.Equals(finding.EntityId, entityId, StringComparison.Ordinal))
                {
                    result.Add(finding);
                }
            }
            return result;
        }

        /// <summary>The status kind matching a validation severity.</summary>
        /// <param name="severity">The severity.</param>
        internal static MolcaStatusKind StatusOf(NetworkValidationSeverity severity)
        {
            switch (severity)
            {
                case NetworkValidationSeverity.Error: return MolcaStatusKind.Error;
                case NetworkValidationSeverity.Warning: return MolcaStatusKind.Warning;
                // Info findings are worth reading but nothing is wrong, so they render neutral rather than
                // borrowing the healthy green — a green dot next to "this is not migrated yet" would read
                // as approval.
                default: return MolcaStatusKind.Idle;
            }
        }

        /// <summary>
        /// Renders a finding as a card body row with its remedy and an Open action.
        /// </summary>
        /// <param name="finding">The finding to render.</param>
        /// <param name="onOpen">Invoked when the user chooses Open, or <c>null</c> to omit the action.</param>
        internal static VisualElement FindingRow(NetworkValidationFinding finding, Action onOpen = null)
        {
            var container = new VisualElement();
            container.AddToClassList("molca-network__finding");

            var header = new VisualElement();
            header.AddToClassList("molca-network__finding-header");
            header.Add(Dot(StatusOf(finding.Severity), finding.Severity.ToString()));

            var message = new Label(finding.Message);
            message.AddToClassList("molca-network__finding-message");
            message.style.whiteSpace = WhiteSpace.Normal;
            header.Add(message);

            if (onOpen != null)
                header.Add(MolcaButtons.Mini("Open", onOpen));

            container.Add(header);

            if (!string.IsNullOrEmpty(finding.Remedy))
            {
                var remedy = Note(finding.Remedy);
                remedy.AddToClassList("molca-network__finding-remedy");
                container.Add(remedy);
            }

            var code = new Label(finding.Code);
            code.AddToClassList("molca-network__finding-code");
            // The code is what a Doctor finding, an MCP payload, and a test all match on, so it is shown
            // rather than hidden behind a tooltip — a user reporting a problem can quote it.
            code.tooltip = "Stable finding code. It reads the same in Doctor, MCP payloads, and tests.";
            container.Add(code);

            return container;
        }
    }
}
