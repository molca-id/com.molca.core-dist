using Molca.Editor.Hub;
using Molca.Editor.Icons;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Onboarding
{
    /// <summary>
    /// Standalone window shell for the Molca onboarding checklist. The UI lives in the reusable
    /// <see cref="MolcaOnboardingView"/> (window/view split, per <c>EDITOR_DESIGN_LANGUAGE.md</c>).
    /// </summary>
    /// <remarks>
    /// <para>The Hub's Onboarding workspace is the primary surface; this window exists for the case the Hub
    /// is not where someone starts. Both host the same view, so neither can drift from the other.</para>
    /// <para>Implements the contract in <c>Documentation~/internal/ONBOARDING_CHECKLIST.md</c>:
    /// post-compile only, opt-in, and every action writes into consumer space (<c>Assets/</c>), never into
    /// <c>Packages/</c>.</para>
    /// </remarks>
    public class MolcaOnboardingWindow : EditorWindow
    {
        /// <summary>
        /// Whether this project has already been offered the checklist once.
        /// </summary>
        /// <remarks>
        /// The key keeps its pre-rebrand spelling on purpose: the value records "this project has been
        /// nudged", which is still true, and renaming it would re-prompt every project that was already
        /// offered the old wizard. <see cref="MolcaEditorPrefs"/> already scopes it per project.
        /// </remarks>
        private const string OfferedPrefKey = "OnboardingWizard.Offered";

        /// <summary>Opens (or focuses) the standalone onboarding window.</summary>
        [MenuItem("Molca/Onboarding", priority = 1)]
        public static void Open()
        {
            var window = GetWindow<MolcaOnboardingWindow>("Molca Onboarding");
            window.titleContent = MolcaEditorIcons.WindowTitle("Molca Onboarding", "window");
            window.minSize = new Vector2(560, 420);
        }

        // Set in OnEnable (not just Open) so the icon survives domain reloads and layout restores, where
        // Unity recreates the window without calling the menu Open() method.
        private void OnEnable() =>
            titleContent = MolcaEditorIcons.WindowTitle("Molca Onboarding", "window");

        /// <summary>Builds the hosted view.</summary>
        public void CreateGUI() => rootVisualElement.Add(new MolcaOnboardingView());

        /// <summary>
        /// Offers (never forces) the checklist once per project on a fresh install, by opening the Hub on the
        /// Onboarding workspace.
        /// </summary>
        /// <remarks>
        /// <para>Deferred past the domain-reload boundary so it never runs ahead of first compile, per the
        /// contract's one hard rule: a fresh project must compile before any of this is reachable.</para>
        /// <para>Opens the Hub rather than this window because the Hub is where every other surface the
        /// checklist links to already lives — and because the activity-rail chip, which is the ongoing
        /// reminder, is a Hub affordance. The standalone window stays a menu item.</para>
        /// </remarks>
        [InitializeOnLoadMethod]
        private static void OfferOnFreshInstall()
        {
            EditorApplication.delayCall += () =>
            {
                // delayCall can fire while the editor is closing down; never act on a torn-down domain.
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    return;

                if (MolcaEditorPrefs.GetBool(OfferedPrefKey))
                    return;

                // Marked regardless of the choice below: a one-time nudge, not a recurring nag.
                MolcaEditorPrefs.SetBool(OfferedPrefKey, true);

                if (global::Molca.MolcaProjectSettings.LiveAssetExists)
                    return;

                bool openNow = EditorUtility.DisplayDialog(
                    "Molca Onboarding",
                    "This project hasn't been set up yet. Open the onboarding checklist to see what Molca "
                    + "expects and what is already in place?",
                    "Open Checklist", "Skip");

                if (openNow)
                    MolcaHubWindow.OpenWorkspace(OnboardingWorkspaceProvider.WorkspaceId);
            };
        }
    }
}
