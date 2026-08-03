using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Molca.Editor.Migration
{
    /// <summary>What happened when a retired component was removed.</summary>
    public sealed class LegacyComponentRemovalResult
    {
        /// <summary>How many components were removed.</summary>
        public int Removed { get; }

        /// <summary>Assets that were written.</summary>
        public IReadOnlyList<string> WrittenAssets { get; }

        /// <summary>One message per component that could not be removed.</summary>
        public IReadOnlyList<string> Failures { get; }

        /// <summary>Creates a result.</summary>
        public LegacyComponentRemovalResult(int removed, IReadOnlyList<string> written,
            IReadOnlyList<string> failures)
        {
            Removed = removed;
            WrittenAssets = written ?? Array.Empty<string>();
            Failures = failures ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Removes components of a retired type, whether or not its script still resolves.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Migration/</c>.
    /// <b>Shape:</b> editor-only static service, the write half of <see cref="LegacyComponentIndex"/>.
    /// <para/>
    /// <b>Two worlds, one remover.</b> A migration ships in the release that deletes the type, so it must
    /// run in the consumer's project, where the class is gone and the component is a missing script, and
    /// in the developing project, where the class is still compiled and the component resolves normally.
    /// A live one is destroyed directly, matched by its script GUID; only what is left missing goes down
    /// the other path. Without this the migration could not be tested before the deletion it exists for.
    /// <para/>
    /// <b>The missing-script path is blunt and made precise by counting.</b>
    /// <c>GameObjectUtility.RemoveMonoBehavioursWithMissingScript</c> removes *every* missing script on an
    /// object; on a consumer's project that would also destroy components broken for unrelated reasons —
    /// a script they are mid-rename, a package temporarily removed — and report it as success. Comparing
    /// the object's missing count against the number this migration accounted for closes that: equal means
    /// the blunt call is exactly precise, greater means something foreign shares the object and nothing is
    /// removed.
    /// <para/>
    /// <b>Deleting the <c>m_Component</c> slot does not work</b>, which was the first design. Unity
    /// rebuilds that array from the object's real component list on serialization, so the edit applies,
    /// every API reports success, and the file is unchanged — caught only by re-scanning afterwards. The
    /// index <see cref="LegacyComponentIndex"/> records is still what supplies the per-object accounting
    /// above.
    /// </remarks>
    public static class LegacyComponentRemover
    {
        /// <summary>Removes the given components.</summary>
        /// <param name="records">Components to remove, as recorded by <see cref="LegacyComponentIndex"/>.</param>
        /// <returns>What was removed.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="records"/> is <c>null</c>.</exception>
        public static LegacyComponentRemovalResult Remove(IReadOnlyList<LegacyComponentRecord> records,
            string scriptGuid)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));

            int removed = 0;
            var written = new List<string>();
            var failures = new List<string>();

            foreach (var group in records.GroupBy(r => r.AssetPath)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int count = group.Key.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                    ? RemoveFromScene(group.Key, group.ToList(), scriptGuid, failures)
                    : RemoveFromPrefab(group.Key, group.ToList(), scriptGuid, failures);

                if (count <= 0) continue;

                removed += count;
                written.Add(group.Key);
            }

            if (written.Count > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            return new LegacyComponentRemovalResult(removed, written, failures);
        }

        /// <summary>Removes from a prefab, through a loaded working copy.</summary>
        /// <remarks>
        /// Removing a component is a <i>structural</i> change, and a prefab asset will not take one
        /// through <c>SerializedObject</c> on the loaded asset: the edit applies to the in-memory object,
        /// <c>SaveAssetIfDirty</c> reports success, and the file is unchanged. `LoadPrefabContents` plus
        /// `SaveAsPrefabAsset` is the path Unity sanctions for this, and the one
        /// <c>ColorContentMigration</c> already uses for its own source phase.
        /// <para/>
        /// This does not contradict the rule that instance overrides are never written through
        /// `SaveAsPrefabAsset` — that rule governs the <i>instance-override phase</i>, where re-serializing
        /// a whole asset from a materialized copy once baked a missing-asset magenta into real content.
        /// Removing a source component is the other phase, and a test pins which files are held to the
        /// instance rule.
        /// <para/>
        /// The working copy's objects are scene objects with no persistent ids, so identity is bridged by
        /// hierarchy path: the asset representation supplies path-per-file-id, and the copy is addressed by
        /// path.
        /// </remarks>
        private static int RemoveFromPrefab(string assetPath, IReadOnlyList<LegacyComponentRecord> records,
            string scriptGuid, List<string> failures)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
            {
                failures.Add($"{assetPath}: could not be loaded as a prefab");
                return 0;
            }

            var pathByFileId = HierarchyPathsByFileId(asset);

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                var byPath = root.GetComponentsInChildren<Transform>(true)
                    .GroupBy(HierarchyPath)
                    .ToDictionary(g => g.Key, g => g.First().gameObject);

                int removed = 0;

                foreach (var group in records.GroupBy(r => r.GameObjectFileId))
                {
                    if (!pathByFileId.TryGetValue(group.Key, out string path)
                        || !byPath.TryGetValue(path, out var owner))
                    {
                        failures.Add($"{assetPath}: the GameObject recorded as file id {group.Key} could "
                                     + "not be located, so its retired component was not removed");
                        continue;
                    }

                    removed += RemoveRetiredScripts(owner, scriptGuid, group.Count(), assetPath, failures);
                }

                if (removed > 0) PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                return removed;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>Hierarchy path for every GameObject in a prefab asset, keyed by local file id.</summary>
        private static Dictionary<long, string> HierarchyPathsByFileId(GameObject asset)
        {
            var paths = new Dictionary<long, string>();

            foreach (var transform in asset.GetComponentsInChildren<Transform>(true))
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(transform.gameObject, out _,
                        out long fileId) && fileId != 0)
                {
                    paths[fileId] = HierarchyPath(transform);
                }
            }

            return paths;
        }

        /// <summary>An unambiguous locator, including sibling index so duplicate names cannot collide.</summary>
        private static string HierarchyPath(Transform transform)
        {
            var parts = new List<string>();
            for (var current = transform; current != null; current = current.parent)
                parts.Add($"{current.name}[{current.GetSiblingIndex()}]");
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static int RemoveFromScene(string assetPath, IReadOnlyList<LegacyComponentRecord> records,
            string scriptGuid, List<string> failures)
        {
            var scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
            try
            {
                int removed = RemoveFrom(scene.GetRootGameObjects(), assetPath, records, scriptGuid, failures);
                if (removed > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }

                return removed;
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        /// <summary>
        /// Removes from an open scene, where objects still carry the ids the file recorded.
        /// </summary>
        /// <remarks>
        /// A scene needs no working copy: <c>EditorSceneManager.OpenScene</c> gives the real objects, and
        /// <see cref="GlobalObjectId"/> resolves their local ids — so identity is direct here rather than
        /// bridged by hierarchy path as it must be for a prefab.
        /// </remarks>
        private static int RemoveFrom(IReadOnlyList<GameObject> roots, string assetPath,
            IReadOnlyList<LegacyComponentRecord> records, string scriptGuid, List<string> failures)
        {
            var byFileId = GameObjectsByFileId(roots);
            int removed = 0;

            // Descending, so removing a component does not shift the index of one still to be removed on
            // the same object.
            foreach (var group in records.GroupBy(r => r.GameObjectFileId))
            {
                if (!byFileId.TryGetValue(group.Key, out var owner))
                {
                    failures.Add($"{assetPath}: the GameObject recorded as file id {group.Key} is no "
                                 + "longer present, so its retired component was not removed");
                    continue;
                }

                removed += RemoveRetiredScripts(owner, scriptGuid, group.Count(), assetPath, failures);
            }

            return removed;
        }

        /// <summary>
        /// Removes the retired scripts from one GameObject, but only when every missing script on it is
        /// one this migration is accounting for.
        /// </summary>
        /// <param name="owner">The object to clean.</param>
        /// <param name="expected">How many retired components the scan recorded on it.</param>
        /// <param name="assetPath">The containing asset, for messages.</param>
        /// <param name="failures">Collects refusals.</param>
        /// <returns>How many were removed.</returns>
        /// <remarks>
        /// <b>Why not delete the <c>m_Component</c> slot directly.</b> That was the first design, and it
        /// does not work: <c>m_Component</c> is rebuilt from the object's real component list on
        /// serialization, so the edit applies to the SerializedObject, every API reports success, and the
        /// file is unchanged. Measured, not assumed — it passed its own assertions and failed a re-scan.
        /// <para/>
        /// <b>Why the count check makes the blunt API safe.</b>
        /// <see cref="GameObjectUtility.RemoveMonoBehavioursWithMissingScript"/> removes *every* missing
        /// script on the object, which on a consumer's project could destroy components broken for
        /// unrelated reasons — a script mid-rename, a package temporarily removed. Comparing the object's
        /// missing-script count against the number this migration recorded closes that: equal means every
        /// missing script here is one we are migrating, and the blunt call is exactly precise. More means
        /// something foreign shares the object, and we refuse and say so rather than take it with us.
        /// </remarks>
        internal static int RemoveRetiredScripts(GameObject owner, string scriptGuid, int expected,
            string assetPath, List<string> failures)
        {
            // The type may still exist. A migration ships in the release that deletes it, so it runs
            // against both worlds: the consumer's, where the class is gone and the component is a missing
            // script, and the developing project's, where the class is still compiled and the component
            // resolves normally. A live one is destroyed directly, which is both simpler and more precise
            // than anything the missing-script path can do.
            int destroyed = 0;
            foreach (var behaviour in owner.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null) continue;
                if (!string.Equals(ScriptGuidOf(behaviour), scriptGuid, StringComparison.OrdinalIgnoreCase))
                    continue;

                UnityEngine.Object.DestroyImmediate(behaviour, true);
                destroyed++;
            }

            int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(owner);

            if (missing == 0) return destroyed;
            if (destroyed > 0) expected -= destroyed;

            if (expected <= 0)
            {
                // Every component this migration accounted for was live and has been destroyed; whatever
                // is still missing belongs to something else.
                return destroyed;
            }

            if (missing > expected)
            {
                failures.Add($"{assetPath}: '{owner.name}' has {missing} missing script(s) but only "
                             + $"{expected} belong to this migration. The others are not ours to delete, "
                             + "so nothing was removed here — resolve them and re-run.");
                return 0;
            }

            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(owner);
            return destroyed + missing;
        }

        /// <summary>The script GUID behind a live MonoBehaviour, or <c>null</c>.</summary>
        private static string ScriptGuidOf(MonoBehaviour behaviour)
        {
            var script = MonoScript.FromMonoBehaviour(behaviour);
            if (script == null) return null;

            string path = AssetDatabase.GetAssetPath(script);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
        }

        /// <summary>Every GameObject in the given hierarchies, keyed by its local file id.</summary>
        /// <remarks>
        /// Scene objects are not persistent, so
        /// <see cref="AssetDatabase.TryGetGUIDAndLocalFileIdentifier(UnityEngine.Object, out string, out long)"/>
        /// answers only inside a prefab asset; <see cref="GlobalObjectId"/> covers both. Same split that
        /// left <see cref="PrefabInstanceOverrideWriter"/> unable to see scene instances at all.
        /// </remarks>
        private static Dictionary<long, GameObject> GameObjectsByFileId(IReadOnlyList<GameObject> roots)
        {
            var byFileId = new Dictionary<long, GameObject>();

            foreach (var root in roots)
            {
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    var candidate = transform.gameObject;

                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out _, out long fileId)
                        || fileId == 0)
                    {
                        fileId = (long)GlobalObjectId.GetGlobalObjectIdSlow(candidate).targetObjectId;
                    }

                    if (fileId != 0 && !byFileId.ContainsKey(fileId)) byFileId[fileId] = candidate;
                }
            }

            return byFileId;
        }
    }
}
