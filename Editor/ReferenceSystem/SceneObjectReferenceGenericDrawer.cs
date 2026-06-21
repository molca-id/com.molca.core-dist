using UnityEngine;
using UnityEditor;
using Molca.ReferenceSystem;
using System.Linq;

[CustomPropertyDrawer(typeof(SceneObjectReference<>))]
public class SceneObjectReferenceGenericDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        var refIdProp = property.FindPropertyRelative("refId");
        var refTypeProp = property.FindPropertyRelative("refType");
        var sceneGuidProp = property.FindPropertyRelative("sceneGuid");
        var displayNameProp = property.FindPropertyRelative("cachedDisplayName");

        var typeArg = fieldInfo.FieldType.GetGenericArguments()[0];

        string currentButtonText = "None";
        IReferenceable foundObjectInScene = null;
        if (!string.IsNullOrEmpty(refIdProp.stringValue))
        {
            var sceneReferenceables = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OfType<IReferenceable>()
                .Where(r => typeArg.IsAssignableFrom(r.GetType()))
                .ToList();

            foundObjectInScene = sceneReferenceables.FirstOrDefault(r => r.RefId == refIdProp.stringValue);

            if (foundObjectInScene != null)
            {
                if (Event.current.type != EventType.Layout)
                    SyncMetadata(refTypeProp, displayNameProp, foundObjectInScene);
                currentButtonText = $"{foundObjectInScene.DisplayName} ({foundObjectInScene.RefType})";
            }
            else
            {
                string sceneName = "Unknown Scene";
                if (!string.IsNullOrEmpty(sceneGuidProp.stringValue))
                {
                    string path = AssetDatabase.GUIDToAssetPath(sceneGuidProp.stringValue);
                    sceneName = string.IsNullOrEmpty(path)
                        ? "DELETED SCENE"
                        : System.IO.Path.GetFileNameWithoutExtension(path);
                }
                currentButtonText = $"[In Scene: {sceneName}] {displayNameProp.stringValue} ({refTypeProp.stringValue})";
            }
        }

        Rect prefixRect = EditorGUI.PrefixLabel(position, label);
        const float selectButtonWidth = 22f;
        Rect selectRect = new Rect(prefixRect.xMax - selectButtonWidth, prefixRect.y, selectButtonWidth, prefixRect.height);
        Rect buttonRect = new Rect(prefixRect.x, prefixRect.y, prefixRect.width - selectButtonWidth - 2f, prefixRect.height);

        if (GUI.Button(buttonRect, new GUIContent(currentButtonText), EditorStyles.popup))
        {
            Rect screenRect = new Rect(GUIUtility.GUIToScreenPoint(buttonRect.position), buttonRect.size);
            SceneObjectReferenceDrawer.SceneObjectReferenceSearchPopup.Show(
                screenRect,
                property.serializedObject.targetObjects,
                property.propertyPath,
                typeArg);
        }

        EditorGUI.BeginDisabledGroup(foundObjectInScene == null);
        GUIContent selectIcon = EditorGUIUtility.IconContent("d_ViewToolMove") ?? new GUIContent("SEL");
        selectIcon.tooltip = "Select referenced object";
        if (GUI.Button(selectRect, selectIcon, EditorStyles.miniButton))
        {
            if (foundObjectInScene is Object unityObj)
            {
                Selection.activeObject = unityObj;
                EditorGUIUtility.PingObject(unityObj);
            }
        }
        EditorGUI.EndDisabledGroup();

        EditorGUI.EndProperty();
    }

    private static void SyncMetadata(SerializedProperty refTypeProp, SerializedProperty displayNameProp, IReferenceable found)
    {
        string type = found.RefType ?? string.Empty;
        string display = found.DisplayName ?? string.Empty;
        if (refTypeProp.stringValue == type && displayNameProp.stringValue == display)
            return;
        refTypeProp.stringValue = type;
        displayNameProp.stringValue = display;
        refTypeProp.serializedObject.ApplyModifiedProperties();
    }
}
