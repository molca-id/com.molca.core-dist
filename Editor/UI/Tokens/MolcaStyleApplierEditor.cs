using System;
using System.Collections.Generic;
using Molca.ColorID.Editor;
using Molca.UI.Tokens;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.UI.Tokens
{
    /// <summary>
    /// Inspector for <see cref="MolcaStyleApplier"/>: a searchable token picker plus an <b>Apply Token</b>
    /// button that bakes the concrete components onto the object in one undo group.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/UI/Tokens/</c>.
    /// <b>Registration:</b> <c>[CustomEditor]</c>, discovered by Unity.
    /// <para/>
    /// <b>An invalid token is never repaired by drawing.</b> The picker was a free-text field, which made
    /// a typo indistinguishable from an unmigrated value and offered no way to discover what a catalog
    /// contains. It is now a dropdown grouped by category — but an id the catalog does not contain is shown
    /// as a disabled <c>(unresolved)</c> entry and kept verbatim. Selecting the nearest match, or the first
    /// entry, would destroy the authored value and hide that it was ever broken; the same rule the
    /// <c>ColorTokenReference</c> drawer follows.
    /// <para/>
    /// Colour entries additionally show whether they are canonical or still on the legacy V1 pair, because
    /// that decides which component <see cref="MolcaUiTokenResolver"/> writes — a
    /// <c>ColorThemeBinding</c> or a <c>ColorID</c> — and an author migrating a catalog needs to see which
    /// they are about to get.
    /// </remarks>
    [CustomEditor(typeof(MolcaStyleApplier))]
    public class MolcaStyleApplierEditor : UnityEditor.Editor
    {
        private static MolcaUiTokenRegistry _cachedCatalog;
        private static int _cachedTokenCount = -1;
        private static string[] _menuPaths;
        private static string[] _tokenIds;

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var catalogProperty = serializedObject.FindProperty("_catalog");
            var tokenProperty = serializedObject.FindProperty("_token");

            EditorGUILayout.PropertyField(catalogProperty);

            var catalog = catalogProperty.objectReferenceValue as MolcaUiTokenRegistry;
            DrawTokenPicker(tokenProperty, catalog);

            serializedObject.ApplyModifiedProperties();

            var applier = (MolcaStyleApplier)target;
            DrawStatus(applier, catalog);
            DrawApplyButton(applier, catalog);
        }

        private void DrawTokenPicker(SerializedProperty tokenProperty, MolcaUiTokenRegistry catalog)
        {
            var label = new GUIContent("Token", "The design token this object is styled by.");

            if (catalog == null)
            {
                // No catalog to pick from. A text field keeps an existing value editable and visible
                // instead of showing an empty dropdown that implies there is nothing to choose.
                EditorGUILayout.PropertyField(tokenProperty, label);
                return;
            }

            RefreshMenu(catalog);

            string current = tokenProperty.stringValue;
            bool known = !string.IsNullOrEmpty(current) && Array.IndexOf(_tokenIds, current) >= 0;
            string buttonLabel = string.IsNullOrEmpty(current)
                ? "(none)"
                : known ? current : $"{current}  (unresolved)";

            var rect = EditorGUILayout.GetControlRect();
            var dropdownRect = EditorGUI.PrefixLabel(rect, label);

            if (!GUI.Button(dropdownRect, buttonLabel, EditorStyles.popup)) return;

            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("(none)"), string.IsNullOrEmpty(current), () =>
            {
                tokenProperty.stringValue = string.Empty;
                tokenProperty.serializedObject.ApplyModifiedProperties();
            });

            if (!string.IsNullOrEmpty(current) && !known)
            {
                menu.AddSeparator(string.Empty);
                // Disabled: visible, shown as selected, and impossible to re-pick by accident. Repair is
                // an explicit choice of a different entry.
                menu.AddDisabledItem(new GUIContent($"(unresolved) {current}"), true);
            }

            menu.AddSeparator(string.Empty);

            for (int i = 0; i < _menuPaths.Length; i++)
            {
                string tokenId = _tokenIds[i];
                menu.AddItem(new GUIContent(_menuPaths[i]), tokenId == current, () =>
                {
                    tokenProperty.stringValue = tokenId;
                    tokenProperty.serializedObject.ApplyModifiedProperties();
                });
            }

            menu.DropDown(dropdownRect);
        }

        private static void DrawStatus(MolcaStyleApplier applier, MolcaUiTokenRegistry catalog)
        {
            if (catalog == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign the project's UI Token Catalog to pick a token.", MessageType.Info);
                return;
            }

            if (string.IsNullOrEmpty(applier.Token)) return;

            if (!catalog.TryResolve(applier.Token, out var token))
            {
                EditorGUILayout.HelpBox(
                    $"Token '{applier.Token}' is not in catalog '{catalog.name}'. The value is kept as "
                    + "authored — pick a token above to repair it.", MessageType.Warning);
                return;
            }

            if (token.Category != MolcaUiTokenCategory.Color) return;

            if (token.HasCanonicalColorToken)
            {
                string targets = ColorThemeBindingAuthoring.DescribeColorTargets(applier.gameObject);
                EditorGUILayout.HelpBox(
                    $"Canonical colour token '{token.ColorToken.TokenId}'. Applying writes a "
                    + $"ColorThemeBinding targeting: {targets ?? "nothing on this object"}.",
                    targets == null ? MessageType.Warning : MessageType.Info);
                return;
            }

            if (token.HasLegacyColorPair)
            {
                EditorGUILayout.HelpBox(
                    $"Legacy colour pair '{token.SwatchName}.{token.ColorId}'. Applying writes a V1 "
                    + "ColorID. Migrate the catalog entry to a canonical colour token to get variant "
                    + "coverage and contrast validation.", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                $"Colour token '{token.Id}' carries neither a canonical token nor a legacy pair, so it "
                + "cannot be applied. Fix the catalog entry.", MessageType.Error);
        }

        private static void DrawApplyButton(MolcaStyleApplier applier, MolcaUiTokenRegistry catalog)
        {
            bool resolvable = catalog != null
                              && !string.IsNullOrEmpty(applier.Token)
                              && catalog.TryResolve(applier.Token, out _);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!resolvable))
            {
                if (!GUILayout.Button("Apply Token")) return;

                Undo.SetCurrentGroupName("Apply UI Token");
                int group = Undo.GetCurrentGroup();

                if (MolcaUiTokenResolver.TryApply(catalog, applier.Token, applier.gameObject, out var error))
                    Debug.Log($"[Molca UI] Applied '{applier.Token}' to '{applier.name}'.", applier);
                else
                    Debug.LogWarning($"[Molca UI] Could not apply '{applier.Token}': {error}", applier);

                Undo.CollapseUndoOperations(group);
            }
        }

        /// <summary>Rebuilds the cached menu when the catalog changes identity or token count.</summary>
        /// <remarks>
        /// Cached because an inspector repaints many times per second. Keyed on identity plus count, which
        /// misses a pure rename at the same count — acceptable, since selecting the catalog again or a
        /// domain reload rebuilds it, and a stale entry shows as <c>(unresolved)</c> rather than resolving
        /// to the wrong thing.
        /// </remarks>
        private static void RefreshMenu(MolcaUiTokenRegistry catalog)
        {
            var tokens = catalog.AllTokens;
            int count = tokens?.Count ?? 0;

            if (ReferenceEquals(_cachedCatalog, catalog) && _cachedTokenCount == count) return;

            var entries = new List<(string path, string id)>(count);
            for (int i = 0; i < count; i++)
            {
                var token = tokens[i];
                if (token == null || string.IsNullOrEmpty(token.Id)) continue;

                // Grouped by category so a picker over a large catalog stays navigable, and annotated so
                // an unmigrated colour entry is visible before it is chosen rather than after it is applied.
                string suffix = token.Category == MolcaUiTokenCategory.Color && token.HasLegacyColorPair
                    ? "  (legacy)"
                    : string.Empty;

                entries.Add(($"{token.Category}/{token.Id}{suffix}", token.Id));
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.path, b.path));

            _menuPaths = new string[entries.Count];
            _tokenIds = new string[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                _menuPaths[i] = entries[i].path;
                _tokenIds[i] = entries[i].id;
            }

            _cachedCatalog = catalog;
            _cachedTokenCount = count;
        }
    }
}
