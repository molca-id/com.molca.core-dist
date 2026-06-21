using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Molca.ReferenceSystem;
using Molca.Settings;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Validates serialized <see cref="SceneObjectReference"/> fields in prefabs and
    /// currently open scenes against the Ref Ids recorded in
    /// <see cref="ReferenceManagerSettings"/>. An id that is set but unknown will
    /// fail to resolve at runtime.
    /// </summary>
    /// <remarks>
    /// ScriptableObjects are intentionally not scanned: a <see cref="SceneObjectReference"/>
    /// resolves only against scene-loaded objects via <c>ReferenceManager</c>, so one stored
    /// in an SO can never resolve at runtime (the "SOs-out" boundary documented on
    /// <see cref="ReferenceManagerSettings"/>). Scanning every SO was also the dominant cost
    /// on large projects.
    ///
    /// Closed scenes are not scanned (opening every scene from a validation pass is
    /// too invasive); run the check with the relevant scenes open, or rely on the
    /// scene-save validation in ReferenceManagerSettings.
    /// </remarks>
    public class SceneObjectReferenceCheck : IDoctorCheck
    {
        public string Id => "unresolvable-scene-reference";
        public string Description => "SceneObjectReference ids not present in ReferenceManagerSettings";

        // This check stays on the main thread (AssetDatabase, SerializedObject, and
        // SceneManager are main-thread only) and yields before each heavy prefab/scene so
        // a large project doesn't freeze the editor and the run stays cancellable mid-scan.

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            var issues = new List<DoctorIssue>();

            var settings = FindSettings();
            if (settings == null)
            {
                issues.Add(new DoctorIssue(Id, DoctorSeverity.Info,
                    "No ReferenceManagerSettings asset found — SceneObjectReference ids cannot be validated."));
                return issues;
            }

            var knownByType = settings.GetReferenceTypes()
                .ToDictionary(t => t, t => new HashSet<string>(settings.GetReferenceIds(t)));
            var allKnown = new HashSet<string>(knownByType.Values.SelectMany(s => s));

            // Prefabs: mirror the reference-system scan — only those inside PrefabScanPaths.
            // When no scan paths are configured, prefab referenceable ids are never registered
            // in the DB, so validating prefab references would only produce false "unknown"
            // findings. Skipping them is also what kept large-project runs from loading every
            // prefab in the project (the dominant cost).
            var scanPaths = settings.PrefabScanPaths;
            if (scanPaths != null && scanPaths.Count > 0)
            {
                // Resolve the in-scope prefab paths up front so progress has a real total.
                var prefabPaths = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => !context.IsIgnored(p) && IsInScanPaths(p, scanPaths))
                    .ToList();

                for (int p = 0; p < prefabPaths.Count; p++)
                {
                    var path = prefabPaths[p];

                    // Each prefab is heavy (load + per-component SerializedObject scan), so
                    // yield before every one — this keeps Cancel responsive to ~one prefab.
                    context.ReportStatus($"Prefabs {p + 1}/{prefabPaths.Count}");
                    await EditorYieldAsync(cancellationToken);

                    if (AssetDatabase.LoadMainAssetAtPath(path) is not GameObject go)
                        continue;

                    var targets = go.GetComponentsInChildren<MonoBehaviour>(true)
                        .Where(c => c != null).Cast<Object>();
                    issues.AddRange(ScanObjects(targets, path, knownByType, allKnown));
                }
            }

            // Components in all open scenes.
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                if (!string.IsNullOrEmpty(scene.path) && context.IsIgnored(scene.path))
                    continue;

                context.ReportStatus($"Scene {scene.name}");
                await EditorYieldAsync(cancellationToken);
                var behaviours = scene.GetRootGameObjects()
                    .SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true))
                    .Where(c => c != null)
                    .Cast<Object>();
                issues.AddRange(ScanObjects(behaviours, scene.path ?? scene.name, knownByType, allKnown));
            }

            return issues;
        }

        // Yields until the next editor tick. Uses EditorApplication.update rather than
        // Awaitable.NextFrameAsync because the player loop that drives NextFrameAsync does
        // not advance in Edit Mode, so awaiting a frame there never resumes.
        private static Awaitable EditorYieldAsync(CancellationToken cancellationToken)
        {
            var source = new AwaitableCompletionSource();

            void Tick()
            {
                EditorApplication.update -= Tick;
                if (cancellationToken.IsCancellationRequested)
                    source.SetCanceled();
                else
                    source.SetResult();
            }

            EditorApplication.update += Tick;
            return source.Awaitable;
        }

        // Mirrors ReferenceManagerSettingsEditor.IsPrefabInScanList: a prefab is in scope
        // when its path starts with a scan-path folder entry or matches one exactly.
        private static bool IsInScanPaths(string assetPath, IReadOnlyList<string> scanPaths)
        {
            assetPath = assetPath.Replace('\\', '/');
            foreach (var entry in scanPaths)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;
                var norm = entry.Replace('\\', '/').TrimEnd('/');
                if (assetPath.StartsWith(norm + "/", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(assetPath, norm, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private IEnumerable<DoctorIssue> ScanObjects(
            IEnumerable<Object> objects, string assetPath,
            Dictionary<string, HashSet<string>> knownByType, HashSet<string> allKnown)
        {
            foreach (var obj in objects)
            {
                var serialized = new SerializedObject(obj);
                var property = serialized.GetIterator();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (property.propertyType != SerializedPropertyType.Generic)
                        continue;

                    var refId = property.FindPropertyRelative("refId");
                    var refType = property.FindPropertyRelative("refType");
                    if (refId == null || refType == null || refId.propertyType != SerializedPropertyType.String)
                        continue;

                    enterChildren = false; // it's a SceneObjectReference; don't descend
                    var id = refId.stringValue;
                    if (string.IsNullOrEmpty(id))
                        continue; // unset references are legal

                    bool known = knownByType.TryGetValue(refType.stringValue ?? "", out var ids)
                        ? ids.Contains(id)
                        : allKnown.Contains(id); // type list may be stale; fall back to any-type match

                    if (!known)
                    {
                        yield return new DoctorIssue(Id, DoctorSeverity.Error,
                            $"SceneObjectReference `{property.propertyPath}` on {obj.name} points at unknown Ref Id \"{id}\" (type \"{refType.stringValue}\"). Re-scan references or fix the id.",
                            assetPath);
                    }
                }
            }
        }

        private static ReferenceManagerSettings FindSettings()
        {
            // Prefer the module wired into GlobalSettings; fall back to any asset.
            // GetModule may throw in edit mode when project settings are absent.
            try
            {
                var fromGlobals = GlobalSettings.GetModule<ReferenceManagerSettings>();
                if (fromGlobals != null)
                    return fromGlobals;
            }
            catch (System.Exception)
            {
                // fall through to the asset search
            }

            var guid = AssetDatabase.FindAssets("t:ReferenceManagerSettings").FirstOrDefault();
            return guid == null ? null
                : AssetDatabase.LoadAssetAtPath<ReferenceManagerSettings>(AssetDatabase.GUIDToAssetPath(guid));
        }
    }
}
