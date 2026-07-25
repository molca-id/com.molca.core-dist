using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Onboarding
{
    /// <summary>
    /// Standalone window shell for the Molca Onboarding Wizard. The actual UI lives in the reusable
    /// <see cref="OnboardingWizardView"/> (window/view split, per <c>EDITOR_DESIGN_LANGUAGE.md</c>).
    /// </summary>
    /// <remarks>
    /// Implements the wizard contract in
    /// <c>Documentation~/internal/ONBOARDING_WIZARD.md</c>: every step here is post-compile,
    /// opt-in, and writes only into consumer space (<c>Assets/</c>), never into <c>Packages/</c>.
    /// </remarks>
    public class OnboardingWizardWindow : EditorWindow
    {
        // MolcaEditorPrefs already scopes the key per project (by dataPath hash), so a
        // single stable key is enough — no manual per-project suffix needed.
        private const string OfferedPrefKey = "OnboardingWizard.Offered";

        [MenuItem("Molca/Onboarding Wizard", priority = 1)]
        public static void Open()
        {
            var window = GetWindow<OnboardingWizardWindow>("Molca Onboarding");
            window.titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Molca Onboarding", "window");
            window.minSize = new Vector2(560, 420);
        }

        // Set in OnEnable (not just Open) so the icon survives domain reloads and layout restores,
        // where Unity recreates the window without calling the menu Open() method.
        private void OnEnable() =>
            titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Molca Onboarding", "window");

        public void CreateGUI()
        {
            rootVisualElement.Add(new OnboardingWizardView());
        }

        /// <summary>
        /// Offers (never forces) the wizard once per project on a fresh install: only when the
        /// consumer-space <see cref="Molca.Settings.MolcaProjectSettings"/> asset hasn't been created yet
        /// and this project hasn't been offered before. Deferred past the domain-reload boundary so it
        /// never runs ahead of first compile, per the wizard contract's one hard rule.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void OfferOnFreshInstall()
        {
            EditorApplication.delayCall += () =>
            {
                // EditorApplication.delayCall can fire after the window/editor is already closing down;
                // guard against acting on a torn-down domain.
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    return;

                if (MolcaEditorPrefs.GetBool(OfferedPrefKey, false))
                    return;

                // Mark as offered regardless of the user's choice below — this is a one-time nudge, not a
                // recurring nag, matching the "opt-in" language in the wizard contract.
                MolcaEditorPrefs.SetBool(OfferedPrefKey, true);

                if (Molca.MolcaProjectSettings.LiveAssetExists)
                    return;

                bool openNow = EditorUtility.DisplayDialog(
                    "Molca Onboarding",
                    "This project hasn't been set up yet. Run the onboarding wizard to seed project settings, " +
                    "optional SDK starter config, and the MCP proxy?",
                    "Open Wizard", "Skip");
                if (openNow)
                    Open();
            };
        }
    }
}
