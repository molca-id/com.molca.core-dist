using System;
using Molca.Editor.ReferenceSystem;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Property drawer for the typed <see cref="SceneObjectReference{T}"/>.
/// </summary>
/// <remarks>
/// Identical to <see cref="SceneObjectReferenceDrawer"/> except that the field's <c>T</c> is passed to
/// <see cref="SceneObjectReferenceDrawerCore"/>, which uses it both to constrain the picker and to report
/// a target of the wrong type (<c>REF004</c>) instead of showing it as a working reference that fails the
/// cast at runtime.
/// </remarks>
[CustomPropertyDrawer(typeof(SceneObjectReference<>))]
public class SceneObjectReferenceGenericDrawer : PropertyDrawer
{
    /// <inheritdoc/>
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        SceneObjectReferenceDrawerCore.Draw(position, property, label, ExpectedTargetType());
    }

    /// <summary>
    /// The <c>T</c> of the drawn <see cref="SceneObjectReference{T}"/> field.
    /// </summary>
    /// <returns>
    /// The promised target type, or null when it cannot be determined — for an array or list element,
    /// <see cref="PropertyDrawer.fieldInfo"/> reports the collection type rather than the element type.
    /// A null result degrades to untyped behavior rather than to a wrong type constraint.
    /// </returns>
    private Type ExpectedTargetType()
    {
        var fieldType = fieldInfo?.FieldType;
        if (fieldType == null)
            return null;

        if (fieldType.IsArray)
            fieldType = fieldType.GetElementType();
        else if (fieldType.IsGenericType &&
                 fieldType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>))
            fieldType = fieldType.GetGenericArguments()[0];

        return fieldType != null
            && fieldType.IsGenericType
            && fieldType.GetGenericTypeDefinition() == typeof(SceneObjectReference<>)
                ? fieldType.GetGenericArguments()[0]
                : null;
    }
}
