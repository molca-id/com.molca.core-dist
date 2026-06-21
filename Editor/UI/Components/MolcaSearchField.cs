using System;
using UnityEngine.UIElements;

namespace Molca.Editor.UI.Components
{
    /// <summary>
    /// Single-line search field with an in-field placeholder overlay (no external label).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Implements the design-language rule that search labels are placeholders inside the field, not
    /// external labels (<c>EDITOR_DESIGN_LANGUAGE.md</c> &gt; Search Fields). Styling comes from the
    /// <c>molca-search</c>/<c>molca-search-placeholder</c> classes in <c>MolcaEditorComponents.uss</c>.
    /// </remarks>
    public sealed class MolcaSearchField : VisualElement
    {
        private readonly TextField _field;
        private readonly Label _placeholder;

        /// <summary>Raised when the search text changes; argument is the trimmed query.</summary>
        public event Action<string> OnSearchChanged;

        /// <summary>Current trimmed search text.</summary>
        public string Value => (_field.value ?? string.Empty).Trim();

        /// <summary>Creates a search field with the given placeholder caption.</summary>
        /// <param name="placeholder">Greyed prompt shown while the field is empty.</param>
        public MolcaSearchField(string placeholder = "Search")
        {
            AddToClassList("molca-search");

            _field = new TextField { label = string.Empty };
            _field.SetValueWithoutNotify(string.Empty);
            Add(_field);

            _placeholder = new Label(placeholder) { pickingMode = PickingMode.Ignore };
            _placeholder.AddToClassList("molca-search-placeholder");
            _field.Add(_placeholder);

            _field.RegisterValueChangedCallback(evt =>
            {
                var trimmed = (evt.newValue ?? string.Empty).Trim();
                _placeholder.style.display = string.IsNullOrEmpty(trimmed) ? DisplayStyle.Flex : DisplayStyle.None;
                OnSearchChanged?.Invoke(trimmed);
            });
        }

        /// <summary>Clears the field text (raises <see cref="OnSearchChanged"/>).</summary>
        /// <remarks>
        /// Intentionally hides <see cref="VisualElement.Clear"/> (which removes child elements):
        /// for a search field, "clear" means emptying the query text, not the visual tree.
        /// </remarks>
        public new void Clear() => _field.value = string.Empty;
    }
}
