using System;
using UnityEngine.UIElements;

namespace Molca.Editor.UI.Components
{
    /// <summary>
    /// Factory helpers for the button variants defined in the Molca editor design language.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Styling comes from <c>MolcaEditorComponents.uss</c> (<c>molca-button</c>, <c>molca-mini-button</c>,
    /// <c>molca-button--primary</c>). See <c>EDITOR_DESIGN_LANGUAGE.md</c> &gt; Buttons.
    /// </remarks>
    public static class MolcaButtons
    {
        /// <summary>Full-width bold primary action button.</summary>
        public static Button Primary(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("molca-button");
            button.AddToClassList("molca-button--primary");
            return button;
        }

        /// <summary>Compact (~20px) action button for inline/row affordances.</summary>
        public static Button Mini(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("molca-button");
            button.AddToClassList("molca-mini-button");
            return button;
        }

        /// <summary>Restrained grey toolbar button.</summary>
        public static Button Toolbar(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("molca-button");
            return button;
        }
    }
}
