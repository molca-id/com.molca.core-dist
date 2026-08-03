using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Molca.Editor.Migration
{
    /// <summary>One instance's overrides, rewritten from the old schema onto the new one.</summary>
    public sealed class PrefabInstanceRewrite
    {
        /// <summary>The prefab or scene holding the instance.</summary>
        public string ContainingAssetPath { get; }

        /// <summary>The <c>PrefabInstance</c>'s local file id, as recorded when the plan was built.</summary>
        public long InstanceFileId { get; }

        /// <summary>
        /// Modifications to write, as <c>(target, propertyPath, value)</c>.
        /// </summary>
        /// <remarks>
        /// <paramref name="Target"/> is the object in the <i>source</i> asset that the modification names,
        /// which is how Unity addresses an override. It must already exist — the migration that created it
        /// has to have run first.
        /// </remarks>
        public IReadOnlyList<(UnityEngine.Object Target, string PropertyPath, string Value)> Set { get; }

        /// <summary>Decides which existing modifications are dropped. <c>null</c> drops none.</summary>
        public Func<PropertyModification, bool> Remove { get; }

        /// <summary>Creates a rewrite.</summary>
        /// <param name="containingAssetPath">The prefab or scene holding the instance.</param>
        /// <param name="instanceFileId">The instance's local file id.</param>
        /// <param name="set">Modifications to write.</param>
        /// <param name="remove">Which existing modifications to drop.</param>
        public PrefabInstanceRewrite(string containingAssetPath, long instanceFileId,
            IReadOnlyList<(UnityEngine.Object Target, string PropertyPath, string Value)> set,
            Func<PropertyModification, bool> remove = null)
        {
            ContainingAssetPath = containingAssetPath;
            InstanceFileId = instanceFileId;
            Set = set ?? Array.Empty<(UnityEngine.Object, string, string)>();
            Remove = remove;
        }
    }

    /// <summary>
    /// The one place a schema migration rewrites a prefab instance's overrides.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Migration/</c>.
    /// <b>Shape:</b> editor-only static service, the write half of
    /// <see cref="PrefabInstanceOverrideIndex"/>.
    /// <para/>
    /// <b>Only <see cref="PrefabUtility.GetPropertyModifications"/> and
    /// <see cref="PrefabUtility.SetPropertyModifications"/>.</b> Those speak in the same terms the file
    /// does — a target, a property path, a value — so a change touches the modification list and nothing
    /// else. The alternative, loading the containing prefab's contents and writing it back with
    /// <c>SaveAsPrefabAsset</c>, re-serializes the whole asset from a materialized copy; doing that in
    /// this repository once rewrote an unrelated colour override to Unity's missing-asset magenta and
    /// baked it in as a real value. That is why prefabs here are edited through the asset representation
    /// rather than through loaded contents, and why the rule is enforced by test rather than by memory.
    /// <para/>
    /// <b>Instances are found by file id, not by object.</b> By the time this runs the source has already
    /// been migrated, so the components an override used to name may be gone; the id recorded when the
    /// plan was built is the only handle that survives that.
    /// </remarks>
    public static class PrefabInstanceOverrideWriter
    {
        /// <summary>Applies every rewrite, grouped so each containing asset is opened once.</summary>
        /// <param name="rewrites">The rewrites to apply, in any order.</param>
        /// <param name="applied">How many instances were rewritten.</param>
        /// <param name="failures">One message per instance that could not be.</param>
        /// <returns>The containing assets that were written.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="rewrites"/> is <c>null</c>.</exception>
        public static IReadOnlyList<string> Apply(IReadOnlyList<PrefabInstanceRewrite> rewrites,
            out int applied, out List<string> failures)
        {
            if (rewrites == null) throw new ArgumentNullException(nameof(rewrites));

            applied = 0;
            failures = new List<string>();
            var written = new List<string>();

            foreach (var group in rewrites
                         .GroupBy(r => r.ContainingAssetPath)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                try
                {
                    int count = group.Key.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                        ? ApplyToScene(group.Key, group.ToList(), failures)
                        : ApplyToPrefab(group.Key, group.ToList(), failures);

                    if (count <= 0) continue;

                    applied += count;
                    written.Add(group.Key);
                }
                catch (Exception exception)
                {
                    failures.Add($"{group.Key}: {exception.Message}");
                }
            }

            return written;
        }

        private static int ApplyToPrefab(string containingAssetPath,
            IReadOnlyList<PrefabInstanceRewrite> rewrites, List<string> failures)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(containingAssetPath);
            if (root == null)
            {
                failures.Add($"{containingAssetPath}: could not be loaded as a prefab");
                return 0;
            }

            int applied = ApplyTo(new[] { root }, containingAssetPath, rewrites, failures);
            if (applied > 0)
            {
                EditorUtility.SetDirty(root);
                AssetDatabase.SaveAssetIfDirty(root);
            }

            return applied;
        }

        private static int ApplyToScene(string containingAssetPath,
            IReadOnlyList<PrefabInstanceRewrite> rewrites, List<string> failures)
        {
            var scene = EditorSceneManager.OpenScene(containingAssetPath, OpenSceneMode.Additive);
            try
            {
                int applied = ApplyTo(scene.GetRootGameObjects(), containingAssetPath, rewrites, failures);
                if (applied > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }

                return applied;
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static int ApplyTo(IReadOnlyList<GameObject> roots, string containingAssetPath,
            IReadOnlyList<PrefabInstanceRewrite> rewrites, List<string> failures)
        {
            var byFileId = InstanceRootsByFileId(roots);
            int applied = 0;

            foreach (var rewrite in rewrites)
            {
                if (!byFileId.TryGetValue(rewrite.InstanceFileId, out var instanceRoot))
                {
                    failures.Add($"{containingAssetPath}: the prefab instance recorded as file id "
                                 + $"{rewrite.InstanceFileId} is no longer present, so its overrides were "
                                 + "not carried");
                    continue;
                }

                if (Rewrite(instanceRoot, rewrite)) applied++;
            }

            return applied;
        }

        private static bool Rewrite(GameObject instanceRoot, PrefabInstanceRewrite rewrite)
        {
            var modifications = PrefabUtility.GetPropertyModifications(instanceRoot)?.ToList()
                                ?? new List<PropertyModification>();

            if (rewrite.Remove != null)
                modifications.RemoveAll(m => m != null && rewrite.Remove(m));

            bool changed = false;
            foreach (var (target, propertyPath, value) in rewrite.Set)
            {
                if (target == null || string.IsNullOrEmpty(propertyPath)) continue;

                // Replaced rather than appended: two modifications naming the same (target, path) is a
                // state Unity does not define a winner for.
                modifications.RemoveAll(m =>
                    m != null && ReferenceEquals(m.target, target)
                    && string.Equals(m.propertyPath, propertyPath, StringComparison.Ordinal));

                modifications.Add(new PropertyModification
                {
                    target = target,
                    propertyPath = propertyPath,
                    value = value,
                    objectReference = null,
                });

                changed = true;
            }

            if (!changed && rewrite.Remove == null) return false;

            PrefabUtility.SetPropertyModifications(instanceRoot, modifications.ToArray());
            return true;
        }

        /// <summary>Every prefab-instance root in the given hierarchies, keyed by its local file id.</summary>
        /// <remarks>
        /// <b>Two id sources, because one does not cover scenes.</b>
        /// <see cref="AssetDatabase.TryGetGUIDAndLocalFileIdentifier(UnityEngine.Object, out string, out long)"/>
        /// answers only for persistent objects, so for a prefab instance living in a *scene* it returns
        /// <c>false</c> with an id of 0 — every scene instance then failed to match and its overrides were
        /// reported as "no longer present" while the file sat there untouched. <see cref="GlobalObjectId"/>
        /// returns the same local id the scene file records, so it is the fallback. The asset path is tried
        /// first because it is the one already proven against prefab containers.
        /// <para/>
        /// <b>First match wins.</b> Nested instance roots inside one outer instance all resolve to the
        /// outer instance's id, and it is the outer one the modification list belongs to. Depth-first
        /// traversal reaches it first.
        /// </remarks>
        private static Dictionary<long, GameObject> InstanceRootsByFileId(IReadOnlyList<GameObject> roots)
        {
            var byFileId = new Dictionary<long, GameObject>();

            foreach (var root in roots)
            {
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    var candidate = transform.gameObject;
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(candidate)) continue;

                    var handle = PrefabUtility.GetPrefabInstanceHandle(candidate);
                    if (handle == null) continue;

                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(handle, out _, out long fileId)
                        || fileId == 0)
                    {
                        fileId = (long)GlobalObjectId.GetGlobalObjectIdSlow(handle).targetObjectId;
                    }

                    if (fileId != 0 && !byFileId.ContainsKey(fileId)) byFileId[fileId] = candidate;
                }
            }

            return byFileId;
        }
    }
}
