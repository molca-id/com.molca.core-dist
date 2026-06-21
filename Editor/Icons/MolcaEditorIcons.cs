using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Icons
{
    /// <summary>
    /// Shared loader for the Molca family icons that ship inside the package, used to give editor
    /// windows a title-tab icon.
    /// </summary>
    /// <remarks>
    /// Editor windows cannot use the <see cref="UnityEngine.IconAttribute"/> (that only drives
    /// asset/ScriptableObject icons), so they build their <see cref="GUIContent"/> title through
    /// <see cref="WindowTitle"/> instead. Textures are loaded by package-relative path and cached
    /// for the lifetime of the domain. Pass a family name (e.g. <c>"mcp"</c>, <c>"settings"</c>,
    /// <c>"sequence"</c>) so a window shares the icon of the assets it edits; omit it to fall back to
    /// the generic <c>"window"</c> icon.
    /// </remarks>
    internal static class MolcaEditorIcons
    {
        private const string IconDir = "Packages/com.molca.core/Editor/Icons/";

        // Cache by family name so each texture is loaded at most once per domain.
        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        /// <summary>The generic Molca editor-window icon.</summary>
        internal static Texture2D Window => Family("window");

        /// <summary>The Molca product logo icon.</summary>
        internal static Texture2D Logo => Family("logo");

        /// <summary>Loads (and caches) the icon for the given family, e.g. <c>"mcp"</c>.</summary>
        /// <param name="family">Family name without the <c>molca-</c> prefix or <c>.png</c> suffix.</param>
        /// <returns>The texture, or <c>null</c> if no matching icon ships in the package.</returns>
        internal static Texture2D Family(string family)
        {
            if (_cache.TryGetValue(family, out var tex) && tex != null) return tex;
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{IconDir}molca-{family}.png");
            _cache[family] = tex;
            return tex;
        }

        /// <summary>
        /// Builds a window <see cref="GUIContent"/> with the given <paramref name="title"/> and the
        /// icon of the named <paramref name="family"/> (defaults to the generic window icon).
        /// </summary>
        /// <param name="title">Text shown on the window tab.</param>
        /// <param name="family">Family icon to use, e.g. <c>"mcp"</c>; defaults to <c>"window"</c>.</param>
        /// <returns>A <see cref="GUIContent"/> carrying both the title and icon.</returns>
        internal static GUIContent WindowTitle(string title, string family = "window") =>
            new GUIContent(title, Family(family));
    }
}
