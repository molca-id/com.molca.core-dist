using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Coerces string/structured tokens into a <see cref="SerializedProperty"/> by its property type, and
    /// reads a property's current value back into the same string form. A general-purpose, framework-agnostic
    /// serialized-property helper (it depends on nothing in the sequence system), so any editor tooling in
    /// the <c>Molca.Editor</c> assembly that reads or writes serialized fields by name — inspectors, the
    /// step field/auxiliary services, the CSV importer, MCP authoring tools — can share one neutral,
    /// testable, JSON-free path.
    /// </summary>
    /// <remarks>
    /// Extracted from <c>StepFieldEditingService</c> to <c>Editor/Serialization/</c> (Sprint 25 follow-up)
    /// once it became clear the coercion has no sequence coupling. <see cref="ReadValue"/> is the inverse of
    /// <see cref="TrySet(SerializedProperty,string,out string)"/>: a scalar read round-trips back through the
    /// string setter.
    /// </remarks>
    internal static class SerializedFieldCoercion
    {
        /// <summary>
        /// Writes a structured <see cref="FieldNode"/> into <paramref name="property"/>, recursing into
        /// composite objects (named members) and lists-of-elements so nested serializable types (e.g.
        /// <c>DynamicLocalization</c> and its <c>translations</c> list) can be authored, not just flat
        /// scalars. Scalar leaves defer to the string overload, so anything writable as a string before
        /// still writes identically. Does not call <c>ApplyModifiedProperties</c> — the caller batches that.
        /// </summary>
        /// <param name="property">The target property (or composite/array root).</param>
        /// <param name="node">The structured value to write.</param>
        /// <param name="error">Set to a human-readable reason (with the failing member/element path) on failure.</param>
        /// <returns><c>true</c> if the value was written.</returns>
        internal static bool TrySet(SerializedProperty property, FieldNode node, out string error)
        {
            error = null;
            if (node == null) { error = "null value"; return false; }

            // Scalar leaf: reuse the battle-tested string path verbatim (scalars, enums, object refs,
            // numeric tuples, comma-separated arrays, SceneObjectReference-by-Ref-Id).
            if (node.IsScalar)
                return TrySet(property, node.Scalar, out error);

            if (node.IsList)
            {
                // A non-array property given an all-scalar list is a numeric tuple authored as [x,y,z]
                // (Vector*/Color/Rect/...): fold it back to the comma form the scalar path expects.
                if (!property.isArray)
                {
                    if (node.Elements.All(e => e.IsScalar))
                        return TrySet(property, string.Join(",", node.Elements.Select(e => e.Scalar)), out error);
                    error = "field is not a list/array";
                    return false;
                }

                property.arraySize = node.Elements.Count;
                for (int i = 0; i < node.Elements.Count; i++)
                {
                    if (!TrySet(property.GetArrayElementAtIndex(i), node.Elements[i], out var elementError))
                    {
                        error = $"element {i}: {elementError}";
                        return false;
                    }
                }
                return true;
            }

            // Composite object: recurse into named relative children (private serialized fields included).
            if (property.propertyType != SerializedPropertyType.Generic || property.isArray)
            {
                error = $"field type '{property.propertyType}' is not a composite object";
                return false;
            }
            foreach (var member in node.Members)
            {
                var child = property.FindPropertyRelative(member.Key);
                if (child == null)
                {
                    error = $"member '{member.Key}': no such serialized field";
                    return false;
                }
                if (!TrySet(child, member.Value, out var memberError))
                {
                    error = $"member '{member.Key}': {memberError}";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Writes <paramref name="value"/> into <paramref name="property"/>, parsing it per the property's
        /// type. Does not call <c>ApplyModifiedProperties</c> — the caller batches that.
        /// </summary>
        /// <param name="property">The target property.</param>
        /// <param name="value">String form of the value (numbers/bools/enum names/instance ids/asset paths/Ref Ids).</param>
        /// <param name="error">Set to a human-readable reason when coercion fails.</param>
        /// <returns><c>true</c> if the property was written.</returns>
        internal static bool TrySet(SerializedProperty property, string value, out string error)
        {
            error = null;

            // Arrays and generic lists (propertyType is Generic, isArray is true) accept a
            // comma-separated list of element tokens, each coerced by the element's own type.
            // Checked before the type switch because List<T>/T[] report SerializedPropertyType.Generic.
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
                return TrySetArray(property, value, out error);

            switch (property.propertyType)
            {
                case SerializedPropertyType.String:
                    property.stringValue = value ?? string.Empty;
                    return true;

                case SerializedPropertyType.Integer:
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    {
                        property.longValue = l;
                        return true;
                    }
                    error = "expected an integer";
                    return false;

                case SerializedPropertyType.Boolean:
                    if (bool.TryParse(value, out var b))
                    {
                        property.boolValue = b;
                        return true;
                    }
                    error = "expected 'true' or 'false'";
                    return false;

                case SerializedPropertyType.Float:
                    if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands,
                            CultureInfo.InvariantCulture, out var d))
                    {
                        property.doubleValue = d;
                        return true;
                    }
                    error = "expected a number";
                    return false;

                case SerializedPropertyType.Enum:
                    return TrySetEnum(property, value, out error);

                case SerializedPropertyType.ObjectReference:
                    return TrySetObject(property, value, out error);

                case SerializedPropertyType.Vector2:
                    if (TryParseFloats(value, 2, out var v2, out error))
                    {
                        property.vector2Value = new Vector2(v2[0], v2[1]);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Vector3:
                    if (TryParseFloats(value, 3, out var v3, out error))
                    {
                        property.vector3Value = new Vector3(v3[0], v3[1], v3[2]);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Vector4:
                    if (TryParseFloats(value, 4, out var v4, out error))
                    {
                        property.vector4Value = new Vector4(v4[0], v4[1], v4[2], v4[3]);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Quaternion:
                    if (TryParseFloats(value, 4, out var q, out error))
                    {
                        property.quaternionValue = new Quaternion(q[0], q[1], q[2], q[3]);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Vector2Int:
                    if (TryParseInts(value, 2, out var v2i, out error))
                    {
                        property.vector2IntValue = new Vector2Int(v2i[0], v2i[1]);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Vector3Int:
                    if (TryParseInts(value, 3, out var v3i, out error))
                    {
                        property.vector3IntValue = new Vector3Int(v3i[0], v3i[1], v3i[2]);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Color:
                    return TrySetColor(property, value, out error);

                case SerializedPropertyType.Rect:
                    if (TryParseFloats(value, 4, out var r, out error))
                    {
                        property.rectValue = new Rect(r[0], r[1], r[2], r[3]);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Bounds:
                    // center(x,y,z), size(x,y,z).
                    if (TryParseFloats(value, 6, out var bd, out error))
                    {
                        property.boundsValue = new Bounds(
                            new Vector3(bd[0], bd[1], bd[2]), new Vector3(bd[3], bd[4], bd[5]));
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Generic:
                    // Composite serializable types we support by Ref Id (e.g. SceneObjectReference,
                    // which holds a child 'refId' string).
                    var refIdChild = property.FindPropertyRelative("refId");
                    if (refIdChild != null && refIdChild.propertyType == SerializedPropertyType.String)
                    {
                        refIdChild.stringValue = value ?? string.Empty;
                        return true;
                    }
                    error = "unsupported composite field (no 'refId' child to set)";
                    return false;

                default:
                    error = $"unsupported field type '{property.propertyType}'";
                    return false;
            }
        }

        /// <summary>
        /// Reads a property's current value into the same string form <see cref="TrySet(SerializedProperty,string,out string)"/>
        /// consumes (so a scalar read round-trips). Composite objects without a <c>refId</c> child are
        /// rendered as <c>{ name: value; … }</c> and arrays as <c>[a, b, …]</c> for inspection — those
        /// composite forms are informational, not guaranteed to round-trip through the string setter.
        /// </summary>
        internal static string ReadValue(SerializedProperty property)
        {
            if (property == null) return string.Empty;

            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                var elements = new List<string>(property.arraySize);
                for (int i = 0; i < property.arraySize; i++)
                    elements.Add(ReadValue(property.GetArrayElementAtIndex(i)));
                return "[" + string.Join(", ", elements) + "]";
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.String:
                    return property.stringValue ?? string.Empty;
                case SerializedPropertyType.Integer:
                    return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return property.doubleValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex >= 0 && property.enumValueIndex < property.enumNames.Length
                        ? property.enumNames[property.enumValueIndex]
                        : property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.ObjectReference:
                    return ReadObject(property);
                case SerializedPropertyType.Vector2:
                    return Floats(property.vector2Value.x, property.vector2Value.y);
                case SerializedPropertyType.Vector3:
                    return Floats(property.vector3Value.x, property.vector3Value.y, property.vector3Value.z);
                case SerializedPropertyType.Vector4:
                    return Floats(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w);
                case SerializedPropertyType.Quaternion:
                    return Floats(property.quaternionValue.x, property.quaternionValue.y, property.quaternionValue.z, property.quaternionValue.w);
                case SerializedPropertyType.Vector2Int:
                    return $"{property.vector2IntValue.x},{property.vector2IntValue.y}";
                case SerializedPropertyType.Vector3Int:
                    return $"{property.vector3IntValue.x},{property.vector3IntValue.y},{property.vector3IntValue.z}";
                case SerializedPropertyType.Color:
                    return "#" + ColorUtility.ToHtmlStringRGBA(property.colorValue);
                case SerializedPropertyType.Rect:
                    return Floats(property.rectValue.x, property.rectValue.y, property.rectValue.width, property.rectValue.height);
                case SerializedPropertyType.Bounds:
                    var bn = property.boundsValue;
                    return Floats(bn.center.x, bn.center.y, bn.center.z, bn.size.x, bn.size.y, bn.size.z);
                case SerializedPropertyType.Generic:
                    var refIdChild = property.FindPropertyRelative("refId");
                    if (refIdChild != null && refIdChild.propertyType == SerializedPropertyType.String)
                        return refIdChild.stringValue ?? string.Empty;
                    return ReadComposite(property);
                default:
                    return property.propertyType.ToString();
            }
        }

        private static string ReadObject(SerializedProperty property)
        {
            var obj = property.objectReferenceValue;
            if (obj == null) return "null";
            var path = AssetDatabase.GetAssetPath(obj);
            return string.IsNullOrEmpty(path) ? obj.name : path;
        }

        private static string ReadComposite(SerializedProperty property)
        {
            var parts = new List<string>();
            var iterator = property.Copy();
            var end = property.GetEndProperty();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                parts.Add($"{iterator.name}: {ReadValue(iterator)}");
            }
            return "{ " + string.Join("; ", parts) + " }";
        }

        private static string Floats(params float[] values)
            => string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

        private static bool TrySetEnum(SerializedProperty property, string value, out string error)
        {
            error = null;
            int byName = Array.FindIndex(property.enumNames,
                n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
            if (byName >= 0)
            {
                property.enumValueIndex = byName;
                return true;
            }
            if (int.TryParse(value, out var idx) && idx >= 0 && idx < property.enumNames.Length)
            {
                property.enumValueIndex = idx;
                return true;
            }
            error = $"expected one of: {string.Join(", ", property.enumNames)}";
            return false;
        }

        private static bool TrySetObject(SerializedProperty property, string value, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(value) || value == "null")
            {
                property.objectReferenceValue = null;
                return true;
            }

            // Entity/instance id (scene objects) or asset path (project assets).
            if (int.TryParse(value, out var instanceId))
            {
                var obj = EditorUtility.EntityIdToObject(instanceId);
                if (obj != null)
                {
                    property.objectReferenceValue = obj;
                    return true;
                }
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(value);
            if (asset != null)
            {
                property.objectReferenceValue = asset;
                return true;
            }

            error = "could not resolve object reference (use an instance id or an asset path)";
            return false;
        }

        /// <summary>
        /// Sets an array/list property from a comma-separated list of element tokens, coercing each by
        /// the element's own type. An empty/whitespace value clears the collection. Element parsing
        /// reuses <see cref="TrySet(SerializedProperty,string,out string)"/>, so a bad element reports its
        /// 0-based index in the error.
        /// </summary>
        /// <remarks>
        /// Elements are split on commas; element values that themselves contain commas (e.g. some
        /// string lists) are not representable through this single-string path.
        /// </remarks>
        private static bool TrySetArray(SerializedProperty property, string value, out string error)
        {
            error = null;
            var tokens = string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(',');

            property.arraySize = tokens.Length;
            for (int i = 0; i < tokens.Length; i++)
            {
                var element = property.GetArrayElementAtIndex(i);
                if (!TrySet(element, tokens[i].Trim(), out var elementError))
                {
                    error = $"element {i}: {elementError}";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Sets a Color from "r,g,b" / "r,g,b,a" components (0–1 floats) or a "#RRGGBB"/"#RRGGBBAA"
        /// hex string. Alpha defaults to 1 when only three components are given.
        /// </summary>
        private static bool TrySetColor(SerializedProperty property, string value, out string error)
        {
            error = null;
            if (!string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value.Trim(), out var hex))
                {
                    property.colorValue = hex;
                    return true;
                }
                error = "expected a '#RRGGBB' or '#RRGGBBAA' hex color";
                return false;
            }

            var parts = (value ?? string.Empty).Split(',');
            if ((parts.Length == 3 || parts.Length == 4) && TryParseFloats(value, parts.Length, out var c, out _))
            {
                property.colorValue = new Color(c[0], c[1], c[2], parts.Length == 4 ? c[3] : 1f); // doctor:ignore — parses a color field value, not UI chrome
                return true;
            }
            error = "expected 'r,g,b' or 'r,g,b,a' (0–1 floats) or a '#RRGGBB' hex color";
            return false;
        }

        /// <summary>Parses exactly <paramref name="count"/> invariant-culture floats from a comma list.</summary>
        private static bool TryParseFloats(string value, int count, out float[] result, out string error)
        {
            result = new float[count];
            error = null;
            var parts = (value ?? string.Empty).Split(',');
            if (parts.Length != count)
            {
                error = $"expected {count} comma-separated numbers";
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out result[i]))
                {
                    error = $"component {i} is not a number";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Parses exactly <paramref name="count"/> integers from a comma list.</summary>
        private static bool TryParseInts(string value, int count, out int[] result, out string error)
        {
            result = new int[count];
            error = null;
            var parts = (value ?? string.Empty).Split(',');
            if (parts.Length != count)
            {
                error = $"expected {count} comma-separated integers";
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
                {
                    error = $"component {i} is not an integer";
                    return false;
                }
            }
            return true;
        }
    }
}
