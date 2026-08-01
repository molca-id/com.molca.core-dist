using System;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using Molca.Networking.Configuration;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// A modal prompt for the one input the workspace cannot take inline: a stable identifier.
    /// </summary>
    /// <remarks>
    /// IDs are primary keys — bindings, endpoints, and policy references all name them — so they are never
    /// an inline text field a keystroke can invalidate. The prompt validates against
    /// <see cref="NetworkIds"/> as you type and refuses to return a malformed ID at all, so the caller
    /// never has to handle one.
    /// </remarks>
    internal sealed class NetworkHubPrompt : EditorWindow
    {
        private string _title;
        private string _explanation;
        private string _value;
        private string _error;
        private Action<string> _onAccept;

        /// <summary>
        /// Asks for a valid identifier, blocking until the user accepts or cancels.
        /// </summary>
        /// <param name="title">Window title.</param>
        /// <param name="explanation">What this ID is used for and what changing it costs.</param>
        /// <param name="initialValue">The value to start from.</param>
        /// <returns>The accepted identifier, or <c>null</c> when cancelled.</returns>
        internal static string ForId(string title, string explanation, string initialValue)
        {
            string accepted = null;

            var window = CreateInstance<NetworkHubPrompt>();
            window._title = title;
            window._explanation = explanation;
            window._value = initialValue ?? string.Empty;
            window._onAccept = value => accepted = value;

            window.titleContent = new GUIContent(title);
            window.minSize = new Vector2(420, 190);
            window.Build();

            // Modal so the caller can return the result directly. The alternative — a callback — would
            // leave every call site re-entering the same reload logic asynchronously for no benefit.
            window.ShowModal();

            return accepted;
        }

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

            var field = new TextField("Identifier") { value = _value };
            root.Add(field);

            var error = new Label { style = { whiteSpace = WhiteSpace.Normal } };
            error.style.marginTop = 4;
            root.Add(error);

            var accept = MolcaButtons.Primary("Rename", () =>
            {
                _onAccept?.Invoke(_value);
                Close();
            });

            void Revalidate(string candidate)
            {
                _value = candidate;
                bool valid = NetworkIds.IsValid(candidate, out _error);
                error.text = valid ? string.Empty : _error;
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
