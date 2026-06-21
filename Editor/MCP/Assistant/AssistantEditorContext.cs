using System.Collections.Generic;
using System.Text;
using Molca.Editor.FrameworkGraph;
using Molca.Editor.KnowledgeGraph;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Renders the user's explicitly pinned context (Sprint 24.3) into a plain-text block for the model.
    /// </summary>
    /// <remarks>
    /// Sprint 24 decision (2026-06-19): the old always-on auto-injection of selection/scene was removed.
    /// Nothing reaches the model unless the user pins it via <see cref="AssistantContextItem"/> in the
    /// chat window's context bar. The visible transcript still shows only the user's message.
    /// </remarks>
    public static class AssistantEditorContext
    {
        /// <summary>
        /// Renders the pinned context items into a single <c>[Context]</c> block, or an empty string when
        /// nothing is pinned. Live items (e.g. a live Selection) are resolved at call time.
        /// </summary>
        public static string RenderPinnedContext(IReadOnlyList<AssistantContextItem> items)
        {
            if (items == null || items.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[Context]");
            sb.AppendLine($"Mode: {(EditorApplication.isPlaying ? "Play" : "Edit")}");

            var rendered = 0;
            foreach (var item in items)
            {
                if (item == null) continue;
                var section = RenderItem(item);
                if (string.IsNullOrWhiteSpace(section)) continue;
                sb.AppendLine();
                sb.AppendLine(section.TrimEnd());
                rendered++;
            }

            return rendered == 0 ? string.Empty : sb.ToString().TrimEnd();
        }

        /// <summary>Wraps a user message with the pinned context block for model consumption.</summary>
        public static string WithContext(string userText, IReadOnlyList<AssistantContextItem> items)
        {
            var context = RenderPinnedContext(items);
            return string.IsNullOrWhiteSpace(context)
                ? userText
                : $"{context}\n\n[User message]\n{userText}";
        }

        private static string RenderItem(AssistantContextItem item) => item.Kind switch
        {
            AssistantContextKind.Selection => item.Live || string.IsNullOrEmpty(item.Snapshot)
                ? DescribeSelection()
                : "Selection (snapshot):\n" + item.Snapshot,
            AssistantContextKind.ActiveScene => DescribeActiveScene(),
            AssistantContextKind.Asset => DescribeAsset(item.AssetGuid),
            AssistantContextKind.FrameworkGraph => DescribeFrameworkGraph(),
            AssistantContextKind.KgStatus => DescribeKgStatus(),
            AssistantContextKind.Retrieved => string.IsNullOrEmpty(item.Snapshot)
                ? string.Empty
                : "Retrieved project context:\n" + item.Snapshot,
            _ => string.Empty
        };

        /// <summary>Describes the current editor selection (used live and for snapshots).</summary>
        public static string DescribeSelection()
        {
            var sb = new StringBuilder();
            var objects = Selection.objects;
            sb.AppendLine($"Selection count: {objects?.Length ?? 0}");
            if (objects == null || objects.Length == 0)
                return sb.ToString().TrimEnd();

            var limit = Mathf.Min(objects.Length, 5);
            for (var i = 0; i < limit; i++)
                AppendObject(sb, objects[i], i + 1);

            if (objects.Length > limit)
                sb.AppendLine($"Selection truncated: showing {limit} of {objects.Length} objects.");

            return sb.ToString().TrimEnd();
        }

        /// <summary>Describes the active scene.</summary>
        public static string DescribeActiveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            return scene.IsValid()
                ? $"Active scene: {scene.name} ({scene.path})"
                : "Active scene: <none>";
        }

        private static string DescribeAsset(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return string.Empty;
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return "Asset: <missing>";

            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            var sb = new StringBuilder();
            sb.AppendLine($"Asset: {(obj != null ? obj.name : System.IO.Path.GetFileName(path))} ({(obj != null ? obj.GetType().Name : "?")})");
            sb.AppendLine($"  Path: {path}");
            if (obj is GameObject go)
                sb.AppendLine($"  Components: {ComponentList(go)}");
            return sb.ToString().TrimEnd();
        }

        private static string DescribeFrameworkGraph()
        {
            try
            {
                var snapshot = FrameworkGraphBuilder.Build();
                var sb = new StringBuilder();
                sb.AppendLine($"Framework Graph ({(snapshot.IsPlayMode ? "Play" : "Edit")} mode): {snapshot.Nodes.Count} nodes, {snapshot.Edges.Count} edges.");

                var byCategory = new Dictionary<FrameworkNodeCategory, int>();
                foreach (var node in snapshot.Nodes)
                    byCategory[node.Category] = byCategory.TryGetValue(node.Category, out var c) ? c + 1 : 1;
                foreach (var kv in byCategory)
                    sb.AppendLine($"  {kv.Key}: {kv.Value}");

                foreach (var reason in snapshot.UnavailableReasons)
                    sb.AppendLine($"  Unavailable: {reason}");
                return sb.ToString().TrimEnd();
            }
            catch (System.Exception ex)
            {
                return "Framework Graph: unavailable (" + ex.Message + ")";
            }
        }

        private static string DescribeKgStatus()
        {
            var built = GraphifyCli.GraphExists;
            return built
                ? $"Knowledge graph: built (graph at {GraphifyCli.GraphJsonPath})."
                : "Knowledge graph: not built. Build it from the MCP settings tab to enable project-wide queries.";
        }

        private static void AppendObject(StringBuilder sb, Object obj, int index)
        {
            if (obj == null)
            {
                sb.AppendLine($"Selected {index}: <null>");
                return;
            }

            sb.AppendLine($"Selected {index}: {obj.name} ({obj.GetType().Name})");
            var assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(assetPath))
                sb.AppendLine($"  Asset path: {assetPath}");

            if (obj is GameObject go)
            {
                sb.AppendLine($"  Hierarchy path: {GetHierarchyPath(go.transform)}");
                sb.AppendLine($"  Active: {go.activeInHierarchy}");
                sb.AppendLine($"  Components: {ComponentList(go)}");
            }
            else if (obj is Component component)
            {
                sb.AppendLine($"  GameObject: {GetHierarchyPath(component.transform)}");
                sb.AppendLine($"  Components: {ComponentList(component.gameObject)}");
            }
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null) return "<none>";

            var path = transform.name;
            var parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private static string ComponentList(GameObject go)
        {
            if (go == null) return "<none>";

            var components = go.GetComponents<Component>();
            if (components == null || components.Length == 0) return "<none>";

            var sb = new StringBuilder();
            for (var i = 0; i < components.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(components[i] != null ? components[i].GetType().Name : "Missing Script");
            }
            return sb.ToString();
        }
    }
}
