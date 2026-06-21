using UnityEngine;
using UnityEditor;
using Molca.ReferenceSystem;
using System.Collections.Generic;
using System.Linq;

[CustomPropertyDrawer(typeof(SceneObjectReference))]
public class SceneObjectReferenceDrawer : PropertyDrawer
{
    private const float SearchPopupWidth = 300f;
    private const float SearchPopupHeight = 360f;
    private const float SearchFieldHeight = 22f;
    private const float RowHeight = 20f;
    private const float GroupHeaderHeight = 18f;
    private const float ItemIndent = 12f;
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        // Get all properties
        var refIdProp = property.FindPropertyRelative("refId");
        var refTypeProp = property.FindPropertyRelative("refType");
        var sceneGuidProp = property.FindPropertyRelative("sceneGuid");
        var displayNameProp = property.FindPropertyRelative("cachedDisplayName");

        // 1. Determine the text for the button
        string currentButtonText = "None";
        IReferenceable foundObjectInScene = null;
        if (!string.IsNullOrEmpty(refIdProp.stringValue))
        {
            // Find all IReferenceable MonoBehaviours in the *active* scene
            var sceneReferenceables = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                                            .OfType<IReferenceable>()
                                            .ToList();
            
            // Match by ref id only so we still find the object after its RefType changes (e.g. ReferenceableComponent.refType).
            foundObjectInScene = sceneReferenceables.FirstOrDefault(r => r.RefId == refIdProp.stringValue);

            if (foundObjectInScene != null)
            {
                // Serialized refType / cached display name are set at pick time; if the target's RefType or display name
                // changed later, runtime resolve would use stale refType until re-assigned. Sync here so asset data matches.
                if (Event.current.type != EventType.Layout)
                    SyncSerializedReferenceMetadata(refTypeProp, displayNameProp, foundObjectInScene);

                currentButtonText = $"{foundObjectInScene.DisplayName} ({foundObjectInScene.RefType})";
            }
            else
            {
                // Object is not in this scene, use the "Missing" logic
                string sceneName = "Unknown Scene";
                if (!string.IsNullOrEmpty(sceneGuidProp.stringValue))
                {
                    string path = AssetDatabase.GUIDToAssetPath(sceneGuidProp.stringValue);
                    if (!string.IsNullOrEmpty(path))
                    {
                        sceneName = System.IO.Path.GetFileNameWithoutExtension(path);
                    }
                    else
                    {
                        sceneName = "DELETED SCENE";
                    }
                }
                currentButtonText = $"[In Scene: {sceneName}] {displayNameProp.stringValue} ({refTypeProp.stringValue})";
            }
        }

        // 2. Draw the Dropdown Button (with select helper on the right)
        Rect prefixRect = EditorGUI.PrefixLabel(position, label);
        const float selectButtonWidth = 22f;
        Rect selectRect = new Rect(prefixRect.xMax - selectButtonWidth, prefixRect.y, selectButtonWidth, prefixRect.height);
        Rect buttonRect = new Rect(prefixRect.x, prefixRect.y, prefixRect.width - selectButtonWidth - 2f, prefixRect.height);

        if (GUI.Button(buttonRect, new GUIContent(currentButtonText), EditorStyles.popup))
        {
            Rect screenRect = new Rect(GUIUtility.GUIToScreenPoint(buttonRect.position), buttonRect.size);
            SceneObjectReferenceSearchPopup.Show(screenRect, property.serializedObject.targetObjects, property.propertyPath);
        }

        EditorGUI.BeginDisabledGroup(foundObjectInScene == null);
        GUIContent selectIcon = EditorGUIUtility.IconContent("d_ViewToolMove");
        if (selectIcon == null || selectIcon.image == null)
        {
            selectIcon = EditorGUIUtility.IconContent("ViewToolMove");
        }
        if (selectIcon == null || selectIcon.image == null)
        {
            selectIcon = new GUIContent("SEL");
        }
        selectIcon.tooltip = "Select referenced object";
        if (GUI.Button(selectRect, selectIcon, EditorStyles.miniButton))
        {
            SelectReferencedObject(foundObjectInScene);
        }
        EditorGUI.EndDisabledGroup();

        EditorGUI.EndProperty();
    }

    private static void SyncSerializedReferenceMetadata(SerializedProperty refTypeProp, SerializedProperty displayNameProp, IReferenceable found)
    {
        if (refTypeProp == null || displayNameProp == null || found == null)
            return;

        string type = found.RefType ?? string.Empty;
        string display = found.DisplayName ?? string.Empty;
        if (refTypeProp.stringValue == type && displayNameProp.stringValue == display)
            return;

        refTypeProp.stringValue = type;
        displayNameProp.stringValue = display;
        refTypeProp.serializedObject.ApplyModifiedProperties();
    }

    private void SelectReferencedObject(IReferenceable referenceable)
    {
        if (referenceable is Object unityObject)
        {
            Selection.activeObject = unityObject;
            EditorGUIUtility.PingObject(unityObject);
        }
    }

    private static void ApplySelection(Object[] targets, string propertyPath, IReferenceable selected)
    {
        if (targets == null || targets.Length == 0 || string.IsNullOrEmpty(propertyPath))
            return;

        var so = new SerializedObject(targets);
        var prop = so.FindProperty(propertyPath);
        if (prop == null)
            return;

        var refIdProp = prop.FindPropertyRelative("refId");
        var refTypeProp = prop.FindPropertyRelative("refType");
        var sceneGuidProp = prop.FindPropertyRelative("sceneGuid");
        var displayNameProp = prop.FindPropertyRelative("cachedDisplayName");

        if (selected == null)
        {
            refIdProp.stringValue = string.Empty;
            refTypeProp.stringValue = string.Empty;
            sceneGuidProp.stringValue = string.Empty;
            displayNameProp.stringValue = string.Empty;
        }
        else
        {
            refIdProp.stringValue = selected.RefId;
            refTypeProp.stringValue = selected.RefType;
            displayNameProp.stringValue = selected.DisplayName;
            if (selected is MonoBehaviour mb && mb.gameObject != null)
            {
                string scenePath = mb.gameObject.scene.path;
                sceneGuidProp.stringValue = AssetDatabase.AssetPathToGUID(scenePath);
            }
        }

        so.ApplyModifiedProperties();
    }

    internal sealed class SceneObjectReferenceSearchPopup : EditorWindow
    {
        private Object[] _targets;
        private string _propertyPath;
        private string _search = "";
        private Vector2 _scroll;
        private List<(string refType, List<IReferenceable> items)> _allGroups;
        private List<(string refType, List<IReferenceable> items)> _filteredGroups;
        private bool _focusSearch;
        private string _currentRefId;

        /// <param name="typeFilter">When non-null, only referenceables assignable to this type are shown.</param>
        public static void Show(Rect buttonRect, Object[] targets, string propertyPath, System.Type typeFilter = null)
        {
            var window = CreateInstance<SceneObjectReferenceSearchPopup>();
            window._targets = targets;
            window._propertyPath = propertyPath;

            var allItems = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OfType<IReferenceable>()
                .Where(r => r != null && !string.IsNullOrEmpty(r.RefId))
                .Where(r => typeFilter == null || typeFilter.IsAssignableFrom(r.GetType()))
                .ToList();
            window._allGroups = allItems
                .GroupBy(r => r.RefType ?? "Unknown")
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.OrderBy(r => r.DisplayName).ToList()))
                .ToList();
            window._filteredGroups = new List<(string, List<IReferenceable>)>(window._allGroups);

            if (targets != null && targets.Length > 0)
            {
                var so = new SerializedObject(targets);
                var prop = so.FindProperty(propertyPath);
                if (prop != null)
                    window._currentRefId = prop.FindPropertyRelative("refId")?.stringValue ?? "";
            }

            window._focusSearch = true;
            window.ShowAsDropDown(buttonRect, new Vector2(SearchPopupWidth, SearchPopupHeight));
        }

        private void OnGUI()
        {
            if (_targets == null || _allGroups == null)
            {
                Close();
                return;
            }

            Event e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    Close();
                    e.Use();
                    return;
                }
            }

            // Search field
            EditorGUI.BeginChangeCheck();
            GUI.SetNextControlName("SceneRefSearch");
            Rect searchRect = new Rect(4, 4, position.width - 8, SearchFieldHeight);
            string newSearch = EditorGUI.TextField(searchRect, _search, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
            {
                _search = newSearch ?? "";
                UpdateFilter();
            }

            if (_focusSearch)
            {
                _focusSearch = false;
                EditorGUI.FocusTextInControl("SceneRefSearch");
            }

            float listY = searchRect.yMax + 4;

            // "None" option
            Rect noneRect = new Rect(4, listY, position.width - 8, RowHeight);
            if (GUI.Button(noneRect, new GUIContent("None")))
            {
                ApplySelection(_targets, _propertyPath, null);
                Close();
                return;
            }
            listY += RowHeight + 2;

            float scrollViewHeight = position.height - listY - 4;
            if (scrollViewHeight <= 0)
                return;

            // Content height: one row per group header + one per item
            float contentHeight = 0;
            foreach (var (_, items) in _filteredGroups)
            {
                contentHeight += GroupHeaderHeight;
                contentHeight += items.Count * RowHeight;
            }
            if (_filteredGroups.Count == 0)
                contentHeight = RowHeight;

            _scroll = GUI.BeginScrollView(new Rect(0, listY, position.width, scrollViewHeight), _scroll, new Rect(0, 0, position.width - 20, contentHeight));

            float y = 0;
            if (_filteredGroups.Count == 0)
            {
                Rect noMatchRect = new Rect(4, y, position.width - 24, RowHeight);
                EditorGUI.LabelField(noMatchRect, "No matches", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                foreach (var (refType, items) in _filteredGroups)
                {
                    // Group header
                    Rect headerRect = new Rect(4, y, position.width - 24, GroupHeaderHeight);
                    EditorGUI.LabelField(headerRect, refType, EditorStyles.boldLabel);
                    y += GroupHeaderHeight;

                    foreach (var item in items)
                    {
                        Rect rowRect = new Rect(4 + ItemIndent, y, position.width - 24 - ItemIndent, RowHeight);
                        string label = $"{item.DisplayName} ({item.RefId})";
                        bool isSelected = item.RefId == _currentRefId;
                        if (isSelected)
                        {
                            var highlight = new GUIStyle(GUI.skin.label);
                            highlight.normal.textColor = new Color(0.2f, 0.5f, 1f);
                            EditorGUI.LabelField(rowRect, label, highlight);
                        }
                        else
                        {
                            EditorGUI.LabelField(rowRect, label);
                        }
                        if (GUI.Button(rowRect, GUIContent.none, GUIStyle.none))
                        {
                            ApplySelection(_targets, _propertyPath, item);
                            Close();
                            return;
                        }
                        y += RowHeight;
                    }
                }
            }

            GUI.EndScrollView();
        }

        private void UpdateFilter()
        {
            if (string.IsNullOrWhiteSpace(_search))
            {
                _filteredGroups = new List<(string, List<IReferenceable>)>(_allGroups);
                return;
            }
            string term = _search.Trim().ToLowerInvariant();
            _filteredGroups = new List<(string, List<IReferenceable>)>();
            foreach (var (refType, items) in _allGroups)
            {
                bool typeMatches = refType != null && refType.ToLowerInvariant().Contains(term);
                var filteredItems = items
                    .Where(r => typeMatches
                         || (r.DisplayName != null && r.DisplayName.ToLowerInvariant().Contains(term))
                         || (r.RefId != null && r.RefId.ToLowerInvariant().Contains(term)))
                    .ToList();
                if (filteredItems.Count > 0 || typeMatches)
                {
                    _filteredGroups.Add((refType, typeMatches ? items : filteredItems));
                }
            }
        }
    }
}