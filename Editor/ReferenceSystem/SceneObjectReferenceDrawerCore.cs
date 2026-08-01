using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// The shared drawing and editing logic behind the <see cref="SceneObjectReference"/> and
    /// <see cref="SceneObjectReference{T}"/> property drawers.
    /// </summary>
    /// <remarks>
    /// <para>A projection of <see cref="ReferenceProviderLookup"/>, so the Inspector shows exactly what
    /// the runtime would resolve. The previous drawers matched on Ref Id alone and took
    /// <c>FirstOrDefault</c>, which meant that with two providers sharing an id the Inspector displayed —
    /// and its Select button jumped to — one object while the runtime resolved another or refused the
    /// ambiguity entirely.</para>
    ///
    /// <para><b>Drawing never mutates serialized data.</b> The old drawers rewrote <c>refType</c> and
    /// <c>cachedDisplayName</c> from inside <c>OnGUI</c>, dirtying scenes and prefabs merely by looking at
    /// an Inspector, and doing it on non-Layout events so the write depended on which GUI event happened to
    /// be processing. Metadata refresh is now an explicit button: a click is user intent, a repaint is
    /// not.</para>
    /// </remarks>
    internal static class SceneObjectReferenceDrawerCore
    {
        private const float ActionButtonWidth = 22f;
        private const float StateIconWidth = 20f;

        /// <summary>
        /// Draws one reference field.
        /// </summary>
        /// <param name="position">The rect assigned to the property.</param>
        /// <param name="property">The reference struct property.</param>
        /// <param name="label">The field label.</param>
        /// <param name="expectedType">
        /// The type a <see cref="SceneObjectReference{T}"/> field promises, or null for the untyped struct.
        /// Constrains the picker so a field cannot be pointed at an object that would fail the cast.
        /// </param>
        public static void Draw(Rect position, SerializedProperty property, GUIContent label, Type expectedType)
        {
            EditorGUI.BeginProperty(position, label, property);

            var refIdProperty = property.FindPropertyRelative("refId");
            var refTypeProperty = property.FindPropertyRelative("refType");
            var sceneGuidProperty = property.FindPropertyRelative("sceneGuid");
            var displayNameProperty = property.FindPropertyRelative("cachedDisplayName");

            var storedRefId = refIdProperty?.stringValue ?? string.Empty;
            var storedRefType = refTypeProperty?.stringValue ?? string.Empty;

            var resolution = string.IsNullOrEmpty(storedRefId)
                ? null
                : ReferenceProviderLookup.Resolve(storedRefId, storedRefType, expectedType);

            var state = Describe(resolution, storedRefId, storedRefType, sceneGuidProperty, displayNameProperty);

            // --- layout -----------------------------------------------------------------
            var prefixRect = EditorGUI.PrefixLabel(position, label);
            var x = prefixRect.x;

            if (state.Icon != null)
            {
                var iconRect = new Rect(x, prefixRect.y, StateIconWidth, prefixRect.height);
                GUI.Label(iconRect, state.Icon);
                x += StateIconWidth;
            }

            var actionCount = 2 + (state.CanRefreshMetadata ? 1 : 0);
            var actionsWidth = actionCount * (ActionButtonWidth + 2f);
            var buttonRect = new Rect(x, prefixRect.y, Mathf.Max(24f, prefixRect.xMax - x - actionsWidth), prefixRect.height);

            if (GUI.Button(buttonRect, new GUIContent(state.Label, state.Tooltip), EditorStyles.popup))
            {
                var screenRect = new Rect(GUIUtility.GUIToScreenPoint(buttonRect.position), buttonRect.size);
                ReferencePickerPopup.Show(
                    screenRect, property.serializedObject.targetObjects, property.propertyPath, expectedType);
            }

            var actionX = buttonRect.xMax + 2f;

            if (state.CanRefreshMetadata)
            {
                var refreshRect = new Rect(actionX, prefixRect.y, ActionButtonWidth, prefixRect.height);
                _refreshIcon ??= Icon("Refresh", "R");
                _refreshIcon.tooltip =
                    "Update stale metadata: rewrite the serialized Ref Type and cached display name from "
                    + "the current target. Modifies this asset.";
                if (GUI.Button(refreshRect, _refreshIcon, EditorStyles.miniButton))
                {
                    RefreshMetadata(
                        property.serializedObject.targetObjects, property.propertyPath, state.Resolved);
                }

                actionX += ActionButtonWidth + 2f;
            }

            // "Open in References" is available whatever the field's state, including a healthy one: the
            // workspace answers "what else points at this target, and what does this one resolve to" — a
            // question a correct reference raises as often as a broken one.
            var openRect = new Rect(actionX, prefixRect.y, ActionButtonWidth, prefixRect.height);
            _openInHubIcon ??= Icon("Search Icon", "d_Search Icon", "REF");
            _openInHubIcon.tooltip =
                "Open this reference in the Hub's References workspace: its full locator, every candidate "
                + "target, and the repairs available for it.";
            if (GUI.Button(openRect, _openInHubIcon, EditorStyles.miniButton))
                OpenInReferences(property);

            actionX += ActionButtonWidth + 2f;

            var selectRect = new Rect(actionX, prefixRect.y, ActionButtonWidth, prefixRect.height);
            using (new EditorGUI.DisabledScope(state.Resolved == null))
            {
                _selectIcon ??= Icon("d_ViewToolMove", "ViewToolMove", "SEL");
                _selectIcon.tooltip = "Select and ping the referenced object";
                if (GUI.Button(selectRect, _selectIcon, EditorStyles.miniButton))
                    SelectAndPing(state.Resolved);
            }

            EditorGUI.EndProperty();
        }

        #region State

        /// <summary>How one reference field should be presented, derived from its resolution outcome.</summary>
        private readonly struct FieldState
        {
            public readonly string Label;
            public readonly string Tooltip;
            public readonly GUIContent Icon;
            public readonly ReferenceProviderRecord Resolved;
            public readonly bool CanRefreshMetadata;

            public FieldState(
                string label, string tooltip, GUIContent icon,
                ReferenceProviderRecord resolved, bool canRefreshMetadata)
            {
                Label = label;
                Tooltip = tooltip;
                Icon = icon;
                Resolved = resolved;
                CanRefreshMetadata = canRefreshMetadata;
            }
        }

        private static FieldState Describe(
            ReferenceSiteResolution resolution,
            string storedRefId,
            string storedRefType,
            SerializedProperty sceneGuidProperty,
            SerializedProperty displayNameProperty)
        {
            if (resolution == null)
                return new FieldState("None", "No reference assigned.", null, null, false);

            var cachedName = displayNameProperty?.stringValue ?? string.Empty;
            var candidates = resolution.Candidates;

            switch (resolution.Outcome)
            {
                case ReferenceResolveOutcome.ResolvedExact:
                {
                    var provider = candidates[0];

                    // The cached display name is presentation metadata, so a stale one is cosmetic — but
                    // it is what a "missing" label falls back to, and offering the refresh here is what
                    // replaces the old silent write during repaint.
                    var stale = !string.Equals(cachedName, provider.DisplayName, StringComparison.Ordinal);
                    return new FieldState(
                        $"{provider.DisplayName} ({provider.RefType})",
                        stale
                            ? $"Resolves exactly to '{provider.DisplayName}'. The cached display name "
                              + $"(\"{cachedName}\") is out of date; use the refresh button to update it."
                            : $"Resolves exactly to '{provider.DisplayName}' ({provider.RuntimeTypeName}).",
                        stale ? WarningIcon() : null,
                        provider,
                        stale);
                }

                case ReferenceResolveOutcome.ResolvedViaLegacyFallback:
                {
                    var provider = candidates[0];
                    return new FieldState(
                        $"{provider.DisplayName} ({provider.RefType})",
                        $"REF005: the serialized Ref Type \"{storedRefType}\" no longer matches any provider. "
                        + $"This resolves through the compatibility fallback to '{provider.DisplayName}' "
                        + $"(type \"{provider.RefType}\"), which fails as soon as a second object carries "
                        + "the same Ref Id. Use the refresh button to update the serialized metadata.",
                        WarningIcon(),
                        provider,
                        canRefreshMetadata: true);
                }

                case ReferenceResolveOutcome.DuplicateProvider:
                    return new FieldState(
                        $"{candidates.Count} objects claim \"{storedRefId}\"",
                        $"REF002: {candidates.Count} objects claim Ref Id \"{storedRefId}\" under Ref Type "
                        + $"\"{storedRefType}\": {string.Join(", ", candidates.Select(c => c.DisplayName))}. "
                        + "Which one resolves depends on load order. Give each its own Ref Id, then re-pick "
                        + "the intended target here.",
                        ErrorIcon(),
                        null,
                        false);

                case ReferenceResolveOutcome.AmbiguousFallback:
                    return new FieldState(
                        $"Ambiguous \"{storedRefId}\"",
                        $"REF003: Ref Type \"{storedRefType}\" matches no provider, and {candidates.Count} "
                        + "objects carry this Ref Id under other types: "
                        + $"{string.Join(", ", candidates.Select(c => $"{c.DisplayName} ({c.RefType})"))}. "
                        + "The runtime refuses an ambiguous fallback, so this resolves to nothing. Re-pick "
                        + "the intended target.",
                        ErrorIcon(),
                        null,
                        false);

                case ReferenceResolveOutcome.WrongRuntimeType:
                {
                    var provider = candidates[0];
                    return new FieldState(
                        $"Wrong type: {provider.DisplayName}",
                        $"REF004: \"{storedRefId}\" resolves to '{provider.DisplayName}' of type "
                        + $"{provider.RuntimeTypeName}, which this field cannot accept. The cast fails at "
                        + "runtime.",
                        ErrorIcon(),
                        null,
                        false);
                }

                default:
                {
                    // Missing. Name the scene the target was picked from, if it is still known, so the
                    // reader can tell "wrong id" from "that scene is simply not open".
                    var sceneName = DescribeStoredScene(sceneGuidProperty);
                    var label = string.IsNullOrEmpty(cachedName)
                        ? $"Missing \"{storedRefId}\""
                        : $"[{sceneName}] {cachedName} ({storedRefType})";

                    var inert = candidates.Count > 0;
                    return new FieldState(
                        label,
                        inert
                            ? "REF001: the only object carrying this Ref Id is not a runtime-resolvable "
                              + $"target ({string.Join(", ", candidates.Select(c => c.Locator.AssetPath))}). "
                              + "It resolves only if that object is instantiated into a loaded scene first."
                            : $"REF001: no loaded object carries Ref Id \"{storedRefId}\". Open the scene "
                              + $"that provides it ({sceneName}), or re-pick the target.",
                        inert ? WarningIcon() : ErrorIcon(),
                        null,
                        false);
                }
            }
        }

        private static string DescribeStoredScene(SerializedProperty sceneGuidProperty)
        {
            var guid = sceneGuidProperty?.stringValue;
            if (string.IsNullOrEmpty(guid))
                return "unknown scene";

            var path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path)
                ? "deleted scene"
                : System.IO.Path.GetFileNameWithoutExtension(path);
        }

        #endregion

        #region Navigation

        /// <summary>
        /// Opens the Hub's References workspace focused on the reference this field represents.
        /// </summary>
        /// <param name="property">The reference struct property being drawn.</param>
        /// <remarks>
        /// The site key is derived exactly the way <see cref="ReferenceSiteRecord.SiteKey"/> is built — owner
        /// locator key plus serialized property path — so the workspace can find this field in a snapshot it
        /// produced independently. Deriving it rather than searching for it is what keeps the drawer from
        /// needing an audit of its own just to navigate.
        /// </remarks>
        private static void OpenInReferences(SerializedProperty property)
        {
            var owner = property?.serializedObject?.targetObject;
            var siteKey = owner == null
                ? null
                : $"{ReferenceObjectLocator.For(owner).Key}|{property.propertyPath}";

            Hub.ReferenceHubWorkspace.Open(siteKey);
        }

        #endregion

        #region Mutations (user-initiated only)

        /// <summary>
        /// Writes <paramref name="selected"/> into the reference field, or clears it when null.
        /// </summary>
        /// <param name="targets">The objects being edited.</param>
        /// <param name="propertyPath">Serialized path of the reference field.</param>
        /// <param name="selected">The chosen provider, or null to clear.</param>
        internal static void ApplySelection(
            UnityEngine.Object[] targets, string propertyPath, ReferenceProviderRecord selected)
        {
            Write(targets, propertyPath, (refId, refType, sceneGuid, displayName) =>
            {
                if (selected == null)
                {
                    refId.stringValue = string.Empty;
                    refType.stringValue = string.Empty;
                    sceneGuid.stringValue = string.Empty;
                    displayName.stringValue = string.Empty;
                    return;
                }

                refId.stringValue = selected.RefId;
                refType.stringValue = selected.RefType;
                displayName.stringValue = selected.DisplayName;
                sceneGuid.stringValue = string.IsNullOrEmpty(selected.Locator.AssetPath)
                    ? string.Empty
                    : AssetDatabase.AssetPathToGUID(selected.Locator.AssetPath);
            });
        }

        /// <summary>
        /// Rewrites the serialized Ref Type and cached display name from <paramref name="provider"/>,
        /// leaving the Ref Id — the actual identity — untouched.
        /// </summary>
        /// <param name="targets">The objects being edited.</param>
        /// <param name="propertyPath">Serialized path of the reference field.</param>
        /// <param name="provider">The provider the reference currently resolves to.</param>
        private static void RefreshMetadata(
            UnityEngine.Object[] targets, string propertyPath, ReferenceProviderRecord provider)
        {
            if (provider == null)
                return;

            Write(targets, propertyPath, (refId, refType, sceneGuid, displayName) =>
            {
                refType.stringValue = provider.RefType;
                displayName.stringValue = provider.DisplayName;
                if (!string.IsNullOrEmpty(provider.Locator.AssetPath))
                    sceneGuid.stringValue = AssetDatabase.AssetPathToGUID(provider.Locator.AssetPath);
            });
        }

        /// <summary>
        /// Applies a field mutation through <see cref="SerializedObject"/> so it participates in Undo and
        /// in prefab-override tracking.
        /// </summary>
        private static void Write(
            UnityEngine.Object[] targets,
            string propertyPath,
            Action<SerializedProperty, SerializedProperty, SerializedProperty, SerializedProperty> mutate)
        {
            if (targets == null || targets.Length == 0 || string.IsNullOrEmpty(propertyPath))
                return;

            var serialized = new SerializedObject(targets);
            var property = serialized.FindProperty(propertyPath);
            if (property == null)
                return;

            var refId = property.FindPropertyRelative("refId");
            var refType = property.FindPropertyRelative("refType");
            var sceneGuid = property.FindPropertyRelative("sceneGuid");
            var displayName = property.FindPropertyRelative("cachedDisplayName");
            if (refId == null || refType == null || sceneGuid == null || displayName == null)
                return;

            mutate(refId, refType, sceneGuid, displayName);
            serialized.ApplyModifiedProperties();

            // The stored identity changed, so any cached audit result about it is out of date.
            ReferenceAuditService.Invalidate("a reference field was edited in the Inspector");
        }

        private static void SelectAndPing(ReferenceProviderRecord provider)
        {
            var target = ReferenceProviderLookup.ResolveObject(provider);
            if (target == null)
                return;

            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        #endregion

        #region Icons

        // Cached: OnGUI runs every repaint for every drawn field, so building these per call would allocate
        // continuously while an Inspector is merely open.
        private static GUIContent _warningIcon;
        private static GUIContent _errorIcon;
        private static GUIContent _refreshIcon;
        private static GUIContent _selectIcon;
        private static GUIContent _openInHubIcon;

        private static GUIContent WarningIcon() => _warningIcon ??= Icon("console.warnicon.sml", "⚠");

        private static GUIContent ErrorIcon() => _errorIcon ??= Icon("console.erroricon.sml", "✖");

        /// <summary>
        /// First built-in icon that actually resolves, falling back to a text glyph. Editor icon names
        /// differ between skins and versions, so a missing icon must degrade rather than draw nothing.
        /// </summary>
        private static GUIContent Icon(params string[] namesThenFallbackText)
        {
            for (var i = 0; i < namesThenFallbackText.Length - 1; i++)
            {
                var content = EditorGUIUtility.IconContent(namesThenFallbackText[i]);
                if (content?.image != null)
                    return new GUIContent(content);
            }

            return new GUIContent(namesThenFallbackText[namesThenFallbackText.Length - 1]);
        }

        #endregion

        #region Picker

        /// <summary>
        /// Searchable target picker. Shows every candidate a reference could point at, grouped by Ref
        /// Type, and marks ids that more than one object claims instead of quietly offering one of them.
        /// </summary>
        internal sealed class ReferencePickerPopup : EditorWindow
        {
            private const float PopupWidth = 340f;
            private const float PopupHeight = 380f;
            private const float SearchFieldHeight = 22f;
            private const float RowHeight = 20f;
            private const float GroupHeaderHeight = 18f;
            private const float ItemIndent = 12f;

            private UnityEngine.Object[] _targets;
            private string _propertyPath;
            private string _currentRefId;
            private string _search = string.Empty;
            private Vector2 _scroll;
            private bool _focusSearch;

            private List<(string RefType, List<ReferenceProviderRecord> Items)> _allGroups;
            private List<(string RefType, List<ReferenceProviderRecord> Items)> _filteredGroups;
            private HashSet<string> _duplicatedKeys;
            private string _scopeNote = string.Empty;
            private Action<UnityEngine.Object[], string, ReferenceProviderRecord> _apply;

            /// <summary>
            /// Opens the picker under <paramref name="buttonRect"/>.
            /// </summary>
            /// <param name="buttonRect">Screen-space rect of the button that opened it.</param>
            /// <param name="targets">The objects being edited.</param>
            /// <param name="propertyPath">Serialized path of the reference field.</param>
            /// <param name="expectedType">
            /// When non-null, only providers assignable to it are offered, so a typed field cannot be
            /// pointed at an object that fails the cast.
            /// </param>
            /// <param name="candidateFilter">
            /// When non-null, an additional legality test — used by the scoped v2 field to offer only
            /// targets its scope can actually reach, rather than every provider in the project.
            /// </param>
            /// <param name="scopeNote">
            /// Explanation shown when <paramref name="candidateFilter"/> leaves nothing to choose. An
            /// empty list is otherwise indistinguishable from a broken picker.
            /// </param>
            /// <param name="idFieldName">
            /// Name of the serialized id field, so the picker can mark the current selection. Differs
            /// between the v1 and v2 structs.
            /// </param>
            /// <param name="apply">
            /// Writes the chosen provider into the field. Defaults to the v1 layout; the v2 drawer
            /// supplies its own so both share this popup instead of duplicating it.
            /// </param>
            public static void Show(
                Rect buttonRect,
                UnityEngine.Object[] targets,
                string propertyPath,
                Type expectedType = null,
                Func<ReferenceProviderRecord, bool> candidateFilter = null,
                string scopeNote = null,
                string idFieldName = "refId",
                Action<UnityEngine.Object[], string, ReferenceProviderRecord> apply = null)
            {
                var window = CreateInstance<ReferencePickerPopup>();
                window._targets = targets;
                window._propertyPath = propertyPath;
                window._scopeNote = scopeNote ?? string.Empty;
                window._apply = apply ?? ApplySelection;

                var providers = ReferenceProviderLookup.SelectableProviders(expectedType);
                if (candidateFilter != null)
                    providers = providers.Where(candidateFilter).ToList();

                // Duplicated exact keys are computed once so the list can flag them; the old picker had
                // no notion of them at all and happily offered both entries as if either would work.
                window._duplicatedKeys = new HashSet<string>(
                    providers.GroupBy(p => p.RefType + "|" + p.RefId, StringComparer.Ordinal)
                        .Where(g => g.Count() > 1)
                        .Select(g => g.Key),
                    StringComparer.Ordinal);

                window._allGroups = providers
                    .GroupBy(p => string.IsNullOrEmpty(p.RefType) ? "(no Ref Type)" : p.RefType)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => (g.Key, g.ToList()))
                    .ToList();
                window._filteredGroups = new List<(string, List<ReferenceProviderRecord>)>(window._allGroups);

                if (targets is { Length: > 0 } && !string.IsNullOrEmpty(propertyPath))
                {
                    var serialized = new SerializedObject(targets);
                    window._currentRefId = serialized.FindProperty(propertyPath)
                        ?.FindPropertyRelative(idFieldName)?.stringValue ?? string.Empty;
                }

                window._focusSearch = true;
                window.ShowAsDropDown(buttonRect, new Vector2(PopupWidth, PopupHeight));
            }

            private void OnGUI()
            {
                if (_targets == null || _allGroups == null)
                {
                    Close();
                    return;
                }

                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                {
                    Close();
                    Event.current.Use();
                    return;
                }

                EditorGUI.BeginChangeCheck();
                GUI.SetNextControlName("MolcaReferenceSearch");
                var searchRect = new Rect(4, 4, position.width - 8, SearchFieldHeight);
                var newSearch = EditorGUI.TextField(searchRect, _search, EditorStyles.toolbarSearchField);
                if (EditorGUI.EndChangeCheck())
                {
                    _search = newSearch ?? string.Empty;
                    UpdateFilter();
                }

                if (_focusSearch)
                {
                    _focusSearch = false;
                    EditorGUI.FocusTextInControl("MolcaReferenceSearch");
                }

                var listY = searchRect.yMax + 4;

                var noneRect = new Rect(4, listY, position.width - 8, RowHeight);
                if (GUI.Button(noneRect, new GUIContent("None", "Clear this reference")))
                {
                    _apply(_targets, _propertyPath, null);
                    Close();
                    return;
                }

                listY += RowHeight + 2;

                var scrollHeight = position.height - listY - 4;
                if (scrollHeight <= 0)
                    return;

                var contentHeight = _filteredGroups.Count == 0
                    ? RowHeight * 3
                    : _filteredGroups.Sum(g => GroupHeaderHeight + g.Items.Count * RowHeight);

                _scroll = GUI.BeginScrollView(
                    new Rect(0, listY, position.width, scrollHeight), _scroll,
                    new Rect(0, 0, position.width - 20, contentHeight));

                if (_filteredGroups.Count == 0)
                {
                    // A scoped field can legitimately have nothing to offer. Saying only "No matches"
                    // would read as a broken picker rather than as the scope doing its job.
                    bool scoped = !string.IsNullOrEmpty(_scopeNote) && string.IsNullOrEmpty(_search);
                    EditorGUI.LabelField(
                        new Rect(4, 0, position.width - 24, scoped ? RowHeight * 3 : RowHeight),
                        scoped ? _scopeNote : "No matches",
                        scoped ? EditorStyles.wordWrappedMiniLabel : EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    DrawGroups();
                }

                GUI.EndScrollView();
            }

            private void DrawGroups()
            {
                float y = 0;
                foreach (var (refType, items) in _filteredGroups)
                {
                    EditorGUI.LabelField(
                        new Rect(4, y, position.width - 24, GroupHeaderHeight), refType, EditorStyles.boldLabel);
                    y += GroupHeaderHeight;

                    foreach (var item in items)
                    {
                        var rowRect = new Rect(4 + ItemIndent, y, position.width - 24 - ItemIndent, RowHeight);
                        var isDuplicated = _duplicatedKeys.Contains(item.RefType + "|" + item.RefId);
                        var label = isDuplicated
                            ? $"⚠ {item.DisplayName} ({item.RefId}) — duplicated Ref Id"
                            : $"{item.DisplayName} ({item.RefId})";

                        var style = new GUIStyle(GUI.skin.label);
                        if (isDuplicated)
                            style.normal.textColor = new Color(0.9f, 0.6f, 0.1f);
                        else if (item.RefId == _currentRefId)
                            style.normal.textColor = new Color(0.2f, 0.5f, 1f);

                        var tooltip = isDuplicated
                            ? "Another object claims the same Ref Type and Ref Id. Picking this cannot be "
                              + "resolved deterministically at runtime — fix the duplicate first."
                            : item.Locator.ToString();

                        EditorGUI.LabelField(rowRect, new GUIContent(label, tooltip), style);
                        if (GUI.Button(rowRect, GUIContent.none, GUIStyle.none))
                        {
                            _apply(_targets, _propertyPath, item);
                            Close();
                            return;
                        }

                        y += RowHeight;
                    }
                }
            }

            private void UpdateFilter()
            {
                if (string.IsNullOrWhiteSpace(_search))
                {
                    _filteredGroups = new List<(string, List<ReferenceProviderRecord>)>(_allGroups);
                    return;
                }

                var term = _search.Trim();
                _filteredGroups = new List<(string, List<ReferenceProviderRecord>)>();

                foreach (var (refType, items) in _allGroups)
                {
                    var typeMatches = Contains(refType, term);
                    var matching = typeMatches
                        ? items
                        : items.Where(p => Contains(p.DisplayName, term) || Contains(p.RefId, term)).ToList();

                    if (matching.Count > 0)
                        _filteredGroups.Add((refType, matching));
                }
            }

            private static bool Contains(string value, string term) =>
                !string.IsNullOrEmpty(value) && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion
    }
}
