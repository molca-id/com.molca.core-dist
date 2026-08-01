using Molca.Editor.ReferenceSystem;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Property drawer for the untyped <see cref="SceneObjectReference"/>.
/// </summary>
/// <remarks>
/// All behavior lives in <see cref="SceneObjectReferenceDrawerCore"/>, shared with
/// <see cref="SceneObjectReferenceGenericDrawer"/>, so the typed and untyped fields cannot present the
/// same reference differently. The untyped struct promises no particular target type, so no type
/// constraint is passed to the picker.
/// </remarks>
[CustomPropertyDrawer(typeof(SceneObjectReference))]
public class SceneObjectReferenceDrawer : PropertyDrawer
{
    /// <inheritdoc/>
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        SceneObjectReferenceDrawerCore.Draw(position, property, label, expectedType: null);
    }
}
