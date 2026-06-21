using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor
{
    /// <summary>
    /// Pre-build gate (R3.5): runs a <b>fresh</b> scan of the enabled build scenes and reports
    /// missing, duplicate, or unresolvable scene-MonoBehaviour Ref Ids. <see cref="BuildManager"/>
    /// calls <see cref="Validate"/> before <c>BuildPipeline.BuildPlayer</c> and aborts the build
    /// when any error is returned.
    /// </summary>
    /// <remarks>
    /// This deliberately does not trust the cached <see cref="ReferenceManagerSettings"/> snapshot —
    /// it opens each enabled build scene and scans live components. Asset (ScriptableObject) ids are
    /// excluded from the resolvability set per the SOs-out boundary: only loaded scene MonoBehaviours
    /// resolve through <see cref="ReferenceManager"/> at runtime.
    /// </remarks>
    public static class SceneReferenceBuildValidator
    {
        /// <summary>A referenceable scene component discovered during the scan.</summary>
        public readonly struct ReferenceEntry
        {
            public readonly string Scene;
            public readonly string Owner;
            public readonly string RefType;
            public readonly string RefId;

            public ReferenceEntry(string scene, string owner, string refType, string refId)
            {
                Scene = scene;
                Owner = owner;
                RefType = refType;
                RefId = refId;
            }
        }

        /// <summary>An assigned <see cref="SceneObjectReference"/> field discovered during the scan.</summary>
        public readonly struct ResolveSite
        {
            public readonly string Scene;
            public readonly string Owner;
            public readonly string PropertyPath;
            public readonly string RefId;
            public readonly string RefType;

            public ResolveSite(string scene, string owner, string propertyPath, string refId, string refType)
            {
                Scene = scene;
                Owner = owner;
                PropertyPath = propertyPath;
                RefId = refId;
                RefType = refType;
            }
        }

        /// <summary>
        /// Pure analysis of scanned referenceables and resolve sites — no scene IO, so it is unit
        /// testable. Reports missing ids, duplicate (type+id) ids, and resolve sites whose RefId is
        /// not provided by any scanned scene MonoBehaviour. Asset ids never enter
        /// <paramref name="referenceables"/>, so they are excluded from resolvability (SOs-out).
        /// </summary>
        public static List<string> Analyze(IReadOnlyList<ReferenceEntry> referenceables, IReadOnlyList<ResolveSite> sites)
        {
            var errors = new List<string>();
            var ownerByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            var knownIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var e in referenceables)
            {
                if (string.IsNullOrEmpty(e.RefId))
                {
                    errors.Add($"Missing Ref Id on '{e.Owner}' (scene '{e.Scene}', type \"{e.RefType}\").");
                    continue;
                }

                var key = (e.RefType ?? string.Empty) + "|" + e.RefId;
                if (ownerByKey.TryGetValue(key, out var existingOwner))
                {
                    errors.Add(
                        $"Duplicate Ref Id \"{e.RefId}\" (type \"{e.RefType}\") on '{e.Owner}' (scene '{e.Scene}'); " +
                        $"already used by '{existingOwner}'.");
                    continue;
                }

                ownerByKey[key] = $"{e.Owner} in '{e.Scene}'";
                knownIds.Add(e.RefId);
            }

            foreach (var site in sites)
            {
                if (!knownIds.Contains(site.RefId))
                {
                    errors.Add(
                        $"Unresolvable SceneObjectReference '{site.PropertyPath}' on '{site.Owner}' (scene '{site.Scene}') " +
                        $"points at Ref Id \"{site.RefId}\" (type \"{site.RefType}\") that no scene MonoBehaviour in the build provides.");
                }
            }

            return errors;
        }

        /// <summary>
        /// Scans the enabled build scenes and returns one message per problem found.
        /// An empty list means the scene references are build-safe.
        /// </summary>
        /// <remarks>
        /// Fails open on its own internal errors (logs a warning, returns no errors) so a validator
        /// bug cannot brick every build; genuine reference problems still abort the build.
        /// </remarks>
        public static List<string> Validate()
        {
            var errors = new List<string>();

            var buildScenes = EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .Distinct()
                .ToList();

            if (buildScenes.Count == 0)
                return errors;

            // Preserve the user's open scenes; we must save before single-opening build scenes.
            if (!TryBuildOpenScenesSnapshot(out var snapshotPaths, out var activeScenePath))
            {
                Debug.LogWarning("[SceneReferenceBuildValidator] Skipped: open scenes have unsaved/in-memory state that cannot be restored. Save scenes and retry.");
                return errors;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.LogWarning("[SceneReferenceBuildValidator] Skipped: saving open scenes was cancelled.");
                return errors;
            }

            var referenceables = new List<ReferenceEntry>();
            var resolveSites = new List<ResolveSite>();

            try
            {
                foreach (var scenePath in buildScenes)
                {
                    Scene scene;
                    try
                    {
                        scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    }
                    catch (Exception e)
                    {
                        errors.Add($"Could not open build scene '{scenePath}': {e.Message}");
                        continue;
                    }

                    var sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);

                    foreach (var root in scene.GetRootGameObjects())
                    {
                        foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (mb == null)
                                continue;

                            if (mb is IReferenceable referenceable)
                            {
                                referenceables.Add(new ReferenceEntry(
                                    sceneName, $"{mb.gameObject.name} ({mb.GetType().Name})",
                                    referenceable.RefType ?? string.Empty, referenceable.RefId));
                            }

                            CollectResolveSites(mb, sceneName, resolveSites);
                        }
                    }
                }
            }
            finally
            {
                RestoreEditorOpenScenes(snapshotPaths, activeScenePath);
            }

            errors.AddRange(Analyze(referenceables, resolveSites));
            return errors;
        }

        private static void CollectResolveSites(MonoBehaviour mb, string sceneName, List<ResolveSite> sites)
        {
            var serialized = new SerializedObject(mb);
            var property = serialized.GetIterator();
            bool enterChildren = true;
            while (property.Next(enterChildren))
            {
                enterChildren = true;
                if (property.propertyType != SerializedPropertyType.Generic)
                    continue;

                var refIdProp = property.FindPropertyRelative("refId");
                var refTypeProp = property.FindPropertyRelative("refType");
                if (refIdProp == null || refTypeProp == null || refIdProp.propertyType != SerializedPropertyType.String)
                    continue;

                enterChildren = false; // a SceneObjectReference — don't descend into it
                var id = refIdProp.stringValue;
                if (string.IsNullOrEmpty(id))
                    continue; // unset references are legal

                sites.Add(new ResolveSite(sceneName, $"{mb.gameObject.name} ({mb.GetType().Name})",
                    property.propertyPath, id, refTypeProp.stringValue ?? string.Empty));
            }
        }

        /// <summary>True when every loaded editor scene has a saved asset path (snapshot can be restored).</summary>
        private static bool TryBuildOpenScenesSnapshot(out List<string> pathsInOrder, out string activeScenePath)
        {
            pathsInOrder = new List<string>();
            activeScenePath = null;

            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var sc = EditorSceneManager.GetSceneAt(i);
                if (!sc.isLoaded || !sc.IsValid())
                    continue;
                if (string.IsNullOrEmpty(sc.path))
                    return false;
                pathsInOrder.Add(sc.path);
            }

            if (pathsInOrder.Count == 0)
                return false;

            var active = EditorSceneManager.GetActiveScene();
            if (active.IsValid() && !string.IsNullOrEmpty(active.path))
                activeScenePath = active.path;

            return true;
        }

        private static void RestoreEditorOpenScenes(List<string> pathsInOrder, string activeScenePath)
        {
            if (pathsInOrder == null || pathsInOrder.Count == 0)
                return;

            try
            {
                EditorSceneManager.OpenScene(pathsInOrder[0], OpenSceneMode.Single);
                for (int i = 1; i < pathsInOrder.Count; i++)
                {
                    if (!string.IsNullOrEmpty(pathsInOrder[i]))
                        EditorSceneManager.OpenScene(pathsInOrder[i], OpenSceneMode.Additive);
                }

                if (!string.IsNullOrEmpty(activeScenePath))
                {
                    var active = EditorSceneManager.GetSceneByPath(activeScenePath);
                    if (active.isLoaded)
                        EditorSceneManager.SetActiveScene(active);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneReferenceBuildValidator] Could not fully restore open scenes after validation: {e.Message}");
            }
        }
    }
}
