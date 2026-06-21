using UnityEditor;
using UnityEngine.UIElements;

namespace Molca.Editor.UI
{
    /// <summary>
    /// Entry point for applying the shared Molca editor design language to a custom editor surface.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/</c>.
    /// Registration: call <see cref="Apply"/> on a window/view root once, after the root exists.
    /// This is the single place that loads <c>MolcaEditorTokens.uss</c> and tags the root with the
    /// <c>molca-editor</c> (and skin-aware <c>molca-light</c>) classes documented in
    /// <c>Documentation~/EDITOR_DESIGN_LANGUAGE.md</c>. New editor UI should call this instead of
    /// copying token hex values, so every surface resolves colors from one source.
    /// </remarks>
    public static class MolcaEditorUi
    {
        /// <summary>Root class that carries the shared <c>--molca-*</c> design tokens.</summary>
        public const string RootClass = "molca-editor";

        /// <summary>Class added to the root under the light editor skin for legibility overrides.</summary>
        public const string LightClass = "molca-light";

        private const string TokensAssetPath =
            "Packages/com.molca.core/Editor/UI/MolcaEditorTokens.uss";

        private const string ComponentsAssetPath =
            "Packages/com.molca.core/Editor/UI/Components/MolcaEditorComponents.uss";

        /// <summary>
        /// Loads the shared token stylesheet onto <paramref name="root"/> and applies the
        /// <see cref="RootClass"/> (plus <see cref="LightClass"/> under the light skin).
        /// </summary>
        /// <param name="root">The window or hosted-view root element to style.</param>
        /// <remarks>Idempotent: re-applying re-adds the same classes and stylesheet at most once.</remarks>
        public static void Apply(VisualElement root)
        {
            if (root == null) return;

            var tokens = AssetDatabase.LoadAssetAtPath<StyleSheet>(TokensAssetPath);
            if (tokens != null && !root.styleSheets.Contains(tokens))
                root.styleSheets.Add(tokens);

            var components = AssetDatabase.LoadAssetAtPath<StyleSheet>(ComponentsAssetPath);
            if (components != null && !root.styleSheets.Contains(components))
                root.styleSheets.Add(components);

            root.AddToClassList(RootClass);
            root.EnableInClassList(LightClass, !EditorGUIUtility.isProSkin);
        }
    }
}
