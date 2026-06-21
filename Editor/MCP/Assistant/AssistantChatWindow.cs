using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Standalone window shell for the in-editor assistant chat. The UI lives in the reusable
    /// <see cref="AssistantChatView"/>, which the Molca Hub Assistant workspace hosts as well (Sprint 26.10).
    /// </summary>
    public class AssistantChatWindow : EditorWindow
    {
        /// <summary>Opens (or focuses) the assistant chat window.</summary>
        [MenuItem("Molca/Assistant Chat", priority = 1)]
        public static void Open()
        {
            var window = GetWindow<AssistantChatWindow>();
            window.titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Molca Assistant", "mcp");
            window.minSize = new Vector2(360, 320);
            window.Show();
        }

        private void OnEnable() =>
            titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Molca Assistant", "mcp");

        public void CreateGUI()
        {
            rootVisualElement.Add(new AssistantChatView(message => ShowNotification(new GUIContent(message))));
        }
    }
}
