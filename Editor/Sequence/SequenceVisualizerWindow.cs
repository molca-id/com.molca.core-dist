using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Standalone window shell for the sequence visualizer. The UI lives in the reusable
    /// <see cref="SequenceVisualizerView"/>, which the Molca Hub Sequence workspace hosts as well
    /// (Sprint 26.10).
    /// </summary>
    public class SequenceVisualizerWindow : EditorWindow
    {
        [MenuItem("Molca/Sequence/Sequence Visualizer", priority = 21)]
        public static void ShowWindow()
        {
            var window = GetWindow<SequenceVisualizerWindow>("Sequence Visualizer");
            window.titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Sequence Visualizer", "sequence");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        private void OnEnable() =>
            titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Sequence Visualizer", "sequence");

        public void CreateGUI()
        {
            // Shared editor design tokens on the root (Sprint 27.4); the Hub host applies them on its
            // own root, so the hosted SequenceVisualizerView gets the language in both surfaces.
            Molca.Editor.UI.MolcaEditorUi.Apply(rootVisualElement);
            rootVisualElement.Add(new SequenceVisualizerView(message => ShowNotification(new GUIContent(message))));
        }
    }
}
