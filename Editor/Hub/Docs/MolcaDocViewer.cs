using System;
using System.IO;
using Molca.Editor.UI;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Docs
{
    /// <summary>
    /// Renders a single reference doc into the Molca Hub detail pane: reads the Markdown source from disk
    /// and renders it via <see cref="MolcaMarkdown"/> with the <c>molca://</c> asset/doc link scheme wired in.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Docs/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> as the content of a docs rail leaf. Front-matter
    /// is stripped by <see cref="MolcaMarkdown"/> so only the body renders. A <c>molca://doc/&lt;id&gt;</c> link
    /// invokes <paramref name="onNavigateDoc"/> to switch the rail to another doc.
    /// </remarks>
    internal sealed class MolcaDocViewer : VisualElement
    {
        internal MolcaDocViewer(MolcaDocEntry entry, Action<string> onNavigateDoc)
        {
            AddToClassList("molca-hub-docs-body");
            if (entry == null) return;

            string text;
            try
            {
                text = File.ReadAllText(entry.AbsolutePath);
            }
            catch (Exception e)
            {
                var err = new Label($"Could not read {Path.GetFileName(entry.AbsolutePath)}: {e.Message}");
                err.AddToClassList("molca-hub-muted");
                Add(err);
                return;
            }

            MolcaMarkdown.Render(this, text, MolcaEditorDocLinks.OptionsFor(onNavigateDoc));
        }
    }
}
