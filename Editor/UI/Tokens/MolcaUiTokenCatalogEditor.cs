using System.Collections.Generic;
using Molca.ColorID;
using Molca.ColorID.Editor;
using Molca.UI.Tokens;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.UI.Tokens
{
    /// <summary>
    /// Inspector for <see cref="MolcaUiTokenCatalog"/>: shows only the fields the selected category uses,
    /// validates ids and references, and reports colour entries against the project's Color Theme Set.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/UI/Tokens/</c>.
    /// <b>Registration:</b> <c>[CustomEditor]</c>, discovered by Unity.
    /// <para/>
    /// <b>Why a custom inspector at all.</b> <see cref="MolcaUiToken"/> is deliberately a flat record — a
    /// category discriminator plus every category's fields — because that serializes cleanly without
    /// <c>[SerializeReference]</c>. The cost is paid in the default inspector, which shows a spacing token
    /// a sprite field and a prefab slot, so an author cannot tell which fields are load-bearing. Hiding the
    /// irrelevant ones is the whole reason this exists.
    /// <para/>
    /// <b>Validation is reported, never repaired.</b> A duplicate id, a missing sprite, a colour token the
    /// theme set does not declare — all shown, none silently rewritten. Drawing an inspector must not change
    /// data; that rule is what the V1 <c>ColorIDReference</c> drawer broke, and repairing a duplicate id by
    /// guessing which entry was meant would be the same mistake.
    /// </remarks>
    [CustomEditor(typeof(MolcaUiTokenCatalog))]
    public class MolcaUiTokenCatalogEditor : UnityEditor.Editor
    {
        private const string SearchControlName = "MolcaTokenCatalogSearch";

        private string _search = string.Empty;
        private MolcaUiTokenCategory? _categoryFilter;
        private bool _showValidation = true;
        private readonly Dictionary<int, bool> _expanded = new Dictionary<int, bool>();

        // Recomputed on repaint from serialized state; cheap enough for catalog-sized lists and always
        // consistent with what is on screen.
        private readonly List<string> _duplicateIds = new List<string>();
        private readonly List<string> _problems = new List<string>();

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var tokens = serializedObject.FindProperty("_tokens");
            var raycaster = serializedObject.FindProperty("_vrRaycasterTypeName");

            var themeSet = ColorThemeAuditService.FindThemeSettings()?.ThemeSet;
            var resolvedVariants = ResolveVariants(themeSet);

            Validate(tokens, themeSet, resolvedVariants);
            DrawSummary(tokens, themeSet);
            DrawValidation();
            DrawToolbar(tokens);

            EditorGUILayout.Space();
            DrawTokens(tokens, themeSet, resolvedVariants);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(raycaster);

            serializedObject.ApplyModifiedProperties();
        }

        // ── summary and validation ─────────────────────────────────────────────────────────────

        private void DrawSummary(SerializedProperty tokens, ColorThemeSet themeSet)
        {
            int canonical = 0, legacy = 0, colours = 0;

            for (int i = 0; i < tokens.arraySize; i++)
            {
                var element = tokens.GetArrayElementAtIndex(i);
                if ((MolcaUiTokenCategory)element.FindPropertyRelative("_category").enumValueIndex
                    != MolcaUiTokenCategory.Color) continue;

                colours++;
                if (!string.IsNullOrEmpty(CanonicalOf(element))) canonical++;
                else if (HasLegacyPair(element)) legacy++;
            }

            EditorGUILayout.LabelField($"{tokens.arraySize} token(s)", EditorStyles.boldLabel);

            if (colours == 0) return;

            if (themeSet == null)
            {
                EditorGUILayout.HelpBox(
                    "No Color Theme Set is installed, so colour entries cannot be validated and canonical "
                    + "tokens cannot resolve. Install V2 to check them.", MessageType.Info);
                return;
            }

            string message = $"{colours} colour entry/entries: {canonical} canonical, {legacy} on the "
                             + "legacy V1 pair.";

            if (legacy == 0)
            {
                EditorGUILayout.HelpBox(message + " Fully migrated.", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                message + " A legacy entry still resolves through the theme set's alias map; migrating it "
                + "gives the object variant coverage and contrast validation.", MessageType.Warning);

            if (GUILayout.Button("Preview colour migration"))
            {
                var catalog = (MolcaUiTokenCatalog)target;
                Debug.Log(MolcaUiTokenCatalogMigration.Plan(catalog).ToPreview(), catalog);
            }
        }

        /// <summary>Collects every problem worth an author's attention. Changes nothing.</summary>
        private void Validate(SerializedProperty tokens, ColorThemeSet themeSet,
            Dictionary<string, ResolvedColorTheme> resolvedVariants)
        {
            _duplicateIds.Clear();
            _problems.Clear();

            var seen = new HashSet<string>(System.StringComparer.Ordinal);

            for (int i = 0; i < tokens.arraySize; i++)
            {
                var element = tokens.GetArrayElementAtIndex(i);
                string id = element.FindPropertyRelative("_id").stringValue;
                var category = (MolcaUiTokenCategory)element
                    .FindPropertyRelative("_category").enumValueIndex;

                if (string.IsNullOrEmpty(id))
                {
                    _problems.Add($"Entry {i} has no id.");
                }
                else
                {
                    // First wins in MolcaUiTokenRegistry.TryResolve, so a duplicate id means later entries
                    // are unreachable — silently, which is why this is called out rather than tolerated.
                    if (!seen.Add(id)) _duplicateIds.Add(id);

                    if (!MolcaUiTokenId.IsValid(id))
                        _problems.Add($"'{id}' is not a valid token id (expected 'category/name').");
                    else if (MolcaUiTokenId.TryParse(id, out var parsed, out _) && parsed != category)
                        _problems.Add($"'{id}' is in the {category} category but its id says {parsed}.");
                }

                ValidateCategoryFields(element, id, category, themeSet, resolvedVariants);
            }
        }

        private void ValidateCategoryFields(SerializedProperty element, string id,
            MolcaUiTokenCategory category, ColorThemeSet themeSet,
            Dictionary<string, ResolvedColorTheme> resolvedVariants)
        {
            switch (category)
            {
                case MolcaUiTokenCategory.Color:
                    ValidateColor(element, id, themeSet, resolvedVariants);
                    break;

                case MolcaUiTokenCategory.Text:
                    if (element.FindPropertyRelative("_styleInfo").objectReferenceValue == null)
                        _problems.Add($"'{id}' is a Text token with no style preset.");
                    break;

                case MolcaUiTokenCategory.Surface:
                    if (element.FindPropertyRelative("_sprite").objectReferenceValue == null)
                        _problems.Add($"'{id}' is a Surface token with no sprite.");
                    if (element.FindPropertyRelative("_referencePixels").floatValue <= 0f)
                        _problems.Add($"'{id}' has a non-positive reference-pixels value, so the PPU rule "
                                      + "cannot be applied.");
                    break;

                case MolcaUiTokenCategory.Control:
                    if (element.FindPropertyRelative("_prefab").objectReferenceValue == null)
                        _problems.Add($"'{id}' is a Control token with no prefab.");
                    break;
            }
        }

        private void ValidateColor(SerializedProperty element, string id, ColorThemeSet themeSet,
            Dictionary<string, ResolvedColorTheme> resolvedVariants)
        {
            string canonical = CanonicalOf(element);

            if (string.IsNullOrEmpty(canonical))
            {
                if (!HasLegacyPair(element))
                    _problems.Add($"'{id}' is a Color token with neither a canonical token nor a legacy "
                                  + "swatch and colour ID, so it cannot be applied.");
                else if (themeSet != null && themeSet.ResolveLegacyToken(LegacyKeyOf(element)) == null)
                    _problems.Add($"'{id}' uses legacy '{LegacyKeyOf(element)}', which no alias maps — it "
                                  + "resolves only by guess or not at all.");
                return;
            }

            if (themeSet == null) return;

            if (themeSet.GetDefinition(canonical) == null)
            {
                _problems.Add($"'{id}' names canonical token '{canonical}', which the theme set does not "
                              + "declare.");
                return;
            }

            // Per-variant coverage, not just "the token exists". An optional token present in one variant
            // and absent from another renders nothing in the second, which is exactly the V1 failure this
            // model exists to make visible.
            var missing = new List<string>();
            foreach (var pair in resolvedVariants)
            {
                if (!pair.Value.Contains(canonical)) missing.Add(pair.Key);
            }

            if (missing.Count > 0)
                _problems.Add($"'{id}' names '{canonical}', which does not resolve in: "
                              + $"{string.Join(", ", missing)}.");
        }

        private void DrawValidation()
        {
            int count = _duplicateIds.Count + _problems.Count;
            if (count == 0) return;

            _showValidation = EditorGUILayout.Foldout(_showValidation, $"Validation ({count})", true);
            if (!_showValidation) return;

            using (new EditorGUI.IndentLevelScope())
            {
                foreach (string duplicate in _duplicateIds)
                {
                    EditorGUILayout.HelpBox(
                        $"'{duplicate}' is declared more than once. Lookup returns the first, so the "
                        + "later entries are unreachable.", MessageType.Error);
                }

                foreach (string problem in _problems)
                {
                    EditorGUILayout.HelpBox(problem, MessageType.Warning);
                }
            }
        }

        // ── list ───────────────────────────────────────────────────────────────────────────────

        private void DrawToolbar(SerializedProperty tokens)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.SetNextControlName(SearchControlName);
                _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);

                var options = new[] { "All", "Surface", "Color", "Text", "Control", "Spacing" };
                int current = _categoryFilter.HasValue ? (int)_categoryFilter.Value + 1 : 0;
                int picked = EditorGUILayout.Popup(current, options, EditorStyles.toolbarPopup,
                    GUILayout.Width(90f));
                _categoryFilter = picked == 0 ? (MolcaUiTokenCategory?)null
                    : (MolcaUiTokenCategory)(picked - 1);

                if (GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(24f)))
                {
                    tokens.InsertArrayElementAtIndex(tokens.arraySize);
                }
            }
        }

        private void DrawTokens(SerializedProperty tokens, ColorThemeSet themeSet,
            Dictionary<string, ResolvedColorTheme> resolvedVariants)
        {
            int shown = 0;

            for (int i = 0; i < tokens.arraySize; i++)
            {
                var element = tokens.GetArrayElementAtIndex(i);
                string id = element.FindPropertyRelative("_id").stringValue ?? string.Empty;
                var category = (MolcaUiTokenCategory)element
                    .FindPropertyRelative("_category").enumValueIndex;

                if (_categoryFilter.HasValue && category != _categoryFilter.Value) continue;
                if (!string.IsNullOrEmpty(_search)
                    && id.IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                shown++;
                DrawToken(tokens, element, i, id, category);
            }

            if (shown == 0)
            {
                EditorGUILayout.LabelField(tokens.arraySize == 0
                    ? "No tokens. Add one, or seed the catalog with the token miner."
                    : "No tokens match the current filter.", EditorStyles.centeredGreyMiniLabel);
            }
        }

        private void DrawToken(SerializedProperty tokens, SerializedProperty element, int index,
            string id, MolcaUiTokenCategory category)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool expanded = _expanded.TryGetValue(index, out bool stored) && stored;
                    string title = string.IsNullOrEmpty(id) ? "(no id)" : id;
                    _expanded[index] = EditorGUILayout.Foldout(expanded, $"{title}", true);

                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(Badge(element, category), EditorStyles.miniLabel,
                        GUILayout.Width(120f));

                    if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(22f)))
                    {
                        tokens.DeleteArrayElementAtIndex(index);
                        _expanded.Clear();
                        return;
                    }
                }

                if (!_expanded[index]) return;

                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("_id"));
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("_category"));
                    DrawCategoryFields(element, category);
                }
            }
        }

        /// <summary>Draws only the fields the selected category actually uses.</summary>
        private static void DrawCategoryFields(SerializedProperty element, MolcaUiTokenCategory category)
        {
            switch (category)
            {
                case MolcaUiTokenCategory.Color:
                    // The ColorTokenReference drawer supplies the searchable picker and swatch preview.
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("_colorToken"),
                        new GUIContent("Colour Token"));

                    if (!HasLegacyPair(element)) break;

                    EditorGUILayout.LabelField("Legacy (V1)", EditorStyles.miniBoldLabel);
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUILayout.PropertyField(element.FindPropertyRelative("_swatchName"));
                        EditorGUILayout.PropertyField(element.FindPropertyRelative("_colorId"));
                        EditorGUILayout.LabelField(
                            string.IsNullOrEmpty(CanonicalOf(element))
                                ? "Read at apply time."
                                : "Ignored — the canonical token above wins.",
                            EditorStyles.miniLabel);
                    }
                    break;

                case MolcaUiTokenCategory.Text:
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("_styleInfo"));
                    break;

                case MolcaUiTokenCategory.Surface:
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("_sprite"));
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("_imageType"));
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("_referencePixels"));
                    break;

                case MolcaUiTokenCategory.Control:
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("_prefab"));
                    break;

                case MolcaUiTokenCategory.Spacing:
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("_value"));
                    break;
            }
        }

        private static string Badge(SerializedProperty element, MolcaUiTokenCategory category)
        {
            if (category != MolcaUiTokenCategory.Color) return category.ToString();
            if (!string.IsNullOrEmpty(CanonicalOf(element))) return "Color · canonical";
            return HasLegacyPair(element) ? "Color · legacy" : "Color · empty";
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────

        private static string CanonicalOf(SerializedProperty element) =>
            element.FindPropertyRelative("_colorToken._tokenId")?.stringValue;

        private static bool HasLegacyPair(SerializedProperty element) =>
            !string.IsNullOrEmpty(element.FindPropertyRelative("_swatchName")?.stringValue)
            && !string.IsNullOrEmpty(element.FindPropertyRelative("_colorId")?.stringValue);

        private static LegacyColorKey LegacyKeyOf(SerializedProperty element) =>
            new LegacyColorKey(element.FindPropertyRelative("_swatchName").stringValue,
                element.FindPropertyRelative("_colorId").stringValue);

        private static Dictionary<string, ResolvedColorTheme> ResolveVariants(ColorThemeSet themeSet)
        {
            var resolved = new Dictionary<string, ResolvedColorTheme>();
            if (themeSet == null) return resolved;

            foreach (string variantId in themeSet.GetVariantIds())
            {
                if (ColorThemeResolver.TryResolve(themeSet, variantId, 0, out var theme, out _)
                    == ColorThemeActivation.Activated)
                    resolved[variantId] = theme;
            }
            return resolved;
        }
    }
}
