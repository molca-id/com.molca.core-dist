#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Molca.ColorID;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Property drawer for <see cref="ColorTokenReference"/>: a searchable token picker with a live
    /// swatch preview.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Drawers/</c>.
    /// <b>Registration:</b> <c>[CustomPropertyDrawer]</c>, discovered by Unity.
    /// <para/>
    /// <b>Drawing this never writes.</b> The only serialized write happens inside the
    /// <see cref="EditorGUI.EndChangeCheck"/> block, in response to a user picking an entry. An
    /// unresolvable value is shown as a marked <c>(unresolved)</c> entry and preserved verbatim until the
    /// author explicitly repairs it. The V1 <c>ColorIDReference</c> drawer repointed an unresolved
    /// pair at the first available colour just by rendering, so opening an inspector silently destroyed
    /// the authored value and hid that it was ever broken.
    /// <para/>
    /// Semantic tokens are offered first and grouped by usage; primitives are pushed into a
    /// <c>palette/</c> submenu because they are ingredients for semantic tokens rather than something
    /// application components should normally bind to.
    /// </remarks>
    [CustomPropertyDrawer(typeof(ColorTokenReference))]
    public class ColorTokenReferenceDrawer : PropertyDrawer
    {
        private const float PreviewWidth = 30f;
        private const float Spacing = 2f;

        // Rebuilt when the theme set changes identity or is edited. Cached because a drawer repaints
        // many times per second and rebuilding the menu each time would make large inspectors crawl.
        private static ColorThemeSet _cachedSet;
        private static int _cachedTokenCount = -1;
        private static string[] _menuPaths;
        private static string[] _tokenIds;

        /// <inheritdoc/>
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUIUtility.singleLineHeight;

        /// <inheritdoc/>
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var tokenIdProperty = property.FindPropertyRelative("_tokenId");
            if (tokenIdProperty == null)
            {
                EditorGUI.LabelField(position, label.text, "ColorTokenReference layout changed");
                EditorGUI.EndProperty();
                return;
            }

            var themeSet = ResolveThemeSet();
            RefreshMenu(themeSet);

            float lineHeight = EditorGUIUtility.singleLineHeight;
            float labelWidth = EditorGUIUtility.labelWidth;
            float remaining = position.width - labelWidth;
            float dropdownWidth = Mathf.Max(40f, remaining - PreviewWidth - Spacing);

            var labelRect = new Rect(position.x, position.y, labelWidth, lineHeight);
            var dropdownRect = new Rect(position.x + labelWidth, position.y, dropdownWidth, lineHeight);
            var previewRect = new Rect(dropdownRect.xMax + Spacing, position.y, PreviewWidth, lineHeight);

            EditorGUI.LabelField(labelRect, label);

            string current = tokenIdProperty.stringValue;
            DrawDropdown(dropdownRect, tokenIdProperty, current, themeSet);
            DrawPreview(previewRect, current, themeSet);

            EditorGUI.EndProperty();
        }

        private void DrawDropdown(Rect rect, SerializedProperty tokenIdProperty, string current,
            ColorThemeSet themeSet)
        {
            if (themeSet == null)
            {
                // No theme set installed: show the raw value rather than an empty picker, and leave it
                // editable as text so a legacy project can still author one.
                EditorGUI.BeginChangeCheck();
                string typed = EditorGUI.TextField(rect, current);
                if (EditorGUI.EndChangeCheck()) tokenIdProperty.stringValue = typed;
                return;
            }

            string buttonLabel = BuildButtonLabel(current, themeSet);

            if (!GUI.Button(rect, buttonLabel, EditorStyles.popup)) return;

            var menu = new GenericMenu();

            // An explicit way to clear, so unassigning does not require deleting text.
            menu.AddItem(new GUIContent("(none)"), string.IsNullOrEmpty(current), () =>
            {
                tokenIdProperty.stringValue = string.Empty;
                tokenIdProperty.serializedObject.ApplyModifiedProperties();
            });

            if (!string.IsNullOrEmpty(current) && Array.IndexOf(_tokenIds, current) < 0)
            {
                menu.AddSeparator(string.Empty);
                // Shown as a disabled entry: visible, selected, and impossible to accidentally re-pick.
                // Never written over — repair is an explicit choice of a different entry.
                menu.AddDisabledItem(new GUIContent($"(unresolved) {current}"), true);
            }

            menu.AddSeparator(string.Empty);

            for (int i = 0; i < _menuPaths.Length; i++)
            {
                string tokenId = _tokenIds[i];
                menu.AddItem(new GUIContent(_menuPaths[i]), tokenId == current, () =>
                {
                    tokenIdProperty.stringValue = tokenId;
                    tokenIdProperty.serializedObject.ApplyModifiedProperties();
                });
            }

            menu.DropDown(rect);
        }

        private static string BuildButtonLabel(string current, ColorThemeSet themeSet)
        {
            if (string.IsNullOrEmpty(current)) return "(none)";
            if (themeSet.GetDefinition(current) != null) return current;
            return $"{current}  (unresolved)";
        }

        private void DrawPreview(Rect rect, string tokenId, ColorThemeSet themeSet)
        {
            Color color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            bool resolved = false;

            if (themeSet != null && !string.IsNullOrEmpty(tokenId))
            {
                // Preview against the authored default variant: a drawer has no active runtime theme,
                // and previewing an arbitrary variant would be misleading.
                var settings = ColorThemeAuditService.FindThemeSettings();
                string variantId = settings?.DefaultVariantId ?? FirstVariantId(themeSet);

                if (!string.IsNullOrEmpty(variantId)
                    && ColorThemeResolver.TryResolve(themeSet, variantId, 0, out var theme, out _)
                    == ColorThemeActivation.Activated
                    && theme.TryGetColor(tokenId, out Color resolvedColor))
                {
                    color = resolvedColor;
                    resolved = true;
                }
            }

            const float border = 1f;
            EditorGUI.DrawRect(rect, resolved ? Color.black : new Color(0.6f, 0.2f, 0.2f));
            var inner = new Rect(rect.x + border, rect.y + border,
                rect.width - border * 2f, rect.height - border * 2f);

            const float alphaBarHeight = 3f;
            var colorRect = new Rect(inner.x, inner.y, inner.width, inner.height - alphaBarHeight);
            var alphaRect = new Rect(inner.x, inner.yMax - alphaBarHeight, inner.width, alphaBarHeight);

            EditorGUI.DrawRect(colorRect, new Color(color.r, color.g, color.b, 1f));

            // Alpha shown as a separate bar rather than by blending, so a fully transparent token is
            // visibly transparent instead of looking like the inspector background.
            EditorGUI.DrawRect(alphaRect,
                EditorGUIUtility.isProSkin ? new Color(0.2f, 0.2f, 0.2f) : new Color(0.7f, 0.7f, 0.7f));
            if (color.a > 0.001f)
            {
                EditorGUI.DrawRect(
                    new Rect(alphaRect.x, alphaRect.y, alphaRect.width * color.a, alphaRect.height),
                    Color.white);
            }
        }

        private static string FirstVariantId(ColorThemeSet themeSet)
        {
            var ids = themeSet.GetVariantIds();
            return ids.Length > 0 ? ids[0] : null;
        }

        private static ColorThemeSet ResolveThemeSet() =>
            ColorThemeAuditService.FindThemeSettings()?.ThemeSet;

        /// <summary>Rebuilds the cached menu when the theme set changes.</summary>
        /// <remarks>
        /// Keyed on set identity plus token count. That misses a pure rename with the same count, which
        /// is acceptable: a rename goes through a transaction that reimports the asset and resets the
        /// drawer anyway.
        /// </remarks>
        private static void RefreshMenu(ColorThemeSet themeSet)
        {
            if (themeSet == null)
            {
                _cachedSet = null;
                _cachedTokenCount = -1;
                _menuPaths = Array.Empty<string>();
                _tokenIds = Array.Empty<string>();
                return;
            }

            if (ReferenceEquals(_cachedSet, themeSet)
                && _cachedTokenCount == themeSet.TokenDefinitions.Count)
                return;

            var semantic = new List<(string path, string id)>();
            var primitive = new List<(string path, string id)>();

            foreach (var definition in themeSet.TokenDefinitions)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Id)) continue;

                string suffix = definition.Deprecated ? "  (deprecated)" : string.Empty;

                if (definition.Kind == ColorTokenKind.Primitive)
                {
                    // Primitives behind one submenu so they do not crowd out the semantic tokens an
                    // application component should be choosing between.
                    primitive.Add(($"Primitives/{definition.Id}{suffix}", definition.Id));
                    continue;
                }

                string group = definition.Usage == ColorTokenUsage.None
                    ? "Unclassified"
                    : definition.Usage.ToString();
                semantic.Add(($"{group}/{definition.Id}{suffix}", definition.Id));
            }

            semantic.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
            primitive.Sort((a, b) => string.CompareOrdinal(a.path, b.path));

            var paths = new List<string>(semantic.Count + primitive.Count);
            var ids = new List<string>(paths.Capacity);
            foreach (var (path, id) in semantic) { paths.Add(path); ids.Add(id); }
            foreach (var (path, id) in primitive) { paths.Add(path); ids.Add(id); }

            _menuPaths = paths.ToArray();
            _tokenIds = ids.ToArray();
            _cachedSet = themeSet;
            _cachedTokenCount = themeSet.TokenDefinitions.Count;
        }
    }
}
#endif
