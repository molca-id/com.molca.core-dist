using System;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// Inspector drawer for <see cref="SceneObjectReferenceV2"/>: the target, the scope its id lives
    /// in, and how much the owner depends on it.
    /// </summary>
    /// <remarks>
    /// <para>The picker is scope-aware, which is the whole reason this drawer exists separately.
    /// A prefab-local field only offers targets inside its own prefab, because nothing else can
    /// satisfy a key resolved relative to the live scope root. Offering the rest would let an author
    /// choose a reference that looks fine in the Inspector and can never resolve at runtime — and the
    /// person who finds out is not the person who chose it.</para>
    ///
    /// <para>Like the v1 drawer, <b>drawing never mutates serialized data</b>. Every write here comes
    /// from a click.</para>
    /// </remarks>
    [CustomPropertyDrawer(typeof(SceneObjectReferenceV2))]
    internal sealed class SceneObjectReferenceV2Drawer : PropertyDrawer
    {
        private const float ActionButtonWidth = 22f;
        private const float Gap = 2f;

        /// <inheritdoc/>
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            property.isExpanded
                ? EditorGUIUtility.singleLineHeight * 4 + EditorGUIUtility.standardVerticalSpacing * 3
                : EditorGUIUtility.singleLineHeight;

        /// <inheritdoc/>
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var targetId = property.FindPropertyRelative("targetId");
            var scopeKind = property.FindPropertyRelative("scopeKind");
            var scopeId = property.FindPropertyRelative("scopeId");
            var expectedRefType = property.FindPropertyRelative("expectedRefType");
            var requiredness = property.FindPropertyRelative("requiredness");
            var availability = property.FindPropertyRelative("availability");

            if (targetId == null || scopeKind == null)
            {
                EditorGUI.LabelField(position, label, new GUIContent("Unsupported reference layout"));
                EditorGUI.EndProperty();
                return;
            }

            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

            DrawTargetLine(line, property, label, targetId, scopeKind, expectedRefType);

            if (property.isExpanded)
            {
                float step = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

                line.y += step;
                DrawScopeLine(line, scopeKind, scopeId);

                line.y += step;
                EditorGUI.PropertyField(line, requiredness, new GUIContent(
                    "Requiredness",
                    "Optional: unresolved is legal and silent. Required: unresolved is an editor and build "
                    + "error, and throws at runtime. Deferred required: may register later, but a timeout "
                    + "is an error."));

                line.y += step;
                EditorGUI.PropertyField(line, availability, new GUIContent(
                    "Availability",
                    "Immediate: the target must already exist. Deferred: it may arrive during a bounded "
                    + "wait. Conditional: only expected under a named load set."));
            }

            EditorGUI.EndProperty();
        }

        /// <summary>The target row: foldout, current selection, and the scoped picker.</summary>
        private static void DrawTargetLine(
            Rect line,
            SerializedProperty property,
            GUIContent label,
            SerializedProperty targetId,
            SerializedProperty scopeKind,
            SerializedProperty expectedRefType)
        {
            var foldoutRect = new Rect(line.x, line.y, EditorGUIUtility.labelWidth, line.height);
            property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, toggleOnLabelClick: true);

            float x = line.x + EditorGUIUtility.labelWidth;
            float right = line.xMax;

            var openRect = new Rect(right - ActionButtonWidth, line.y, ActionButtonWidth, line.height);
            var buttonRect = new Rect(x, line.y, Mathf.Max(24f, openRect.x - Gap - x), line.height);

            var kind = CurrentScope(scopeKind);
            string ownerPath = OwnerAssetPath(property);
            string stored = targetId.stringValue ?? string.Empty;

            var caption = string.IsNullOrEmpty(stored)
                ? new GUIContent("None", "No target assigned.")
                : new GUIContent(
                    DescribeTarget(stored, expectedRefType, kind),
                    $"{ReferenceScopeCandidates.Describe(kind, ownerPath)}\n\nStored id: {stored}");

            if (GUI.Button(buttonRect, caption, EditorStyles.popup))
            {
                var screenRect = new Rect(GUIUtility.GUIToScreenPoint(buttonRect.position), buttonRect.size);
                var targets = property.serializedObject.targetObjects;
                string propertyPath = property.propertyPath;

                SceneObjectReferenceDrawerCore.ReferencePickerPopup.Show(
                    screenRect,
                    targets,
                    propertyPath,
                    expectedType: null,
                    candidateFilter: ReferenceScopeCandidates.Predicate(kind, ownerPath),
                    scopeNote: ReferenceScopeCandidates.Describe(kind, ownerPath),
                    idFieldName: "targetId",
                    apply: (t, path, selected) => ApplySelection(t, path, selected, kind, ownerPath));
            }

            _openIcon ??= Icon("Search Icon", "d_Search Icon", "REF");
            _openIcon.tooltip =
                "Open this reference in the Hub's References workspace: its full locator, every candidate "
                + "target, and the repairs available for it.";
            if (GUI.Button(openRect, _openIcon, EditorStyles.miniButton))
                OpenInReferences(property);
        }

        /// <summary>
        /// The scope row. The scope id is shown read-only: it is derived from where the reference is
        /// authored, and hand-editing it would silently point the reference at a scope that may not
        /// exist.
        /// </summary>
        private static void DrawScopeLine(Rect line, SerializedProperty scopeKind, SerializedProperty scopeId)
        {
            float half = (line.width - EditorGUIUtility.labelWidth - Gap) / 2f;
            var kindRect = new Rect(line.x, line.y, EditorGUIUtility.labelWidth + half, line.height);

            EditorGUI.PropertyField(kindRect, scopeKind, new GUIContent(
                "Scope",
                "Which space the target's id must be unique in. Prefab Local resolves relative to the "
                + "nearest Reference Scope Root, so two instances of one prefab do not collide."));

            if (scopeId == null)
                return;

            var idRect = new Rect(kindRect.xMax + Gap, line.y, half, line.height);
            using (new EditorGUI.DisabledScope(true))
            {
                var kind = CurrentScope(scopeKind);
                bool global = kind == ReferenceScopeKind.Global || kind == ReferenceScopeKind.LegacyGlobal;
                EditorGUI.TextField(idRect, global ? "(no scope id)" : scopeId.stringValue ?? string.Empty);
            }
        }

        /// <summary>Writes the chosen provider, including the scope the field is authored in.</summary>
        private static void ApplySelection(
            UnityEngine.Object[] targets,
            string propertyPath,
            ReferenceProviderRecord selected,
            ReferenceScopeKind kind,
            string ownerAssetPath)
        {
            if (targets == null || targets.Length == 0 || string.IsNullOrEmpty(propertyPath))
                return;

            var serialized = new SerializedObject(targets);
            var property = serialized.FindProperty(propertyPath);
            if (property == null)
                return;

            var targetId = property.FindPropertyRelative("targetId");
            var expectedRefType = property.FindPropertyRelative("expectedRefType");
            var scopeId = property.FindPropertyRelative("scopeId");
            var displayName = property.FindPropertyRelative("cachedDisplayName");
            var assetGuid = property.FindPropertyRelative("targetAssetGuid");
            var localFileId = property.FindPropertyRelative("targetLocalFileId");

            if (targetId == null || expectedRefType == null)
                return;

            if (selected == null)
            {
                targetId.stringValue = string.Empty;
                expectedRefType.stringValue = string.Empty;
                if (displayName != null) displayName.stringValue = string.Empty;
                if (assetGuid != null) assetGuid.stringValue = string.Empty;
                if (localFileId != null) localFileId.longValue = 0;
            }
            else
            {
                targetId.stringValue = selected.RefId;
                expectedRefType.stringValue = selected.RefType;
                if (displayName != null) displayName.stringValue = selected.DisplayName;

                if (assetGuid != null && !string.IsNullOrEmpty(selected.Locator.AssetPath))
                    assetGuid.stringValue = AssetDatabase.AssetPathToGUID(selected.Locator.AssetPath);
                if (localFileId != null)
                    localFileId.longValue = selected.Locator.LocalFileId;

                // The scope id follows from where the reference is authored, not from the target. For a
                // prefab-local field that is the prefab itself; the live instance id is substituted at
                // resolve time, because it does not exist until the prefab is instantiated.
                bool global = kind == ReferenceScopeKind.Global || kind == ReferenceScopeKind.LegacyGlobal;
                if (scopeId != null)
                    scopeId.stringValue = global ? string.Empty : ownerAssetPath ?? string.Empty;
            }

            serialized.ApplyModifiedProperties();

            // The stored identity changed, so any cached audit result about it is out of date.
            ReferenceAuditService.Invalidate("a reference field was edited in the Inspector");
        }

        private static ReferenceScopeKind CurrentScope(SerializedProperty scopeKind)
        {
            var values = (ReferenceScopeKind[])Enum.GetValues(typeof(ReferenceScopeKind));
            int index = scopeKind.enumValueIndex;
            return index >= 0 && index < values.Length ? values[index] : ReferenceScopeKind.LegacyGlobal;
        }

        /// <summary>The asset the reference is authored in, which is what its scope is relative to.</summary>
        private static string OwnerAssetPath(SerializedProperty property)
        {
            var owner = property?.serializedObject?.targetObject;
            return owner == null ? string.Empty : ReferenceObjectLocator.For(owner).AssetPath;
        }

        private static string DescribeTarget(
            string stored, SerializedProperty expectedRefType, ReferenceScopeKind kind)
        {
            string type = expectedRefType?.stringValue ?? string.Empty;
            string prefix = kind == ReferenceScopeKind.PrefabLocal ? "local " : string.Empty;
            return string.IsNullOrEmpty(type) ? $"{prefix}{stored}" : $"{prefix}{type}: {stored}";
        }

        private static void OpenInReferences(SerializedProperty property)
        {
            var owner = property?.serializedObject?.targetObject;
            var siteKey = owner == null
                ? null
                : $"{ReferenceObjectLocator.For(owner).Key}|{property.propertyPath}";
            Hub.ReferenceHubWorkspace.Open(siteKey);
        }

        private static GUIContent _openIcon;

        /// <summary>
        /// Built-in icon names differ between skins and versions, so a missing icon degrades to text
        /// rather than drawing nothing.
        /// </summary>
        private static GUIContent Icon(params string[] namesThenFallbackText)
        {
            for (int i = 0; i < namesThenFallbackText.Length - 1; i++)
            {
                var content = EditorGUIUtility.IconContent(namesThenFallbackText[i]);
                if (content?.image != null)
                    return new GUIContent(content);
            }

            return new GUIContent(namesThenFallbackText[namesThenFallbackText.Length - 1]);
        }
    }
}
