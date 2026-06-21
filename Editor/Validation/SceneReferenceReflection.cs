using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Molca.ReferenceSystem;

namespace Molca.Editor.Validation
{
    /// <summary>
    /// One discovered <see cref="SceneObjectReference"/> location on an object — enough to read its value
    /// and to clear it in place. Used by <c>ReferenceResolutionValidator</c> (read) and
    /// <c>ClearBrokenReferenceFix</c> (mutate) so both share a single enumeration.
    /// </summary>
    public readonly struct SceneReferenceField
    {
        /// <summary>The object declaring the field (a <c>Step</c> component or a <c>StepAuxiliary</c> instance).</summary>
        public readonly object Owner;

        /// <summary>The reflected field.</summary>
        public readonly FieldInfo Field;

        /// <summary>Element index for array/list fields; <c>-1</c> for a scalar field.</summary>
        public readonly int Index;

        /// <summary>The current reference value.</summary>
        public readonly SceneObjectReference Value;

        /// <summary>Human-readable label (e.g. <c>buttonRef</c> or <c>targets[2]</c>).</summary>
        public readonly string Label;

        internal SceneReferenceField(object owner, FieldInfo field, int index, SceneObjectReference value, string label)
        {
            Owner = owner;
            Field = field;
            Index = index;
            Value = value;
            Label = label;
        }

        /// <summary>
        /// Sets this reference back to its default (unset) value in place. The caller is responsible for
        /// <c>Undo.RecordObject</c> / <c>EditorUtility.SetDirty</c> on the owning component.
        /// </summary>
        public void Clear()
        {
            if (Index < 0)
            {
                Field.SetValue(Owner, default(SceneObjectReference));
                return;
            }

            var collection = Field.GetValue(Owner);
            if (collection is IList list && Index < list.Count)
                list[Index] = default(SceneObjectReference);
        }
    }

    /// <summary>
    /// Reflection over an object's <see cref="SceneObjectReference"/> fields — scalar, array, and
    /// <c>List&lt;SceneObjectReference&gt;</c> — walking base types so references declared on a base
    /// <c>Step</c>/auxiliary are included.
    /// </summary>
    public static class SceneReferenceReflection
    {
        private const BindingFlags FieldFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>Enumerates every <see cref="SceneObjectReference"/> location on <paramref name="owner"/>.</summary>
        /// <param name="owner">The component or auxiliary to scan.</param>
        /// <returns>The discovered reference fields (scalar and collection elements).</returns>
        public static IEnumerable<SceneReferenceField> Enumerate(object owner)
        {
            if (owner == null) yield break;

            for (var type = owner.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(FieldFlags))
                {
                    var ft = field.FieldType;
                    if (ft == typeof(SceneObjectReference))
                    {
                        yield return new SceneReferenceField(
                            owner, field, -1, (SceneObjectReference)field.GetValue(owner), field.Name);
                    }
                    else if ((ft.IsArray && ft.GetElementType() == typeof(SceneObjectReference))
                             || (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>)
                                 && ft.GetGenericArguments()[0] == typeof(SceneObjectReference)))
                    {
                        if (field.GetValue(owner) is IEnumerable items)
                        {
                            int i = 0;
                            foreach (var item in items)
                            {
                                yield return new SceneReferenceField(
                                    owner, field, i, (SceneObjectReference)item, $"{field.Name}[{i}]");
                                i++;
                            }
                        }
                    }
                }
            }
        }
    }
}
