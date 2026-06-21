using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Molca.Editor.UI.Components
{
    /// <summary>
    /// Stable-width navigation rail of selectable rows for switching the primary detail context.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Implements the design-language Rails component: fixed 188px width, selected row uses
    /// <c>--molca-row-selected</c> with a 2px <c>--molca-accent</c> left border, no rounded
    /// per-row card treatment (<c>EDITOR_DESIGN_LANGUAGE.md</c> &gt; Rails And Profile Lists).
    /// </remarks>
    public sealed class MolcaRail : VisualElement
    {
        private readonly List<Button> _items = new List<Button>();

        /// <summary>Raised with the row key when a different row is selected.</summary>
        public event Action<string> OnSelected;

        /// <summary>Currently selected row key, or null.</summary>
        public string SelectedKey { get; private set; }

        /// <summary>Creates an empty rail.</summary>
        public MolcaRail()
        {
            AddToClassList("molca-rail");
        }

        /// <summary>Adds a selectable row.</summary>
        /// <param name="key">Stable identity used for selection and reporting.</param>
        /// <param name="label">Display text.</param>
        /// <returns>The created row button (for tooltip/icon customization).</returns>
        public Button AddItem(string key, string label)
        {
            var row = new Button(() => Select(key)) { text = label };
            row.AddToClassList("molca-rail-item");
            row.userData = key;
            _items.Add(row);
            Add(row);
            return row;
        }

        /// <summary>Selects the row with the given key and raises <see cref="OnSelected"/> if it changed.</summary>
        public void Select(string key)
        {
            bool changed = SelectedKey != key;
            SelectedKey = key;

            foreach (var item in _items)
                item.EnableInClassList("molca-rail-item--selected", (string)item.userData == key);

            if (changed) OnSelected?.Invoke(key);
        }

        /// <summary>Removes all rows and clears selection.</summary>
        public void ClearItems()
        {
            _items.Clear();
            Clear();
            SelectedKey = null;
        }
    }
}
