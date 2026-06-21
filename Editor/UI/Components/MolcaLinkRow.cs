using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.UI.Components
{
    /// <summary>
    /// Read-only URL rendered as link-colored text with an adjacent <c>Open</c> mini button.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/Components/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Implements the design-language Links rule: opening a link uses <see cref="Application.OpenURL"/>
    /// explicitly via a visible control — never as a hidden side effect of passive row selection
    /// (<c>EDITOR_DESIGN_LANGUAGE.md</c> &gt; Links).
    /// </remarks>
    public sealed class MolcaLinkRow : VisualElement
    {
        /// <summary>Creates a link row showing <paramref name="url"/> with an Open button.</summary>
        /// <param name="url">The URL to display and open.</param>
        /// <param name="displayText">Optional override for the visible text (defaults to the URL).</param>
        public MolcaLinkRow(string url, string displayText = null)
        {
            AddToClassList("molca-link-row");

            var link = new Button(() => OpenUrl(url)) { text = string.IsNullOrEmpty(displayText) ? url : displayText };
            link.AddToClassList("molca-link-button");
            link.tooltip = url;
            Add(link);

            var open = new Button(() => OpenUrl(url)) { text = "Open" };
            open.AddToClassList("molca-button");
            open.AddToClassList("molca-mini-button");
            Add(open);
        }

        private static void OpenUrl(string url)
        {
            if (!string.IsNullOrEmpty(url)) Application.OpenURL(url);
        }
    }
}
