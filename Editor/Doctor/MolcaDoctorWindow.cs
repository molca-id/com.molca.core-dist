using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Standalone window shell for the Molca Doctor. The actual UI lives in the reusable
    /// <see cref="MolcaDoctorView"/>, which the Molca Hub Doctor workspace hosts as well (Sprint 26.10).
    /// </summary>
    public class MolcaDoctorWindow : EditorWindow
    {
        [MenuItem("Molca/Doctor", priority = 2)]
        public static void Open()
        {
            var window = GetWindow<MolcaDoctorWindow>("Molca Doctor");
            window.titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Molca Doctor", "doctor");
            window.minSize = new Vector2(560, 280);
        }

        // Set in OnEnable (not just Open) so the icon survives domain reloads and layout restores,
        // where Unity recreates the window without calling the menu Open() method.
        private void OnEnable() =>
            titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Molca Doctor", "doctor");

        public void CreateGUI()
        {
            rootVisualElement.Add(new MolcaDoctorView());
        }
    }
}
