using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Tabular
{
    /// <summary>
    /// Applies a <see cref="TabularBindingSpec"/> onto scene GameObjects, their components, and project
    /// assets at edit time. <see cref="Plan"/> resolves and coerces every binding <em>without</em> mutating
    /// (for a reviewable diff); <see cref="Apply"/> performs the same work as one Unity Undo group (a single
    /// Ctrl+Z reverts the whole batch). Field coercion — including setting <c>SceneObjectReference</c> fields
    /// by Ref Id — is delegated to <see cref="SerializedFieldCoercion"/>, so behavior matches the
    /// <c>molca_unity_component_set_fields</c> tool exactly.
    /// </summary>
    /// <remarks>
    /// Editor-time only: <see cref="Apply"/> writes to ScriptableObject assets and scene objects and refuses
    /// to run in Play mode (SO writes are edit-time-legal, runtime-illegal). A per-row resolution failure or
    /// a per-cell coercion failure is recorded in <see cref="BindingResult.Rejected"/> and never aborts the
    /// batch — good rows still apply. Not thread-safe; main thread only.
    /// </remarks>
    internal static class TabularBindingService
    {
        private const string UndoGroupName = "Apply Sheet Values";

        /// <summary>Resolves and coerces every binding without mutating anything; returns the would-be diff.</summary>
        internal static BindingResult Plan(TabularBindingSpec spec) => Run(spec, apply: false);

        /// <summary>Applies every resolvable binding as one Undo group; returns the applied diff.</summary>
        internal static BindingResult Apply(TabularBindingSpec spec) => Run(spec, apply: true);

        private static BindingResult Run(TabularBindingSpec spec, bool apply)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            if (apply && Application.isPlaying)
                throw new InvalidOperationException(
                    "TabularBindingService.Apply is edit-time only (it writes assets and scene objects); " +
                    "it must not run in Play mode.");

            var result = new BindingResult();

            int group = 0;
            if (apply)
            {
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName(UndoGroupName);
                group = Undo.GetCurrentGroup();
            }

            bool touchedAssets = false;

            foreach (var row in spec.Rows)
            {
                string key = row != null && row.TryGetValue(spec.KeyColumn, out var k) ? k : null;
                string rowKey = string.IsNullOrEmpty(key) ? "(no key)" : key;

                if (string.IsNullOrEmpty(key))
                {
                    foreach (var b in spec.Bindings)
                        result.Rejected.Add(new BindingReject(rowKey, "", b.Target,
                            $"key column '{spec.KeyColumn}' is missing or empty"));
                    continue;
                }

                var target = ResolveTarget(spec.Selector, key, out var targetDesc, out var resolveError);
                if (target == null)
                {
                    foreach (var b in spec.Bindings)
                        result.Rejected.Add(new BindingReject(rowKey, targetDesc, b.Target, resolveError));
                    continue;
                }

                foreach (var b in spec.Bindings)
                {
                    if (row == null || !row.TryGetValue(b.Column, out var cell))
                    {
                        result.Rejected.Add(new BindingReject(rowKey, targetDesc, b.Target,
                            $"column '{b.Column}' is not present in this row"));
                        continue;
                    }

                    ApplyBinding(target, targetDesc, rowKey, b, cell, apply, result, ref touchedAssets);
                }
            }

            if (apply)
            {
                Undo.CollapseUndoOperations(group);
                if (touchedAssets) AssetDatabase.SaveAssets();
            }

            return result;
        }

        /// <summary>
        /// A resolved binding target: a GameObject (scene selectors) or a project asset (assetPath selector).
        /// </summary>
        private readonly struct ResolvedTarget
        {
            public readonly GameObject GameObject;
            public readonly UnityEngine.Object Asset;
            public bool IsAsset => Asset != null;
            public ResolvedTarget(GameObject go, UnityEngine.Object asset) { GameObject = go; Asset = asset; }
        }

        private static ResolvedTarget? ResolveTarget(
            TargetSelectorKind selector, string key, out string description, out string error)
        {
            description = key;
            error = null;

            switch (selector)
            {
                case TargetSelectorKind.RefId:
                {
                    var matches = FindReferenceablesByRefId(key);
                    if (matches.Count == 0) { error = $"no IReferenceable with Ref Id '{key}' in the loaded scene(s)"; return null; }
                    if (matches.Count > 1) { error = $"Ref Id '{key}' is ambiguous — {matches.Count} components share it"; return null; }
                    var go = matches[0].gameObject;
                    description = GameObjectEditingService.GetHierarchyPath(go);
                    return new ResolvedTarget(go, null);
                }
                case TargetSelectorKind.Scene:
                {
                    var go = GameObjectEditingService.Resolve(key, out error);
                    if (go == null) return null;
                    description = GameObjectEditingService.GetHierarchyPath(go);
                    return new ResolvedTarget(go, null);
                }
                case TargetSelectorKind.AssetPath:
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(key);
                    if (asset == null) { error = $"no asset at path '{key}'"; return null; }
                    description = key;
                    return new ResolvedTarget(null, asset);
                }
                default:
                    error = "unknown target selector";
                    return null;
            }
        }

        private static void ApplyBinding(
            ResolvedTarget? targetNullable, string targetDesc, string rowKey, TabularBindingField b,
            string cell, bool apply, BindingResult result, ref bool touchedAssets)
        {
            var target = targetNullable.Value;

            if (target.IsAsset)
            {
                if (string.Equals(b.Target, "name", StringComparison.OrdinalIgnoreCase))
                {
                    result.Rejected.Add(new BindingReject(rowKey, targetDesc, b.Target,
                        "renaming assets is not supported; target a serialized field instead"));
                    return;
                }
                if (WriteSerialized(target.Asset, b.Target, cell, apply, out var oldA, out var newA, out var errA))
                {
                    if (apply) touchedAssets = true;
                    result.Applied.Add(new BindingChange(rowKey, targetDesc, b.Target, oldA, newA));
                }
                else result.Rejected.Add(new BindingReject(rowKey, targetDesc, b.Target, errA));
                return;
            }

            // Scene target.
            var go = target.GameObject;

            if (string.Equals(b.Target, "name", StringComparison.OrdinalIgnoreCase))
            {
                var oldName = go.name;
                if (apply)
                {
                    Undo.RecordObject(go, UndoGroupName);
                    go.name = cell;
                    EditorUtility.SetDirty(go);
                }
                result.Applied.Add(new BindingChange(rowKey, targetDesc, b.Target, oldName, cell));
                return;
            }

            int slash = b.Target.IndexOf('/');
            if (slash <= 0 || slash >= b.Target.Length - 1)
            {
                result.Rejected.Add(new BindingReject(rowKey, targetDesc, b.Target,
                    "scene-target field must be 'name' or 'ComponentType/fieldPath'"));
                return;
            }

            var typeName = b.Target.Substring(0, slash);
            var fieldPath = b.Target.Substring(slash + 1);

            var component = go.GetComponents<Component>().FirstOrDefault(c =>
                c != null && (
                    string.Equals(c.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.GetType().FullName, typeName, StringComparison.OrdinalIgnoreCase)));
            if (component == null)
            {
                result.Rejected.Add(new BindingReject(rowKey, targetDesc, b.Target,
                    $"no component '{typeName}' on the resolved GameObject"));
                return;
            }

            if (WriteSerialized(component, fieldPath, cell, apply, out var oldV, out var newV, out var err))
                result.Applied.Add(new BindingChange(rowKey, targetDesc, b.Target, oldV, newV));
            else
                result.Rejected.Add(new BindingReject(rowKey, targetDesc, b.Target, err));
        }

        /// <summary>
        /// Coerces <paramref name="cell"/> into serialized field <paramref name="fieldPath"/> on
        /// <paramref name="obj"/>. In dry-run (<paramref name="apply"/> false) the value is written into the
        /// <see cref="SerializedObject"/> buffer and read back for the diff, but never applied — so the target
        /// stays untouched. On apply, <c>ApplyModifiedProperties</c> registers the change on the current Undo
        /// group.
        /// </summary>
        private static bool WriteSerialized(
            UnityEngine.Object obj, string fieldPath, string cell, bool apply,
            out string oldValue, out string newValue, out string error)
        {
            oldValue = newValue = string.Empty;
            error = null;

            if (fieldPath == "m_Script")
            {
                error = "the script reference is read-only";
                return false;
            }

            var so = new SerializedObject(obj);
            var prop = so.FindProperty(fieldPath);
            if (prop == null) { error = $"no such serialized field '{fieldPath}'"; return false; }
            if (!prop.editable) { error = "field is read-only"; return false; }

            oldValue = SerializedFieldCoercion.ReadValue(prop);
            if (!SerializedFieldCoercion.TrySet(prop, cell, out error))
                return false;
            newValue = SerializedFieldCoercion.ReadValue(prop);

            if (apply)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(obj);
            }
            return true;
        }

        /// <summary>
        /// Live <see cref="IReferenceable"/> components in the loaded scene(s) whose Ref Id equals
        /// <paramref name="refId"/>. Edit-time resolution scans loaded objects directly because the runtime
        /// <c>ReferenceManager</c> registry is empty outside Play mode.
        /// </summary>
        private static List<MonoBehaviour> FindReferenceablesByRefId(string refId)
        {
            var matches = new List<MonoBehaviour>();
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null || mb is not IReferenceable r) continue;
                string id;
                try { id = r.RefId; } catch { continue; }
                if (string.Equals(id, refId, StringComparison.Ordinal))
                    matches.Add(mb);
            }
            return matches;
        }
    }
}
