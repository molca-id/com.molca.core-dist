using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Migration
{
    /// <summary>A component's position in a hierarchy, independent of any file id.</summary>
    public readonly struct ComponentLocator : IEquatable<ComponentLocator>
    {
        /// <summary>Path from the root, each step carrying its sibling index.</summary>
        public string HierarchyPath { get; }

        /// <summary>The component's type name.</summary>
        public string TypeName { get; }

        /// <summary>Which one, among components of that type on that object.</summary>
        public int Ordinal { get; }

        /// <summary>Whether this locator names anything.</summary>
        public bool IsValid => !string.IsNullOrEmpty(HierarchyPath) && !string.IsNullOrEmpty(TypeName);

        /// <summary>Creates a locator.</summary>
        public ComponentLocator(string hierarchyPath, string typeName, int ordinal)
        {
            HierarchyPath = hierarchyPath;
            TypeName = typeName;
            Ordinal = ordinal;
        }

        /// <inheritdoc/>
        public bool Equals(ComponentLocator other) =>
            string.Equals(HierarchyPath, other.HierarchyPath, StringComparison.Ordinal)
            && string.Equals(TypeName, other.TypeName, StringComparison.Ordinal)
            && Ordinal == other.Ordinal;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is ComponentLocator other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() =>
            (HierarchyPath?.GetHashCode() ?? 0) ^ (TypeName?.GetHashCode() ?? 0) ^ Ordinal;

        /// <inheritdoc/>
        public override string ToString() => $"{HierarchyPath}/{TypeName}[{Ordinal}]";
    }

    /// <summary>
    /// Translates a serialized component <c>fileID</c> into something a loaded working copy can resolve.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Migration/</c>.
    /// <b>Shape:</b> editor-only. Read-only.
    /// <para/>
    /// <b>The problem it exists for.</b> A migration reads its plan from serialized YAML, where every
    /// reference is a local file id. It applies through <c>PrefabUtility.LoadPrefabContents</c>, whose
    /// objects are scene objects carrying <i>no</i> persistent ids at all — so the ids the plan is written
    /// in cannot be looked up in the copy that has to be edited. The asset representation is the only
    /// place both exist: it resolves file ids, and it has the same hierarchy the copy does.
    /// <para/>
    /// So identity is carried across as position: path, type, and which one of that type. The sibling
    /// index is part of every path step because duplicate names are ordinary in UI content — a row of
    /// buttons all called "Button" — and a name-only path would silently address the wrong one.
    /// </remarks>
    public static class ComponentLocatorMap
    {
        /// <summary>Builds file id → locator for every component in a prefab asset.</summary>
        /// <param name="assetPath">Project-relative path of the prefab.</param>
        /// <returns>The map; empty when the asset is not a loadable prefab.</returns>
        /// <remarks>
        /// Read from the asset rather than from loaded contents, because only the asset representation
        /// carries the persistent ids this is translating away from.
        /// </remarks>
        public static Dictionary<long, ComponentLocator> FromAsset(string assetPath)
        {
            var map = new Dictionary<long, ComponentLocator>();

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null) return map;

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                string path = HierarchyPath(transform);
                var byType = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var component in transform.GetComponents<Component>())
                {
                    // A missing script reads as null and occupies a slot. It is skipped rather than
                    // counted, because the copy will not surface it either — counting it here would shift
                    // every later ordinal on the object.
                    if (component == null) continue;

                    string typeName = component.GetType().Name;
                    byType.TryGetValue(typeName, out int ordinal);
                    byType[typeName] = ordinal + 1;

                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(component, out _, out long fileId)
                        && fileId != 0)
                    {
                        map[fileId] = new ComponentLocator(path, typeName, ordinal);
                    }
                }
            }

            return map;
        }

        /// <summary>Builds file id → hierarchy path for every GameObject in a prefab asset.</summary>
        /// <param name="assetPath">Project-relative path of the prefab.</param>
        /// <returns>The map; empty when the asset is not a loadable prefab.</returns>
        /// <remarks>
        /// A GameObject's file id is not any of its components' file ids, so a component map cannot answer
        /// "which object was this?". Serialized data names an owning object directly — every MonoBehaviour
        /// carries <c>m_GameObject</c> — so that id needs its own translation.
        /// </remarks>
        public static Dictionary<long, string> GameObjectPathsFromAsset(string assetPath)
        {
            var map = new Dictionary<long, string>();

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null) return map;

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(transform.gameObject, out _,
                        out long fileId) && fileId != 0)
                {
                    map[fileId] = HierarchyPath(transform);
                }
            }

            return map;
        }

        /// <summary>Finds a GameObject in a loaded hierarchy by its path.</summary>
        /// <param name="root">The working copy's root.</param>
        /// <param name="hierarchyPath">A path from <see cref="HierarchyPath"/>.</param>
        /// <returns>The object, or <c>null</c>.</returns>
        public static GameObject ResolveGameObject(GameObject root, string hierarchyPath)
        {
            if (root == null || string.IsNullOrEmpty(hierarchyPath)) return null;

            return root.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => string.Equals(HierarchyPath(t), hierarchyPath,
                    StringComparison.Ordinal))
                ?.gameObject;
        }

        /// <summary>Resolves a locator inside a loaded hierarchy.</summary>
        /// <param name="root">The working copy's root.</param>
        /// <param name="locator">The locator to resolve.</param>
        /// <returns>The component, or <c>null</c> when the hierarchy no longer matches.</returns>
        public static Component Resolve(GameObject root, ComponentLocator locator)
        {
            if (root == null || !locator.IsValid) return null;

            var transform = root.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => string.Equals(HierarchyPath(t), locator.HierarchyPath,
                    StringComparison.Ordinal));

            if (transform == null) return null;

            return transform.GetComponents<Component>()
                .Where(c => c != null && c.GetType().Name == locator.TypeName)
                .ElementAtOrDefault(locator.Ordinal);
        }

        /// <summary>An unambiguous locator for a transform within its hierarchy.</summary>
        public static string HierarchyPath(Transform transform)
        {
            var parts = new List<string>();
            for (var current = transform; current != null; current = current.parent)
                parts.Add($"{current.name}[{current.GetSiblingIndex()}]");
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
