using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Networking.Http.Models;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// The Network workspace's editable field vocabulary: <see cref="MolcaFields"/> wearing this
    /// workspace's class names, plus the two controls that only make sense against a catalog.
    /// </summary>
    /// <remarks>
    /// The generic controls moved to <see cref="MolcaFields"/> when the Content workspace needed the same
    /// ones. What is left here is the domain-specific part: <see cref="EditChoice"/>, whose create path
    /// goes through <see cref="NetworkHubPrompt"/>, and <see cref="EditHeaderList"/>, which edits
    /// <see cref="HttpHeader"/>s. Everything else delegates, so the commit-timing rules and the
    /// draft-row rules have one implementation rather than two that drift.
    /// <para>
    /// Delegating members re-tag the shared row with this workspace's classes, because
    /// <c>NetworkHubView.uss</c> loads after the shared sheet and overrides the label column width. Without
    /// the tag the rows would still work but would sit on the narrower shared grid, and a card mixing
    /// read-only <see cref="NetworkHubUi.Field"/> rows with editable ones would show two label columns.
    /// </para>
    /// <para>
    /// Every control takes a commit callback and touches no catalog: the view passes a callback that goes
    /// through <c>NetworkCatalogEditingService</c>, which stays the only write path.
    /// </para>
    /// </remarks>
    internal static class NetworkHubFields
    {
        /// <summary>The label shown for an optional reference that is not set.</summary>
        internal const string InheritLabel = MolcaFields.InheritLabel;

        /// <summary>The label shown for an optional reference meaning "nothing".</summary>
        internal const string NoneLabel = MolcaFields.NoneLabel;

        /// <summary>
        /// A label plus control on the same grid as <see cref="NetworkHubUi.Field"/>.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="control">The editing control.</param>
        /// <param name="tooltip">Optional tooltip on the whole row.</param>
        internal static VisualElement Row(string label, VisualElement control, string tooltip = null) =>
            Tag(MolcaFields.Row(label, control, tooltip));

        /// <summary>
        /// Applies this workspace's class names to a shared field row.
        /// </summary>
        /// <param name="row">A row built by <see cref="MolcaFields"/>: label first, control second.</param>
        /// <returns>The same row.</returns>
        private static VisualElement Tag(VisualElement row)
        {
            row.AddToClassList("molca-network__field");
            if (row.childCount > 0) row[0].AddToClassList("molca-network__field-label");
            if (row.childCount > 1) row[1].AddToClassList("molca-network__field-control");
            return row;
        }

        /// <summary>
        /// A single-line text field committing on blur and Enter.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the new value when it differs from <paramref name="value"/>.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        /// <param name="placeholder">Hint shown while the field is empty.</param>
        /// <param name="trim">
        /// Whether surrounding whitespace is stripped before committing. Pass <c>false</c> where
        /// whitespace is part of the value — a credential's <c>"Bearer "</c> scheme prefix needs its
        /// trailing space, and trimming it here would break the header the service assembles.
        /// </param>
        internal static VisualElement EditText(
            string label,
            string value,
            Action<string> commit,
            string tooltip = null,
            string placeholder = null,
            bool trim = true) =>
            Tag(MolcaFields.EditText(label, value, commit, tooltip, placeholder, trim));

        /// <summary>
        /// A multi-line text area committing on blur.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the new value when it differs from <paramref name="value"/>.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        internal static VisualElement EditTextArea(
            string label,
            string value,
            Action<string> commit,
            string tooltip = null)
        {
            var row = Tag(MolcaFields.EditTextArea(label, value, commit, tooltip));
            if (row.childCount > 1) row[1].AddToClassList("molca-network__textarea");
            return row;
        }

        /// <summary>
        /// A checkbox committing immediately.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the new value.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        internal static VisualElement EditToggle(
            string label,
            bool value,
            Action<bool> commit,
            string tooltip = null) =>
            Tag(MolcaFields.EditToggle(label, value, commit, tooltip));

        /// <summary>
        /// An enum dropdown committing immediately.
        /// </summary>
        /// <typeparam name="TEnum">The enum type.</typeparam>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the new value.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        internal static VisualElement EditEnum<TEnum>(
            string label,
            TEnum value,
            Action<TEnum> commit,
            string tooltip = null)
            where TEnum : Enum =>
            Tag(MolcaFields.EditEnum(label, value, commit, tooltip));

        /// <summary>
        /// A flags dropdown for a <see cref="FlagsAttribute"/> enum, committing immediately.
        /// </summary>
        /// <typeparam name="TEnum">The flags enum type.</typeparam>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current mask.</param>
        /// <param name="commit">Invoked with the new mask.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        internal static VisualElement EditFlags<TEnum>(
            string label,
            TEnum value,
            Action<TEnum> commit,
            string tooltip = null)
            where TEnum : Enum =>
            Tag(MolcaFields.EditFlags(label, value, commit, tooltip));

        /// <summary>
        /// An integer field, clamped to the serialized field's range, committing on blur and Enter.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the clamped new value.</param>
        /// <param name="min">Lowest accepted value.</param>
        /// <param name="max">Highest accepted value.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        internal static VisualElement EditInt(
            string label,
            int value,
            Action<int> commit,
            int min,
            int max,
            string tooltip = null) =>
            Tag(MolcaFields.EditInt(label, value, commit, min, max, tooltip));

        /// <summary>
        /// A float field with a lower bound, committing on blur and Enter.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the clamped new value.</param>
        /// <param name="min">Lowest accepted value.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        internal static VisualElement EditFloat(
            string label,
            float value,
            Action<float> commit,
            float min = 0f,
            string tooltip = null) =>
            Tag(MolcaFields.EditFloat(label, value, commit, min, tooltip));

        /// <summary>
        /// A byte-count field where zero reads as unbounded, committing on blur and Enter.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value; 0 means unbounded.</param>
        /// <param name="commit">Invoked with the new value.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        internal static VisualElement EditByteSize(
            string label,
            long value,
            Action<long> commit,
            string tooltip = null) =>
            Tag(MolcaFields.EditByteSize(label, value, commit, tooltip));

        /// <summary>
        /// A dropdown over existing IDs, with a sentinel entry for "not set".
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The currently referenced ID, or empty.</param>
        /// <param name="options">The selectable IDs.</param>
        /// <param name="commit">Invoked with the chosen ID, or empty for the sentinel.</param>
        /// <param name="emptyLabel">
        /// What an unset reference is called — <see cref="InheritLabel"/> where the value falls through to
        /// another layer, <see cref="NoneLabel"/> where it simply means nothing.
        /// </param>
        /// <param name="tooltip">Optional tooltip.</param>
        internal static VisualElement EditReference(
            string label,
            string value,
            IReadOnlyList<string> options,
            Action<string> commit,
            string emptyLabel = InheritLabel,
            string tooltip = null) =>
            Tag(MolcaFields.EditReference(label, value, options, commit, emptyLabel, tooltip));

        /// <summary>
        /// An editable list of strings: one row per entry with a remove action, plus an add row.
        /// </summary>
        /// <param name="heading">Section heading.</param>
        /// <param name="values">The current entries.</param>
        /// <param name="commit">Invoked with the whole new list.</param>
        /// <param name="entryLabel">What one entry is called, used on the add button and placeholder.</param>
        /// <param name="emptyNote">Explains what an empty list means. Shown when there are no entries.</param>
        /// <param name="tooltip">Optional tooltip on each entry.</param>
        /// <remarks>
        /// Untagged, unlike the field rows: a list is not on the label/value grid, and the shared list-row
        /// and heading classes carry the same rules this workspace's copies did.
        /// </remarks>
        internal static VisualElement EditStringList(
            string heading,
            IReadOnlyList<string> values,
            Action<List<string>> commit,
            string entryLabel,
            string emptyNote = null,
            string tooltip = null) =>
            MolcaFields.EditStringList(heading, values, commit, entryLabel, emptyNote, tooltip);

        /// <summary>
        /// A strict, searchable dropdown over detected values, with an explicit action to create one.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value, or empty.</param>
        /// <param name="options">The detected values, from <see cref="NetworkHubChoices"/>.</param>
        /// <param name="commit">Invoked with the chosen value, or empty for the sentinel.</param>
        /// <param name="emptyLabel">
        /// What an unset value is called — <see cref="InheritLabel"/> where it falls through to another
        /// layer, <see cref="NoneLabel"/> where it simply means nothing.
        /// </param>
        /// <param name="create">
        /// How a value absent from <paramref name="options"/> is introduced. Null omits the create entry,
        /// making the field selectable only from what was detected.
        /// </param>
        /// <param name="tooltip">Optional tooltip.</param>
        /// <remarks>
        /// <para><b>No free typing.</b> These fields name things that must already exist — a compiled type,
        /// an exported environment variable, the region label the rest of the catalog uses — and a text box
        /// accepts every misspelling of each of them silently, to be discovered at runtime or never. The
        /// dropdown can only offer what was detected, so choosing is the normal path and introducing
        /// something new is a separate, deliberate act with its own validation.</para>
        ///
        /// <para><b>A stored value that is no longer detected is shown, marked, and left alone.</b> The
        /// author's machine not exporting a variable, or a model class not being compiled yet, is not
        /// evidence the authored value is wrong — so it stays exactly as authored and says that it could not
        /// be found, rather than being normalized to empty by a control that could not see it.</para>
        /// </remarks>
        internal static VisualElement EditChoice(
            string label,
            string value,
            IReadOnlyList<string> options,
            Action<string> commit,
            string emptyLabel = NoneLabel,
            ChoiceCreation create = null,
            string tooltip = null)
        {
            var choices = new List<string>();
            if (options != null)
            {
                foreach (string option in options)
                {
                    if (!string.IsNullOrWhiteSpace(option)) choices.Add(option);
                }
            }

            string current = value ?? string.Empty;
            bool missing = current.Length > 0 && !choices.Contains(current);

            var button = new Button { text = DisplayFor(current, emptyLabel, missing) };
            button.AddToClassList("molca-network__choice");
            if (missing) button.AddToClassList("molca-network__choice--missing");

            button.clicked += () =>
            {
                var dropdown = new ChoiceDropdown(
                    new AdvancedDropdownState(),
                    label,
                    choices,
                    emptyLabel,
                    create?.EntryLabel,
                    picked =>
                    {
                        if (!string.Equals(picked, current, StringComparison.Ordinal))
                            commit?.Invoke(picked);
                    },
                    () =>
                    {
                        string created = NetworkHubPrompt.ForValue(
                            create.Title, create.Explanation, create.FieldLabel,
                            current, "Add", create.Validate);

                        // Cancelling leaves the field exactly as it was. Nothing is committed, so there is
                        // no reload and no Undo step for an action the author backed out of.
                        if (!string.IsNullOrEmpty(created)
                            && !string.Equals(created, current, StringComparison.Ordinal))
                        {
                            commit?.Invoke(created);
                        }
                    });

                dropdown.Show(button.worldBound);
            };

            return Row(label, button, tooltip);
        }

        /// <summary>What the closed dropdown reads as.</summary>
        private static string DisplayFor(string value, string emptyLabel, bool missing)
        {
            if (string.IsNullOrEmpty(value)) return emptyLabel;
            return missing ? value + "  ·  not found" : value;
        }

        /// <summary>
        /// How an <see cref="EditChoice"/> field introduces a value its detection could not offer.
        /// </summary>
        /// <remarks>
        /// A separate type rather than four more parameters, because the create path is all-or-nothing: a
        /// field either has one and needs every part of it, or has none and takes only what was detected.
        /// </remarks>
        internal sealed class ChoiceCreation
        {
            /// <summary>The dropdown entry that starts creation, e.g. <c>New type…</c>.</summary>
            internal string EntryLabel { get; }

            /// <summary>Prompt window title.</summary>
            internal string Title { get; }

            /// <summary>What the value is for, and what it costs to name one that does not exist.</summary>
            internal string Explanation { get; }

            /// <summary>Label on the prompt's input.</summary>
            internal string FieldLabel { get; }

            /// <summary>Returns null when acceptable, otherwise why not.</summary>
            internal Func<string, string> Validate { get; }

            /// <summary>Describes the create action for one field.</summary>
            /// <param name="entryLabel">The dropdown entry that starts creation.</param>
            /// <param name="title">Prompt window title.</param>
            /// <param name="explanation">What the value is for.</param>
            /// <param name="fieldLabel">Label on the prompt's input.</param>
            /// <param name="validate">Returns null when acceptable, otherwise the reason.</param>
            internal ChoiceCreation(
                string entryLabel,
                string title,
                string explanation,
                string fieldLabel,
                Func<string, string> validate = null)
            {
                EntryLabel = entryLabel;
                Title = title;
                Explanation = explanation;
                FieldLabel = fieldLabel;
                Validate = validate;
            }
        }

        /// <summary>
        /// The dropdown behind <see cref="EditChoice"/>.
        /// </summary>
        /// <remarks>
        /// An <see cref="AdvancedDropdown"/> rather than a <see cref="PopupField{T}"/> because it searches.
        /// The response-type field offers every concrete type in the player assemblies, which is thousands
        /// of entries — a flat popup list is unusable at that size, and capping the list would mean the
        /// control silently could not offer some valid answers.
        /// </remarks>
        private sealed class ChoiceDropdown : AdvancedDropdown
        {
            private readonly string _heading;
            private readonly List<string> _options;
            private readonly string _emptyLabel;
            private readonly string _createLabel;
            private readonly Action<string> _pick;
            private readonly Action _create;

            internal ChoiceDropdown(
                AdvancedDropdownState state,
                string heading,
                List<string> options,
                string emptyLabel,
                string createLabel,
                Action<string> pick,
                Action create)
                : base(state)
            {
                _heading = heading;
                _options = options;
                _emptyLabel = emptyLabel;
                _createLabel = createLabel;
                _pick = pick;
                _create = create;

                minimumSize = new Vector2(280f, 340f);
            }

            protected override AdvancedDropdownItem BuildRoot()
            {
                var root = new AdvancedDropdownItem(_heading);

                root.AddChild(new AdvancedDropdownItem(_emptyLabel));
                if (!string.IsNullOrEmpty(_createLabel))
                    root.AddChild(new AdvancedDropdownItem(_createLabel));

                root.AddSeparator();

                foreach (string option in _options)
                    root.AddChild(new AdvancedDropdownItem(option));

                return root;
            }

            protected override void ItemSelected(AdvancedDropdownItem item)
            {
                // Options are matched first so a detected value always wins, even in the pathological case
                // where something is literally named "(none)".
                if (_options.Contains(item.name))
                {
                    _pick(item.name);
                    return;
                }

                if (!string.IsNullOrEmpty(_createLabel)
                    && string.Equals(item.name, _createLabel, StringComparison.Ordinal))
                {
                    _create();
                    return;
                }

                _pick(string.Empty);
            }
        }

        /// <summary>
        /// An editable list of headers: name, value, and an enabled toggle per row.
        /// </summary>
        /// <param name="heading">Section heading.</param>
        /// <param name="headers">The current headers.</param>
        /// <param name="commit">Invoked with the whole new list.</param>
        /// <param name="emptyNote">Shown when there are no headers.</param>
        internal static VisualElement EditHeaderList(
            string heading,
            IReadOnlyList<HttpHeader> headers,
            Action<List<HttpHeader>> commit,
            string emptyNote = null)
        {
            var container = new VisualElement();
            container.Add(NetworkHubUi.Heading(heading));

            var rows = new VisualElement();
            container.Add(rows);

            // As in MolcaFields.EditStringList: an unnamed header is dropped by the editing service, so a
            // newly added row is held here until it has a name rather than committed and lost to the reload.
            var working = new List<HttpHeader>();
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    if (header == null) continue;
                    working.Add(new HttpHeader(header.key, header.value) { isEnabled = header.isEnabled });
                }
            }

            var stored = CloneHeaders(working);

            void Render()
            {
                rows.Clear();

                if (working.Count == 0 && !string.IsNullOrEmpty(emptyNote))
                    rows.Add(NetworkHubUi.Note(emptyNote));

                for (int i = 0; i < working.Count; i++)
                {
                    int index = i;
                    var header = working[index];

                    var row = new VisualElement();
                    row.AddToClassList("molca-network__list-row");

                    var enabled = new Toggle { value = header.isEnabled };
                    enabled.tooltip = "Send this header.";
                    enabled.RegisterValueChangedCallback(evt =>
                    {
                        working[index].isEnabled = evt.newValue;
                        commit?.Invoke(CloneHeaders(working));
                    });
                    row.Add(enabled);

                    var key = new TextField { value = header.key };
                    key.AddToClassList("molca-network__header-key");
                    key.textEdition.placeholder = "Header";
                    MolcaFields.CommitOnBlurOrEnter(key,
                        () =>
                        {
                            working[index].key = key.value?.Trim() ?? string.Empty;
                            commit?.Invoke(CloneHeaders(working));
                        },
                        () => !string.Equals(key.value?.Trim() ?? string.Empty, header.key,
                            StringComparison.Ordinal));
                    row.Add(key);

                    var value = new TextField { value = header.value };
                    value.AddToClassList("molca-network__header-value");
                    value.textEdition.placeholder = "Value";
                    MolcaFields.CommitOnBlurOrEnter(value,
                        () =>
                        {
                            working[index].value = value.value ?? string.Empty;
                            commit?.Invoke(CloneHeaders(working));
                        },
                        () => !string.Equals(value.value ?? string.Empty, header.value,
                            StringComparison.Ordinal));
                    row.Add(value);

                    row.Add(MolcaButtons.Mini("Remove", () =>
                    {
                        working.RemoveAt(index);

                        if (SameHeaders(working, stored))
                            Render();
                        else
                            commit?.Invoke(CloneHeaders(working));
                    }));

                    rows.Add(row);
                }
            }

            Render();

            container.Add(NetworkHubUi.Actions(MolcaButtons.Mini("Add header", () =>
            {
                working.Add(new HttpHeader(string.Empty, string.Empty));
                Render();
            })));

            return container;
        }

        private static List<HttpHeader> CloneHeaders(List<HttpHeader> headers)
        {
            var clone = new List<HttpHeader>(headers.Count);
            foreach (var header in headers)
                clone.Add(new HttpHeader(header.key, header.value) { isEnabled = header.isEnabled });

            return clone;
        }

        /// <summary>Whether two header lists carry the same named headers, ignoring unnamed drafts.</summary>
        private static bool SameHeaders(List<HttpHeader> left, List<HttpHeader> right)
        {
            var a = Named(left);
            var b = Named(right);

            if (a.Count != b.Count) return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].key, b[i].key, StringComparison.Ordinal) ||
                    !string.Equals(a[i].value, b[i].value, StringComparison.Ordinal) ||
                    a[i].isEnabled != b[i].isEnabled)
                {
                    return false;
                }
            }
            return true;
        }

        private static List<HttpHeader> Named(List<HttpHeader> headers)
        {
            var kept = new List<HttpHeader>(headers.Count);
            foreach (var header in headers)
            {
                if (header != null && !string.IsNullOrWhiteSpace(header.key)) kept.Add(header);
            }
            return kept;
        }
    }
}
