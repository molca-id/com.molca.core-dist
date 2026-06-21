using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor
{
    /// <summary>
    /// Shared editor authoring for UI Toolkit: creates <see cref="PanelSettings"/> assets (themed with the
    /// shipped Molca theme) and configured <see cref="UIDocument"/> components.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UIToolkit/</c>.
    /// Registration: static editor utility; not an asset. Single source of truth for both the
    /// <c>molca_unity_uitk_*</c> MCP tools and the Figma scaffolding orchestrator (<c>molca_figma_build_panel</c>),
    /// so the wiring logic is not duplicated across the two MCP providers. Main thread only.
    /// </remarks>
    public static class UiToolkitAuthoringService
    {
        /// <summary>Package-relative path of the shipped Molca default runtime theme.</summary>
        public const string MolcaDefaultThemePath =
            "Packages/com.molca.core/Runtime/UIToolkit/MolcaDefaultTheme.tss";

        /// <summary>The outcome of creating a <see cref="PanelSettings"/> asset.</summary>
        public readonly struct PanelSettingsResult
        {
            internal PanelSettingsResult(PanelSettings panel, string assetPath, string themeSource, string themeWarning)
            {
                Panel = panel;
                AssetPath = assetPath;
                ThemeSource = themeSource;
                ThemeWarning = themeWarning;
            }

            /// <summary>The created PanelSettings instance.</summary>
            public PanelSettings Panel { get; }
            /// <summary>The project-relative asset path written.</summary>
            public string AssetPath { get; }
            /// <summary>Where the assigned theme came from ("override"/"molca"/"project"/"none").</summary>
            public string ThemeSource { get; }
            /// <summary>A note when the theme result is degraded, else <c>null</c>.</summary>
            public string ThemeWarning { get; }
        }

        /// <summary>
        /// Creates a <see cref="PanelSettings"/> asset under <paramref name="folder"/> and assigns the resolved
        /// theme (the Molca default unless <paramref name="themeOverridePath"/> is given).
        /// </summary>
        /// <param name="folder">Project-relative folder (default <c>Assets/UI Toolkit</c> when null/empty).</param>
        /// <param name="name">Asset name without extension (default <c>PanelSettings</c>).</param>
        /// <param name="themeOverridePath">An explicit <c>.tss</c> path, or null to use the Molca default.</param>
        /// <param name="result">The created asset and theme metadata on success.</param>
        /// <param name="error">A human-readable error on failure, else <c>null</c>.</param>
        /// <returns><c>true</c> on success.</returns>
        public static bool CreatePanelSettings(
            string folder, string name, string themeOverridePath, out PanelSettingsResult result, out string error)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(folder)) folder = "Assets/UI Toolkit";
            if (!EnsureAssetFolder(folder, out error)) return false;
            if (string.IsNullOrWhiteSpace(name)) name = "PanelSettings";

            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            var theme = ResolveTheme(themeOverridePath, out string themeSource, out string themeWarning);
            if (theme != null) panel.themeStyleSheet = theme;

            string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.asset");
            AssetDatabase.CreateAsset(panel, assetPath);
            AssetDatabase.SaveAssets();

            result = new PanelSettingsResult(panel, assetPath, themeSource, themeWarning);
            return true;
        }

        /// <summary>
        /// Creates a GameObject (under an optional parent) with a configured <see cref="UIDocument"/>. Registered
        /// as one Unity Undo group via <see cref="GameObjectEditingService"/>.
        /// </summary>
        /// <param name="name">Name for the new GameObject (default <c>UIDocument</c>).</param>
        /// <param name="parent">Optional parent GameObject (scene root when null).</param>
        /// <param name="uxml">The source UXML to assign.</param>
        /// <param name="panel">The PanelSettings to assign.</param>
        /// <param name="sortingOrder">The panel sort order.</param>
        /// <param name="document">The created UIDocument on success.</param>
        /// <param name="error">A human-readable error on failure, else <c>null</c>.</param>
        /// <returns>The created GameObject, or <c>null</c> on failure.</returns>
        public static GameObject CreateUiDocument(
            string name, GameObject parent, VisualTreeAsset uxml, PanelSettings panel, float sortingOrder,
            out UIDocument document, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name)) name = "UIDocument";

            var go = GameObjectEditingService.Create(name, parent, null);
            document = GameObjectEditingService.AddComponent(go, typeof(UIDocument), out error) as UIDocument;
            if (document == null)
            {
                error ??= "could not add a UIDocument component.";
                return null;
            }

            Undo.RecordObject(document, "Configure UIDocument");
            document.visualTreeAsset = uxml;
            document.panelSettings = panel;
            document.sortingOrder = sortingOrder;
            EditorUtility.SetDirty(document);
            return go;
        }

        /// <summary>
        /// Resolves the theme to assign: an explicit override path, else the shipped Molca theme, else the first
        /// <see cref="ThemeStyleSheet"/> found in the project.
        /// </summary>
        /// <param name="requestedPath">An explicit <c>.tss</c> path, or null/empty for default resolution.</param>
        /// <param name="source">Set to where the theme came from ("override"/"molca"/"project"/"none").</param>
        /// <param name="warning">Set to a note when the result is degraded, else <c>null</c>.</param>
        /// <returns>The resolved theme, or <c>null</c> when none could be found.</returns>
        public static ThemeStyleSheet ResolveTheme(string requestedPath, out string source, out string warning)
        {
            warning = null;

            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                source = "override";
                var requested = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(requestedPath);
                if (requested == null)
                    warning = $"themeStyleSheet '{requestedPath}' did not resolve to a ThemeStyleSheet; panel left themeless.";
                return requested;
            }

            var molca = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(MolcaDefaultThemePath);
            if (molca != null) { source = "molca"; return molca; }

            // The shipped theme should always exist; fall back to any project theme rather than rendering blank.
            var guids = AssetDatabase.FindAssets("t:ThemeStyleSheet");
            if (guids.Length > 0)
            {
                source = "project";
                warning = "Molca default theme was not found; assigned the first project ThemeStyleSheet instead.";
                return AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            source = "none";
            warning = "No ThemeStyleSheet found; assign one in the PanelSettings inspector or the panel will not render.";
            return null;
        }

        /// <summary>Ensures a project-relative folder under <c>Assets/</c> exists, creating each missing segment.</summary>
        /// <param name="projectRelativeFolder">A path under <c>Assets/</c>.</param>
        /// <param name="error">A human-readable error when the path is not under <c>Assets/</c>, else <c>null</c>.</param>
        /// <returns><c>true</c> when the folder exists (or was created).</returns>
        public static bool EnsureAssetFolder(string projectRelativeFolder, out string error)
        {
            error = null;
            var normalized = projectRelativeFolder.Replace('\\', '/').TrimEnd('/');
            var segments = normalized.Split('/');
            if (segments.Length == 0 || segments[0] != "Assets")
            {
                error = $"folder '{projectRelativeFolder}' must be under 'Assets/'.";
                return false;
            }

            string current = "Assets";
            for (int i = 1; i < segments.Length; i++)
            {
                string next = $"{current}/{segments[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
            return true;
        }
    }
}
