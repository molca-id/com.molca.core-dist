using System;
using System.Collections.Generic;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.UI.Components
{
    /// <summary>
    /// The editable counterpart to the read-only field row: one control per kind of authored value, laid
    /// out on the shared <c>.molca-field-row</c> grid so an editable row and a read-out row line up in the
    /// same card.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// <b>Base class:</b> static factory over <see cref="VisualElement"/>. <b>Registration:</b> call from
    /// any root that has received <see cref="MolcaEditorUi.Apply"/>; styling comes from
    /// <c>MolcaEditorComponents.uss</c>.
    /// <para>
    /// <b>Every control takes a commit callback and writes nothing itself.</b> The view passes a callback
    /// that goes through that domain's editing service — <c>NetworkCatalogEditingService</c>,
    /// <c>ContentPackageEditingService</c> — which stays the only write path. That is what keeps
    /// validation, Undo grouping, and the refusal messages identical whether an edit came from a Hub
    /// workspace, an MCP tool, or a test.
    /// </para>
    /// <para>
    /// This started as the Network workspace's private field vocabulary. It is shared because Content
    /// needed the same controls with the same commit semantics, and a second copy is how the editor ended
    /// up with two navigation rails that disagreed — the subtle parts below are exactly the parts a
    /// re-implementation gets wrong.
    /// </para>
    /// <para>
    /// <b>Commit timing differs by control on purpose.</b> A text or number field commits on blur and on
    /// Enter, because committing per keystroke would make every character its own Undo entry and would
    /// flash half-typed values as validation errors. A toggle, enum, or dropdown commits immediately —
    /// those have no intermediate state to protect.
    /// </para>
    /// <para>
    /// Controls also suppress a commit whose value is unchanged, so tabbing through a card does not
    /// produce a run of empty Undo steps and reloads.
    /// </para>
    /// </remarks>
    public static class MolcaFields
    {
        /// <summary>The label shown for an optional reference that falls through to another layer.</summary>
        public const string InheritLabel = "(inherit)";

        /// <summary>The label shown for an optional reference meaning "nothing".</summary>
        public const string NoneLabel = "(none)";

        /// <summary>
        /// A label plus control on the shared compact field grid.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="control">The editing control.</param>
        /// <param name="tooltip">Optional tooltip on the whole row.</param>
        /// <param name="top">Whether the label aligns to the top, for a multi-line control.</param>
        /// <returns>The assembled row.</returns>
        public static VisualElement Row(
            string label,
            VisualElement control,
            string tooltip = null,
            bool top = false)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-field-row");
            if (top) row.AddToClassList("molca-field-row--top");
            if (!string.IsNullOrEmpty(tooltip)) row.tooltip = tooltip;

            var key = new Label(label ?? string.Empty);
            key.AddToClassList("molca-field-label");
            row.Add(key);

            if (control != null)
            {
                control.AddToClassList("molca-field-control");
                row.Add(control);
            }

            return row;
        }

        /// <summary>
        /// A read-only value on the same grid as the editable rows.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The value; an em dash is shown when empty.</param>
        /// <param name="tooltip">Optional tooltip on the whole row.</param>
        /// <returns>The assembled row.</returns>
        public static VisualElement ReadOnly(string label, string value, string tooltip = null)
        {
            var text = new Label(string.IsNullOrEmpty(value) ? "—" : value)
            {
                style = { whiteSpace = WhiteSpace.Normal },
            };
            if (string.IsNullOrEmpty(value)) text.AddToClassList("molca-muted");

            return Row(label, text, tooltip);
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
        /// <returns>The assembled row.</returns>
        public static VisualElement EditText(
            string label,
            string value,
            Action<string> commit,
            string tooltip = null,
            string placeholder = null,
            bool trim = true)
        {
            var field = new TextField { value = value ?? string.Empty };
            if (!string.IsNullOrEmpty(placeholder))
                field.textEdition.placeholder = placeholder;

            string Current() => trim
                ? field.value?.Trim() ?? string.Empty
                : field.value ?? string.Empty;

            CommitOnBlurOrEnter(field, () => commit?.Invoke(Current()),
                () => !string.Equals(Current(), value ?? string.Empty, StringComparison.Ordinal));

            return Row(label, field, tooltip);
        }

        /// <summary>
        /// A multi-line text area committing on blur.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the new value when it differs from <paramref name="value"/>.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        /// <returns>The assembled row.</returns>
        /// <remarks>
        /// Enter inserts a newline here rather than committing, so blur is the only commit. That is the
        /// opposite of <see cref="EditText"/> and is the behaviour a multi-line field has everywhere else
        /// in the editor.
        /// </remarks>
        public static VisualElement EditTextArea(
            string label,
            string value,
            Action<string> commit,
            string tooltip = null)
        {
            var field = new TextField { value = value ?? string.Empty, multiline = true };
            field.AddToClassList("molca-field-textarea");

            field.RegisterCallback<BlurEvent>(_ =>
            {
                string next = field.value ?? string.Empty;
                if (!string.Equals(next, value ?? string.Empty, StringComparison.Ordinal))
                    commit?.Invoke(next);
            });

            return Row(label, field, tooltip, top: true);
        }

        /// <summary>
        /// A checkbox committing immediately.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the new value.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        /// <returns>The assembled row.</returns>
        public static VisualElement EditToggle(
            string label,
            bool value,
            Action<bool> commit,
            string tooltip = null)
        {
            var field = new Toggle { value = value };
            field.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue != value) commit?.Invoke(evt.newValue);
            });

            return Row(label, field, tooltip);
        }

        /// <summary>
        /// An enum dropdown committing immediately.
        /// </summary>
        /// <typeparam name="TEnum">The enum type.</typeparam>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the new value.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        /// <returns>The assembled row.</returns>
        public static VisualElement EditEnum<TEnum>(
            string label,
            TEnum value,
            Action<TEnum> commit,
            string tooltip = null)
            where TEnum : Enum
        {
            var field = new EnumField(value);
            field.RegisterValueChangedCallback(evt =>
            {
                if (!Equals(evt.newValue, value)) commit?.Invoke((TEnum)evt.newValue);
            });

            return Row(label, field, tooltip);
        }

        /// <summary>
        /// A flags dropdown for a <see cref="FlagsAttribute"/> enum, committing immediately.
        /// </summary>
        /// <typeparam name="TEnum">The flags enum type.</typeparam>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current mask.</param>
        /// <param name="commit">Invoked with the new mask.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        /// <returns>The assembled row.</returns>
        public static VisualElement EditFlags<TEnum>(
            string label,
            TEnum value,
            Action<TEnum> commit,
            string tooltip = null)
            where TEnum : Enum
        {
            var field = new EnumFlagsField(value);
            field.RegisterValueChangedCallback(evt =>
            {
                if (!Equals(evt.newValue, value)) commit?.Invoke((TEnum)evt.newValue);
            });

            return Row(label, field, tooltip);
        }

        /// <summary>
        /// An integer field, clamped to the serialized field's range, committing on blur and Enter.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the clamped new value.</param>
        /// <param name="min">Lowest accepted value.</param>
        /// <param name="max">Highest accepted value.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        /// <returns>The assembled row.</returns>
        /// <remarks>
        /// Clamped here as well as in the editing service. The service is the guarantee; doing it here too
        /// means the number the author sees after committing is the number that was stored.
        /// </remarks>
        public static VisualElement EditInt(
            string label,
            int value,
            Action<int> commit,
            int min,
            int max,
            string tooltip = null)
        {
            var field = new IntegerField { value = value };

            CommitOnBlurOrEnter(field,
                () => commit?.Invoke(Mathf.Clamp(field.value, min, max)),
                () => Mathf.Clamp(field.value, min, max) != value);

            return Row(label, field, tooltip);
        }

        /// <summary>
        /// A float field with a lower bound, committing on blur and Enter.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="commit">Invoked with the clamped new value.</param>
        /// <param name="min">Lowest accepted value.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        /// <returns>The assembled row.</returns>
        public static VisualElement EditFloat(
            string label,
            float value,
            Action<float> commit,
            float min = 0f,
            string tooltip = null)
        {
            var field = new FloatField { value = value };

            CommitOnBlurOrEnter(field,
                () => commit?.Invoke(Mathf.Max(min, field.value)),
                () => !Mathf.Approximately(Mathf.Max(min, field.value), value));

            return Row(label, field, tooltip);
        }

        /// <summary>
        /// A byte-count field where zero reads as unbounded, committing on blur and Enter.
        /// </summary>
        /// <param name="label">Field name.</param>
        /// <param name="value">The current value; 0 means unbounded.</param>
        /// <param name="commit">Invoked with the new value.</param>
        /// <param name="tooltip">Optional tooltip.</param>
        /// <returns>The assembled row.</returns>
        public static VisualElement EditByteSize(
            string label,
            long value,
            Action<long> commit,
            string tooltip = null)
        {
            var field = new LongField { value = value };
            field.textEdition.placeholder = "0 = unbounded";

            CommitOnBlurOrEnter(field,
                () => commit?.Invoke(Math.Max(0L, field.value)),
                () => Math.Max(0L, field.value) != value);

            return Row(label, field, tooltip);
        }

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
        /// <returns>The assembled row.</returns>
        /// <remarks>
        /// A dropdown rather than a text field because these are foreign keys. Typing one lets an author
        /// name something that does not exist, which resolves to a fallback and looks like the override was
        /// ignored. A missing current value is still shown, marked, so a reference to something already
        /// deleted reads as broken instead of silently snapping to the sentinel.
        /// </remarks>
        public static VisualElement EditReference(
            string label,
            string value,
            IReadOnlyList<string> options,
            Action<string> commit,
            string emptyLabel = InheritLabel,
            string tooltip = null)
        {
            var choices = new List<string> { emptyLabel };
            if (options != null)
            {
                foreach (string option in options)
                {
                    if (!string.IsNullOrEmpty(option)) choices.Add(option);
                }
            }

            string current = string.IsNullOrEmpty(value) ? emptyLabel : value;

            // A reference to something that no longer exists must stay visible rather than being
            // normalized away, so it is added as a marked choice.
            if (!choices.Contains(current))
                choices.Add(current + "  ·  missing");

            string selected = choices.Contains(current) ? current : current + "  ·  missing";

            var field = new PopupField<string>(choices, selected);
            field.RegisterValueChangedCallback(evt =>
            {
                if (string.Equals(evt.newValue, selected, StringComparison.Ordinal))
                    return;

                commit?.Invoke(string.Equals(evt.newValue, emptyLabel, StringComparison.Ordinal)
                    ? string.Empty
                    : evt.newValue);
            });

            return Row(label, field, tooltip);
        }

        /// <summary>
        /// An editable list of strings: one row per entry with a remove action, plus an add row.
        /// </summary>
        /// <param name="heading">Section heading, or null for no heading.</param>
        /// <param name="values">The current entries.</param>
        /// <param name="commit">Invoked with the whole new list.</param>
        /// <param name="entryLabel">What one entry is called, used on the add button and placeholder.</param>
        /// <param name="emptyNote">Explains what an empty list means. Shown when there are no entries.</param>
        /// <param name="tooltip">Optional tooltip on each entry.</param>
        /// <returns>The assembled container.</returns>
        /// <remarks>
        /// The whole list is committed on every change rather than one entry at a time, because a service
        /// validates these as a set — a malformed entry must reject the edit without leaving earlier
        /// entries applied.
        /// </remarks>
        public static VisualElement EditStringList(
            string heading,
            IReadOnlyList<string> values,
            Action<List<string>> commit,
            string entryLabel,
            string emptyNote = null,
            string tooltip = null)
        {
            var container = new VisualElement();
            if (!string.IsNullOrEmpty(heading)) container.Add(Heading(heading));

            var rows = new VisualElement();
            container.Add(rows);

            // The stored list, plus any row the author has added but not yet filled in. A blank entry lives
            // here and nowhere else: an editing service drops blanks, so committing one would reload the
            // view and the new row would vanish as it was created.
            var working = new List<string>();
            if (values != null)
            {
                foreach (string value in values) working.Add(value);
            }

            var stored = new List<string>(working);

            void Render()
            {
                rows.Clear();

                if (working.Count == 0 && !string.IsNullOrEmpty(emptyNote))
                    rows.Add(Note(emptyNote));

                for (int i = 0; i < working.Count; i++)
                {
                    int index = i;
                    string entry = working[index];

                    var row = new VisualElement();
                    row.AddToClassList("molca-field-list-row");
                    if (!string.IsNullOrEmpty(tooltip)) row.tooltip = tooltip;

                    var field = new TextField { value = entry };
                    field.AddToClassList("molca-field-list-entry");
                    field.textEdition.placeholder = entryLabel;

                    CommitOnBlurOrEnter(field,
                        () =>
                        {
                            working[index] = field.value?.Trim() ?? string.Empty;
                            commit?.Invoke(new List<string>(working));
                        },
                        () => !string.Equals(field.value?.Trim() ?? string.Empty, entry,
                            StringComparison.Ordinal));

                    row.Add(field);
                    row.Add(MolcaButtons.Mini("Remove", () =>
                    {
                        working.RemoveAt(index);

                        // Removing a row the author never filled in changes nothing stored, so it is
                        // dropped locally rather than through a commit that would reload for no reason.
                        // Compared by content, not by count: a blank draft row sitting in the list must
                        // not mask the removal of a real one.
                        if (SameEntries(working, stored))
                            Render();
                        else
                            commit?.Invoke(new List<string>(working));
                    }));

                    rows.Add(row);
                }
            }

            Render();

            container.Add(Actions(MolcaButtons.Mini($"Add {entryLabel}", () =>
            {
                working.Add(string.Empty);
                Render();
            })));

            return container;
        }

        /// <summary>A subheading inside a card body.</summary>
        /// <param name="text">The heading.</param>
        /// <returns>The heading label.</returns>
        public static Label Heading(string text)
        {
            var label = new Label(text ?? string.Empty);
            label.AddToClassList("molca-field-heading");
            return label;
        }

        /// <summary>A muted note, for empty states and explanations.</summary>
        /// <param name="text">The note.</param>
        /// <returns>The note label.</returns>
        public static Label Note(string text)
        {
            var label = new Label(text ?? string.Empty) { style = { whiteSpace = WhiteSpace.Normal } };
            label.AddToClassList("molca-muted");
            return label;
        }

        /// <summary>A trailing row of actions.</summary>
        /// <param name="actions">The action controls; nulls are skipped.</param>
        /// <returns>The action row.</returns>
        public static VisualElement Actions(params VisualElement[] actions)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-field-actions");
            if (actions != null)
            {
                foreach (var action in actions)
                {
                    if (action != null) row.Add(action);
                }
            }
            return row;
        }

        /// <summary>
        /// Whether two string lists carry the same entries once blanks are ignored.
        /// </summary>
        /// <remarks>
        /// Blanks are ignored because they are what an editing service drops, so a list differing only in
        /// blank entries would commit to no observable change — a reload and an empty Undo step.
        /// </remarks>
        private static bool SameEntries(List<string> left, List<string> right)
        {
            var a = NonBlank(left);
            var b = NonBlank(right);

            if (a.Count != b.Count) return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static List<string> NonBlank(List<string> values)
        {
            var kept = new List<string>(values.Count);
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) kept.Add(value.Trim());
            }
            return kept;
        }

        /// <summary>
        /// Wires a text-based field to commit on blur and on Enter, once, and only when changed.
        /// </summary>
        /// <param name="field">The field to wire.</param>
        /// <param name="commit">The commit action.</param>
        /// <param name="hasChanged">Whether the field's value differs from the stored one.</param>
        /// <remarks>
        /// The guard matters more than it looks: a commit reloads the owning view, which rebuilds these
        /// controls. Without it, tabbing across a card would fire a reload per field, and each would be an
        /// Undo step recording no change.
        /// </remarks>
        public static void CommitOnBlurOrEnter(VisualElement field, Action commit, Func<bool> hasChanged)
        {
            if (field == null || commit == null) return;

            bool committed = false;

            void Commit()
            {
                // Enter commits and then blurs, which would otherwise run the same commit twice — the
                // second time against a value the reload has already replaced.
                if (committed || (hasChanged != null && !hasChanged())) return;

                committed = true;
                commit();
            }

            field.RegisterCallback<BlurEvent>(_ => Commit());
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                    Commit();
            });
        }
    }
}
