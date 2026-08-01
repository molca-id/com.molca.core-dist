using System;
using System.Collections.Generic;
using System.Reflection;
using Molca.ReferenceSystem;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// Answers "could an instance of this type possibly hold a scene-object reference?" so the audit can
    /// skip the expensive part — loading the asset and building a <see cref="UnityEditor.SerializedObject"/>
    /// — for the overwhelming majority of types that cannot.
    /// </summary>
    /// <remarks>
    /// This is a static, conservative filter: it answers <c>true</c> whenever a reference is
    /// <i>possible</i>, including for <c>[SerializeReference]</c> fields whose concrete type is unknown
    /// until deserialization. Being conservative means the filter can only save work, never hide a site.
    ///
    /// Scanning every ScriptableObject in a project used to be the dominant cost of reference validation,
    /// which is why ScriptableObject-owned references were skipped entirely — and why a real broken
    /// reference stored in one went unreported. Filtering by type instead of by asset category keeps the
    /// cost down without giving up coverage.
    /// </remarks>
    public static class ReferenceFieldTypeCache
    {
        private const BindingFlags FieldFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>Deep enough for real authoring data; guards against a pathological type graph.</summary>
        private const int MaxDepth = 8;

        private static readonly Dictionary<Type, bool> Cache = new();

        /// <summary>
        /// True when <paramref name="type"/>, or anything reachable through its serialized fields, could
        /// be a <see cref="SceneObjectReference"/> or <see cref="SceneObjectReference{T}"/>.
        /// </summary>
        /// <param name="type">The component or asset type to test. Null answers false.</param>
        public static bool MayContainReference(Type type)
        {
            if (type == null)
                return false;

            if (Cache.TryGetValue(type, out var cached))
                return cached;

            // Cycles are broken by the per-walk `visiting` set rather than by the cache, so the cache is
            // written once with the finished answer.
            var result = Walk(type, 0, new HashSet<Type>());
            Cache[type] = result;
            return result;
        }

        private static bool Walk(Type type, int depth, HashSet<Type> visiting)
        {
            if (depth > MaxDepth || !visiting.Add(type))
                return false;

            try
            {
                for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                {
                    // Unity's own base classes never declare Molca references; stopping here trims the
                    // walk for every MonoBehaviour and ScriptableObject in the project.
                    if (IsEngineType(current))
                        break;

                    foreach (var field in current.GetFields(FieldFlags))
                    {
                        if (!IsSerialized(field))
                            continue;

                        if (FieldMayContainReference(field, depth, visiting))
                            return true;
                    }
                }

                return false;
            }
            finally
            {
                visiting.Remove(type);
            }
        }

        private static bool FieldMayContainReference(FieldInfo field, int depth, HashSet<Type> visiting)
        {
            // A [SerializeReference] field's concrete type is only known after deserialization, so it
            // must be treated as possible.
            if (field.IsDefined(typeof(SerializeReference), inherit: false))
                return true;

            return TypeMayContainReference(ElementTypeOf(field.FieldType), depth, visiting);
        }

        private static bool TypeMayContainReference(Type type, int depth, HashSet<Type> visiting)
        {
            if (type == null)
                return false;

            if (type == typeof(SceneObjectReference))
                return true;

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SceneObjectReference<>))
                return true;

            if (type.IsPrimitive || type.IsEnum || type == typeof(string))
                return false;

            // Unity object references are serialized as links, not inline data, so a reference held by
            // the *target* belongs to that target's own scan, not this one.
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                return false;

            if (!type.IsValueType && !IsSerializableClass(type))
                return false;

            return Walk(type, depth + 1, visiting);
        }

        /// <summary>Element type for arrays and <c>List&lt;T&gt;</c>; the type itself otherwise.</summary>
        private static Type ElementTypeOf(Type type)
        {
            if (type.IsArray)
                return type.GetElementType();

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return type.GetGenericArguments()[0];

            return type;
        }

        /// <summary>Unity's serialization rules: public fields, or private fields marked for serialization.</summary>
        private static bool IsSerialized(FieldInfo field)
        {
            if (field.IsNotSerialized || field.IsDefined(typeof(NonSerializedAttribute), inherit: false))
                return false;

            return field.IsPublic
                || field.IsDefined(typeof(SerializeField), inherit: false)
                || field.IsDefined(typeof(SerializeReference), inherit: false);
        }

        private static bool IsSerializableClass(Type type) =>
            type.IsDefined(typeof(SerializableAttribute), inherit: false);

        private static bool IsEngineType(Type type)
        {
            var ns = type.Namespace;
            return ns != null
                && (ns == "UnityEngine" || ns.StartsWith("UnityEngine.", StringComparison.Ordinal)
                 || ns == "UnityEditor" || ns.StartsWith("UnityEditor.", StringComparison.Ordinal));
        }

        /// <summary>Drops the cache. Call after a script recompile changes field layouts.</summary>
        public static void Invalidate() => Cache.Clear();
    }
}
