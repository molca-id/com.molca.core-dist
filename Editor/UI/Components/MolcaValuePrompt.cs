using System;
using Molca.Editor.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.UI.Components
{
    /// <summary>
    /// A modal prompt for the one input a workspace will not take inline: a value that must be valid
    /// before it is written anywhere.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// <b>Base class:</b> <see cref="EditorWindow"/>. <b>Registration:</b> none — call
    /// <see cref="ForValue"/>, which creates, shows, and disposes the window.
    /// <para>
    /// Identifiers are primary keys: a catalog binding, a content package dependency, and an installed
    /// package on a device all name them. An inline text field bound to a setter rewrites the key on
    /// every keystroke, so each half-typed state is a real, briefly-persisted id. This prompt validates
    /// as you type and refuses to return a malformed value at all, so the caller never has to handle one.
    /// </para>
    /// <para>
    /// It also serves the "create new" entry behind a strict dropdown. Those fields refuse free typing
    /// precisely because it is unreliable, so the escape hatch has to be a deliberate act with its own
    /// validation rather than a text box that quietly accepts anything.
    /// </para>
    /// </remarks>
    public sealed class MolcaValuePrompt : EditorWindow
    {
        private string _title;
        private string _explanation;
        private string _fieldLabel;
        private string _acceptLabel;
        private string _value;
        private Func<string, string> _validate;
        private Action<string> _onAccept;

        /// <summary>
        /// Asks for a value the caller validates, blocking until the user accepts or cancels.
        /// </summary>
        /// <param name="title">Window title.</param>
        /// <param name="explanation">What the value is used for, and what changing it costs.</param>
        /// <param name="fieldLabel">Label on the input.</param>
        /// <param name="initialValue">The value to start from.</param>
        /// <param name="acceptLabel">Text on the accept button, e.g. <c>Add</c> or <c>Rename</c>.</param>
        /// <param name="validate">
        /// Returns null or empty when the candidate is acceptable, otherwise the reason it is not. Null
        /// accepts anything non-blank.
        /// </param>
        /// <returns>The accepted value, or <c>null</c> when cancelled.</returns>
        public static string ForValue(
            string title,
            string explanation,
            string fieldLabel,
            string initialValue,
            string acceptLabel,
            Func<string, string> validate = null)
        {
            string accepted = null;

            var window = CreateInstance<MolcaValuePrompt>();
            window._title = title;
            window._explanation = explanation;
            window._fieldLabel = string.IsNullOrEmpty(fieldLabel) ? "Value" : fieldLabel;
            window._acceptLabel = string.IsNullOrEmpty(acceptLabel) ? "OK" : acceptLabel;
            window._value = initialValue ?? string.Empty;
            window._validate = validate ?? DefaultValidate;
            window._onAccept = value => accepted = value;

            window.titleContent = new GUIContent(title);
            window.minSize = new Vector2(420, 190);
            window.Build();

            // Modal so the caller can return the result directly. The alternative — a callback — would
            // leave every call site re-entering the same reload logic asynchronously for no benefit.
            window.ShowModal();

            return accepted;
        }

        /// <summary>Rejects only a blank value, for callers whose values have no other rule.</summary>
        private static string DefaultValidate(string candidate) =>
            string.IsNullOrWhiteSpace(candidate) ? "Enter a value." : null;

        private void Build()
        {
            var root = rootVisualElement;
            MolcaEditorUi.Apply(root);
            root.style.paddingLeft = 12;
            root.style.paddingRight = 12;
            root.style.paddingTop = 12;
            root.style.paddingBottom = 12;

            var heading = new Label(_title);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            root.Add(heading);

            var explanation = new Label(_explanation) { style = { whiteSpace = WhiteSpace.Normal } };
            explanation.style.marginBottom = 8;
            root.Add(explanation);

            var field = new TextField(_fieldLabel) { value = _value };
            root.Add(field);

            var error = new Label { style = { whiteSpace = WhiteSpace.Normal } };
            error.style.marginTop = 4;
            root.Add(error);

            var accept = MolcaButtons.Primary(_acceptLabel, () =>
            {
                _onAccept?.Invoke(_value);
                Close();
            });

            void Revalidate(string candidate)
            {
                _value = candidate;
                string reason = _validate(candidate);
                bool valid = string.IsNullOrEmpty(reason);
                error.text = valid ? string.Empty : reason;
                accept.SetEnabled(valid);
            }

            field.RegisterValueChangedCallback(evt => Revalidate(evt.newValue));
            Revalidate(_value);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.justifyContent = Justify.FlexEnd;
            actions.style.marginTop = 8;
            actions.Add(MolcaButtons.Mini("Cancel", Close));
            actions.Add(accept);
            root.Add(actions);

            field.schedule.Execute(() => field.Focus());
        }
    }
}
