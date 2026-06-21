using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

namespace Molca.Editor
{
    /// <summary>
    /// Scene view tool: hold the configured modifier and click to show a list of GameObjects
    /// whose position is inside the proximity sphere around the click point. Works for any GameObject;
    /// optional component type filter limits candidates.
    /// </summary>
    [InitializeOnLoad]
    public static class AreaPicker
    {
        private const float DefaultRayDistance = 1000f;

        public const string PrefKeyEnabled = "Molca.AreaPicker.Enabled";
        public const string PrefKeyProximityRadius = "Molca.AreaPicker.ProximityRadius";
        public const string PrefKeyModifier = "Molca.AreaPicker.Modifier";
        public const string PrefKeyFilterTypeName = "Molca.AreaPicker.FilterTypeName";

        public static bool GetEnabled() => MolcaEditorPrefs.GetBool(PrefKeyEnabled, true);
        public static void SetEnabled(bool value) => MolcaEditorPrefs.SetBool(PrefKeyEnabled, value);

        public static float GetProximityRadius() => MolcaEditorPrefs.GetFloat(PrefKeyProximityRadius, 1f);
        public static void SetProximityRadius(float value) => MolcaEditorPrefs.SetFloat(PrefKeyProximityRadius, value);

        public static EventModifiers GetModifier() => (EventModifiers)MolcaEditorPrefs.GetInt(PrefKeyModifier, (int)EventModifiers.Alt);
        public static void SetModifier(EventModifiers value) => MolcaEditorPrefs.SetInt(PrefKeyModifier, (int)value);

        public static string GetFilterTypeName() => MolcaEditorPrefs.GetString(PrefKeyFilterTypeName, "");
        public static void SetFilterTypeName(string value) => MolcaEditorPrefs.SetString(PrefKeyFilterTypeName, value ?? "");

        static AreaPicker()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!GetEnabled())
                return;

            Event e = Event.current;
            float radius = Mathf.Max(0.01f, GetProximityRadius());

            // Show pick radius circle while modifier is held (before click)
            if (e.type == EventType.Repaint && (e.modifiers & GetModifier()) == GetModifier())
            {
                Ray previewRay = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                Vector3 previewCenter = GetPickCenterFromRay(previewRay);
                Handles.color = new Color(1f, 0.9f, 0.2f, 0.9f); // doctor:ignore — scene-view gizmo tint, not editor UI chrome
                Handles.DrawWireDisc(previewCenter, sceneView.camera.transform.forward, radius);
                Handles.color = Color.white;
                sceneView.Repaint();
                return;
            }

            if (e.type != EventType.MouseDown || e.button != 0 || e.modifiers != GetModifier())
                return;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            Vector3 center = GetPickCenterFromRay(ray);
            List<GameObject> candidates = GatherCandidates(ray, radius, center);
            if (candidates.Count == 0)
            {
                var menu = new GenericMenu();
                menu.AddDisabledItem(new GUIContent("No objects under cursor"));
                menu.ShowAsContext();
                e.Use();
                return;
            }

            var genericMenu = new GenericMenu();
            var candidatesArray = candidates.Where(go => go != null).ToArray();
            genericMenu.AddItem(new GUIContent("Select All"), false, () =>
            {
                AddToSelection(candidatesArray);
                if (candidatesArray.Length > 0)
                    EditorGUIUtility.PingObject(candidatesArray[0]);
            });
            genericMenu.AddSeparator("");
            foreach (GameObject go in candidates)
            {
                if (go == null)
                    continue;
                string path = GetHierarchyPath(go);
                GameObject captured = go;
                genericMenu.AddItem(new GUIContent(path), false, () =>
                {
                    AddToSelection(new UnityEngine.Object[] { captured });
                    EditorGUIUtility.PingObject(captured);
                });
            }
            genericMenu.ShowAsContext();
            e.Use();
        }

        /// <summary>
        /// Adds the given object(s) to the current selection (merge with existing, no duplicates).
        /// Integrates with multi-selection: existing selection is preserved and new picks are added.
        /// </summary>
        private static void AddToSelection(IEnumerable<UnityEngine.Object> toAdd)
        {
            var current = new HashSet<UnityEngine.Object>(Selection.objects);
            foreach (var obj in toAdd)
                if (obj != null)
                    current.Add(obj);
            Selection.objects = current.ToArray();
        }

        private static Vector3 GetPickCenterFromRay(Ray ray)
        {
            Vector3 center = ray.GetPoint(DefaultRayDistance);
            RaycastHit[] hits3D = Physics.RaycastAll(ray, DefaultRayDistance, -1, QueryTriggerInteraction.Collide);
            if (hits3D.Length > 0)
            {
                System.Array.Sort(hits3D, (a, b) => a.distance.CompareTo(b.distance));
                center = hits3D[0].point;
            }
            else
            {
                RaycastHit2D[] hits2D = Physics2D.RaycastAll(new Vector2(ray.origin.x, ray.origin.y), new Vector2(ray.direction.x, ray.direction.y), DefaultRayDistance);
                if (hits2D.Length > 0)
                {
                    var first = hits2D.OrderBy(h => Vector2.Distance(h.point, ray.origin)).First();
                    center = new Vector3(first.point.x, first.point.y, center.z);
                }
            }
            return center;
        }

        private static List<GameObject> GatherCandidates(Ray ray, float radius, Vector3 center)
        {
            Vector3 camPos = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera.transform.position : ray.origin;
            Type filterType = ResolveFilterType(GetFilterTypeName());
            var withDistance = new List<(GameObject go, float distance)>();
            float radiusSq = radius * radius;

            // Prefer active scene only (much faster than FindObjectsByType over all loaded objects)
            var toCheck = GetGameObjectsInActiveScene();
            if (toCheck == null)
                toCheck = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            for (int i = 0; i < toCheck.Length; i++)
            {
                GameObject go = toCheck[i];
                if (go == null)
                    continue;
                if (filterType != null && go.GetComponent(filterType) == null)
                    continue;

                Vector3 pos = go.transform.position;
                bool positionInSphere = (center - pos).sqrMagnitude <= radiusSq;

                bool boundsInRange = false;
                float distSq = (camPos - pos).sqrMagnitude;
                if (TryGetBounds(go, out Bounds bounds))
                {
                    boundsInRange = bounds.Contains(center) || (center - bounds.ClosestPoint(center)).sqrMagnitude <= radiusSq;
                    distSq = (camPos - bounds.ClosestPoint(camPos)).sqrMagnitude;
                }

                if (positionInSphere || boundsInRange)
                    withDistance.Add((go, distSq));
            }

            return withDistance.OrderBy(t => t.distance).Select(t => t.go).ToList();
        }

        /// <summary>
        /// Gets world-space bounds from Renderer, Collider, or Collider2D if present. Returns false only for missing/invalid.
        /// </summary>
        private static bool TryGetBounds(GameObject go, out Bounds bounds)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null && r.enabled)
            {
                bounds = r.bounds;
                return true;
            }
            var c = go.GetComponent<Collider>();
            if (c != null && c.enabled)
            {
                bounds = c.bounds;
                return true;
            }
            var c2 = go.GetComponent<Collider2D>();
            if (c2 != null && c2.enabled)
            {
                bounds = c2.bounds;
                return true;
            }
            bounds = default;
            return false;
        }

        /// <summary>
        /// Collects all GameObjects in the active scene only. Much faster than FindObjectsByType over all loaded objects.
        /// Returns null if no active scene or not loaded (caller should fall back to FindObjectsByType).
        /// </summary>
        private static GameObject[] GetGameObjectsInActiveScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.isLoaded)
                return null;
            GameObject[] roots = scene.GetRootGameObjects();
            if (roots == null || roots.Length == 0)
                return Array.Empty<GameObject>();
            var list = new List<GameObject>(roots.Length * 4);
            for (int i = 0; i < roots.Length; i++)
                CollectDescendants(roots[i].transform, list);
            return list.ToArray();
        }

        private static void CollectDescendants(Transform t, List<GameObject> list)
        {
            list.Add(t.gameObject);
            for (int i = 0; i < t.childCount; i++)
                CollectDescendants(t.GetChild(i), list);
        }

        private static Type ResolveFilterType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;
            Type t = Type.GetType(typeName.Trim());
            if (t != null)
                return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(typeName.Trim());
                    if (t != null)
                        return t;
                }
                catch { }
            }
            return null;
        }

        private static string GetHierarchyPath(GameObject go)
        {
            if (go == null)
                return "";
            var path = new List<string>();
            Transform t = go.transform;
            while (t != null)
            {
                path.Add(t.name);
                t = t.parent;
            }
            path.Reverse();
            return string.Join("/", path);
        }
    }
}
