using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor
{
    /// <summary>
    /// Single Undo-grouped path for basic GameObject edits in the loaded scene(s) — rename, set-active,
    /// transform, create, delete, add-component — plus hierarchy resolution helpers. The generic
    /// scene-authoring counterpart to the sequence-specific editing services; no GUI dependencies, every
    /// mutation is one Unity Undo group (revertible with Ctrl+Z). Shared by the <c>molca_unity_gameobject_*</c>
    /// MCP tools and reusable by any editor tooling in the <c>Molca.Editor</c> assembly.
    /// </summary>
    public static class GameObjectEditingService
    {
        /// <summary>
        /// Resolves a GameObject by integer instance id or by '/'-separated hierarchy path (e.g.
        /// "Example Sequence/Choice/PathA") across all loaded scenes.
        /// </summary>
        /// <param name="target">Instance id or hierarchy path.</param>
        /// <param name="error">Set to a reason when nothing matches.</param>
        /// <returns>The resolved GameObject, or null.</returns>
        public static GameObject Resolve(string target, out string error)
        {
            var matches = ResolveAll(target, out error);
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
            {
                error = AmbiguityMessage(target, matches);
                return null;
            }
            return null; // error already set by ResolveAll (blank / not found)
        }

        /// <summary>
        /// Every GameObject a target selector resolves to across the loaded scenes: exactly one for an
        /// instance id or an unambiguous path/name, or several when the name collides — same-named siblings
        /// share a hierarchy path, so a lone name is not a unique handle. Empty (with <paramref name="error"/>
        /// set) when the selector is blank or nothing matches.
        /// </summary>
        /// <remarks>
        /// Resolution order: (1) integer instance id — always unique; (2) exact '/'-separated path, returning
        /// <em>all</em> objects at that path (so exact-case duplicates are surfaced, not silently first-won);
        /// (3) case-insensitive tolerant fallback (see <see cref="FindTolerant"/>) so a weak model that
        /// mis-cases or shortens a name it was just given still resolves. Callers that must act on a single
        /// object use <see cref="Resolve"/> (which turns &gt;1 into an ambiguity error); callers that support
        /// multi-selection, or that need to detect ambiguity to ask the user, use this and inspect the count.
        /// </remarks>
        /// <param name="target">Instance id or hierarchy path / object name.</param>
        /// <param name="error">Set to a reason when the result is empty; null otherwise.</param>
        /// <returns>All matching GameObjects (0, 1, or many).</returns>
        public static IReadOnlyList<GameObject> ResolveAll(string target, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(target))
            {
                error = "target is required (a hierarchy path or an instance id).";
                return Array.Empty<GameObject>();
            }

            if (int.TryParse(target, out var instanceId))
            {
                if (EditorUtility.EntityIdToObject(instanceId) is GameObject byId)
                    return new[] { byId };
                error = $"no GameObject with instance id {instanceId}.";
                return Array.Empty<GameObject>();
            }

            var exact = EnumerateAll()
                .Where(go => GetHierarchyPath(go) == target)
                .Distinct().ToList();
            if (exact.Count > 0) return exact;

            var tolerant = FindTolerant(target);
            if (tolerant.Count > 0) return tolerant;

            error = $"no GameObject at hierarchy path '{target}'.";
            return Array.Empty<GameObject>();
        }

        /// <summary>
        /// Human/agent-readable ambiguity message listing each candidate as "path (id N)". The instance id is
        /// the only unique handle when names collide, so it is always included for deterministic re-selection.
        /// </summary>
        public static string AmbiguityMessage(string target, IReadOnlyList<GameObject> candidates)
        {
            var listed = string.Join(", ",
                candidates.Select(go => $"'{GetHierarchyPath(go)}' (id {go.GetInstanceID()})"));
            return $"'{target}' is ambiguous — {candidates.Count} objects match: {listed}. " +
                   "Ask the user which one(s) to act on, then re-issue targeting the chosen instance id(s).";
        }

        /// <summary>The '/'-separated hierarchy path of <paramref name="go"/> (root-to-leaf).</summary>
        public static string GetHierarchyPath(GameObject go)
        {
            if (go == null) return null;
            var sb = new StringBuilder(go.name);
            var t = go.transform;
            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }
            return sb.ToString();
        }

        /// <summary>All root GameObjects across the loaded scenes.</summary>
        public static IEnumerable<GameObject> EnumerateRoots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    yield return root;
            }
        }

        /// <summary>
        /// Every GameObject across the loaded scenes matching the optional filters: a name substring and/or
        /// an attached-component type-name substring, both case-insensitive. A null/blank filter is ignored,
        /// so passing neither returns every GameObject. Mirrors the <c>molca_unity_scene_objects</c> filter
        /// semantics so "select all with a MeshRenderer" is a single deterministic query rather than an
        /// enumerate-then-eyeball loop.
        /// </summary>
        /// <param name="nameContains">Case-insensitive substring the object name must contain, or null/blank for any.</param>
        /// <param name="componentType">Case-insensitive substring an attached component's type name must contain, or null/blank for any.</param>
        /// <returns>All matching GameObjects (possibly empty).</returns>
        public static List<GameObject> FindByFilters(string nameContains, string componentType)
        {
            var results = new List<GameObject>();
            foreach (var go in EnumerateAll())
            {
                if (!string.IsNullOrEmpty(nameContains) &&
                    go.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!string.IsNullOrEmpty(componentType) &&
                    !go.GetComponents<Component>().Any(c => c != null &&
                        c.GetType().Name.IndexOf(componentType, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                results.Add(go);
            }
            return results;
        }

        /// <summary>Renames <paramref name="go"/> as one undo group.</summary>
        public static bool Rename(GameObject go, string newName)
        {
            if (go == null || string.IsNullOrEmpty(newName)) return false;
            int group = Begin("Rename GameObject");
            Undo.RecordObject(go, "Rename GameObject");
            go.name = newName;
            EditorUtility.SetDirty(go);
            Collapse(group);
            return true;
        }

        /// <summary>Sets <paramref name="go"/>'s active-self state as one undo group.</summary>
        public static bool SetActive(GameObject go, bool active)
        {
            if (go == null) return false;
            int group = Begin("Set GameObject Active");
            Undo.RecordObject(go, "Set GameObject Active");
            go.SetActive(active);
            EditorUtility.SetDirty(go);
            Collapse(group);
            return true;
        }

        /// <summary>
        /// Sets any provided local transform components (position/euler rotation/scale) as one undo group.
        /// Null arguments are left unchanged.
        /// </summary>
        public static bool SetLocalTransform(GameObject go, Vector3? position, Vector3? eulerAngles, Vector3? scale)
        {
            if (go == null) return false;
            int group = Begin("Set Transform");
            Undo.RecordObject(go.transform, "Set Transform");
            if (position.HasValue) go.transform.localPosition = position.Value;
            if (eulerAngles.HasValue) go.transform.localEulerAngles = eulerAngles.Value;
            if (scale.HasValue) go.transform.localScale = scale.Value;
            EditorUtility.SetDirty(go.transform);
            Collapse(group);
            return true;
        }

        /// <summary>
        /// Creates a new GameObject (empty, or a <paramref name="primitive"/>) optionally parented under
        /// <paramref name="parent"/>, as one undo group.
        /// </summary>
        /// <returns>The created GameObject.</returns>
        public static GameObject Create(string name, GameObject parent, PrimitiveType? primitive)
        {
            int group = Begin("Create GameObject");
            var go = primitive.HasValue ? GameObject.CreatePrimitive(primitive.Value) : new GameObject();
            if (!string.IsNullOrEmpty(name)) go.name = name;
            else if (!primitive.HasValue) go.name = "GameObject";
            Undo.RegisterCreatedObjectUndo(go, "Create GameObject");
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Create GameObject");
            Collapse(group);
            return go;
        }

        /// <summary>Duplicates <paramref name="go"/> as one undo group, preserving its parent.</summary>
        public static GameObject Duplicate(GameObject go)
        {
            if (go == null) return null;
            int group = Begin("Duplicate GameObject");
            var duplicate = UnityEngine.Object.Instantiate(go, go.transform.parent);
            duplicate.name = ObjectNames.GetUniqueName(GetSiblingNames(go.transform.parent), go.name);
            Undo.RegisterCreatedObjectUndo(duplicate, "Duplicate GameObject");
            Collapse(group);
            return duplicate;
        }

        /// <summary>Reparents <paramref name="go"/> as one undo group.</summary>
        public static bool Reparent(GameObject go, GameObject newParent, bool worldPositionStays, out string error)
        {
            error = null;
            if (go == null) { error = "GameObject is null."; return false; }
            if (newParent != null && IsDescendantOf(newParent.transform, go.transform))
            {
                error = "cannot reparent an object under itself or one of its descendants.";
                return false;
            }

            var localPosition = go.transform.localPosition;
            var localRotation = go.transform.localRotation;
            var localScale = go.transform.localScale;

            int group = Begin("Reparent GameObject");
            Undo.SetTransformParent(go.transform, newParent != null ? newParent.transform : null, "Reparent GameObject");
            if (!worldPositionStays)
            {
                go.transform.localPosition = localPosition;
                go.transform.localRotation = localRotation;
                go.transform.localScale = localScale;
            }
            EditorUtility.SetDirty(go.transform);
            Collapse(group);
            return true;
        }

        /// <summary>Destroys <paramref name="go"/> as one undo group.</summary>
        public static bool Delete(GameObject go)
        {
            if (go == null) return false;
            int group = Begin("Delete GameObject");
            Undo.DestroyObjectImmediate(go);
            Collapse(group);
            return true;
        }

        /// <summary>Adds a component of <paramref name="type"/> to <paramref name="go"/> as one undo group.</summary>
        /// <param name="error">Set to a reason when the component cannot be added.</param>
        /// <returns>The added component, or null on failure.</returns>
        public static Component AddComponent(GameObject go, Type type, out string error)
        {
            error = null;
            if (go == null) { error = "GameObject is null."; return null; }
            if (type == null || !typeof(Component).IsAssignableFrom(type) || type.IsAbstract)
            {
                error = "type must be a concrete Component type.";
                return null;
            }
            int group = Begin("Add Component");
            Component component;
            try
            {
                component = Undo.AddComponent(go, type);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Collapse(group);
                return null;
            }
            Collapse(group);
            return component;
        }

        /// <summary>Removes <paramref name="component"/> as one undo group.</summary>
        public static bool RemoveComponent(Component component, out string error)
        {
            error = null;
            if (component == null) { error = "Component is null."; return false; }
            if (component is Transform)
            {
                error = "Transform cannot be removed.";
                return false;
            }

            int group = Begin("Remove Component");
            Undo.DestroyObjectImmediate(component);
            Collapse(group);
            return true;
        }

        /// <summary>Writes serialized component fields as one undo group.</summary>
        internal static StepFieldEditingService.SetFieldsResult SetComponentFields(
            Component component,
            IReadOnlyDictionary<string, FieldNode> fields)
        {
            var applied = new List<string>();
            var rejected = new List<KeyValuePair<string, string>>();
            if (component == null || fields == null)
                return new StepFieldEditingService.SetFieldsResult(applied, rejected);

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Set Component Fields");
            int group = Undo.GetCurrentGroup();

            var so = new SerializedObject(component);
            foreach (var pair in fields)
            {
                if (pair.Key == "m_Script")
                {
                    rejected.Add(new KeyValuePair<string, string>(pair.Key, "the script reference is read-only"));
                    continue;
                }

                var prop = so.FindProperty(pair.Key);
                if (prop == null)
                {
                    rejected.Add(new KeyValuePair<string, string>(pair.Key, "no such serialized field"));
                    continue;
                }

                if (!prop.editable)
                {
                    rejected.Add(new KeyValuePair<string, string>(pair.Key, "field is read-only"));
                    continue;
                }

                if (SerializedFieldCoercion.TrySet(prop, pair.Value, out var fieldError))
                    applied.Add(pair.Key);
                else
                    rejected.Add(new KeyValuePair<string, string>(pair.Key, fieldError));
            }

            if (applied.Count > 0) so.ApplyModifiedProperties();
            Collapse(group);
            return new StepFieldEditingService.SetFieldsResult(applied, rejected);
        }

        /// <summary>
        /// Case-insensitive resolution used only after an exact path miss (see <see cref="ResolveAll"/>).
        /// Matches, in order of preference: (1) the full '/'-separated path compared case-insensitively; failing that,
        /// (2) the target's last segment as a bare object name anywhere in the loaded scenes. Distinct
        /// GameObjects only — a single object reachable by both rules is returned once.
        /// </summary>
        /// <returns>All distinct case-insensitive matches (empty when none, &gt;1 when ambiguous).</returns>
        private static List<GameObject> FindTolerant(string target)
        {
            var all = EnumerateAll().ToList();

            var byPath = all
                .Where(go => string.Equals(GetHierarchyPath(go), target, StringComparison.OrdinalIgnoreCase))
                .Distinct().ToList();
            if (byPath.Count > 0) return byPath;

            var leaf = target.Split('/').Last();
            return all
                .Where(go => string.Equals(go.name, leaf, StringComparison.OrdinalIgnoreCase))
                .Distinct().ToList();
        }

        /// <summary>Every GameObject across the loaded scenes (roots and all descendants).</summary>
        private static IEnumerable<GameObject> EnumerateAll()
        {
            foreach (var root in EnumerateRoots())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    yield return t.gameObject;
        }

        private static string[] GetSiblingNames(Transform parent)
        {
            var names = new List<string>();
            if (parent == null)
            {
                foreach (var root in EnumerateRoots())
                    names.Add(root.name);
                return names.ToArray();
            }

            for (int i = 0; i < parent.childCount; i++)
                names.Add(parent.GetChild(i).name);
            return names.ToArray();
        }

        private static bool IsDescendantOf(Transform candidate, Transform ancestor)
        {
            var current = candidate;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = current.parent;
            }
            return false;
        }

        private static int Begin(string name)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return Undo.GetCurrentGroup();
        }

        private static void Collapse(int group) => Undo.CollapseUndoOperations(group);
    }
}
