using System.Collections.Generic;
using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only editor-workflow context tool. Captures a one-call snapshot of the editor's current
    /// state (play mode, loaded scenes, selection, open prefab stage) for assistant follow-up, so the
    /// assistant does not have to chain several discovery calls to orient itself.
    /// </summary>
    /// <remarks>Read-only; main thread only. Navigation/mutation workflow tools are deferred to Wave 2.</remarks>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateContextSnapshotTool() => new McpToolDefinition(
            name: "molca_unity_context_snapshot",
            description: "Captures a snapshot of the editor's current context: play/pause state, loaded scenes "
                       + "with active/dirty flags, the current selection (paths + ids), and the open prefab stage "
                       + "if one is active. One call to orient an assistant before deeper inspection.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteContextSnapshot,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateSelectTool() => new McpToolDefinition(
            name: "molca_unity_select",
            description: "Selects one or more objects in the editor: project assets by 'path'/'paths', GameObjects "
                       + "by 'target'/'targets' (hierarchy path or instance id), or any objects by "
                       + "'instanceId'/'instanceIds'. To select every GameObject with a given component or name, "
                       + "pass 'componentType' and/or 'nameContains' — one call, no need to enumerate the scene "
                       + "first. All forms can be combined; the first resolved object becomes the active "
                       + "selection. Changes editor selection only; not data-mutating.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Project asset path to select.\"}," +
                "\"paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Project asset paths to select together.\"}," +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id to select.\"}," +
                "\"targets\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"GameObject hierarchy paths or instance ids to select together.\"}," +
                "\"instanceId\":{\"type\":\"integer\",\"description\":\"Instance id of any object to select.\"}," +
                "\"instanceIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"Instance ids of objects to select together.\"}," +
                "\"componentType\":{\"type\":\"string\",\"description\":\"Select all GameObjects with an attached component whose type name contains this substring (case-insensitive), e.g. 'MeshRenderer', 'Light', 'Rigidbody'.\"}," +
                "\"nameContains\":{\"type\":\"string\",\"description\":\"Select all GameObjects whose name contains this substring (case-insensitive).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteSelect,
            mode: McpToolMode.Any,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static McpToolDefinition CreatePingAssetTool() => new McpToolDefinition(
            name: "molca_unity_ping_asset",
            description: "Pings (highlights) a project asset by path in the Project window. Does not mutate data.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Project asset path to ping.\"}}," +
                "\"required\":[\"path\"],\"additionalProperties\":false}",
            execute: ExecutePingAsset,
            mode: McpToolMode.Any,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static McpToolDefinition CreateOpenAssetTool() => new McpToolDefinition(
            name: "molca_unity_open",
            description: "Opens an asset by path using Unity's default handler (scene, prefab, or other asset). "
                       + "Opening a scene may prompt to save dirty scenes. Does not mutate the asset itself.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path to open.\"}}," +
                "\"required\":[\"path\"],\"additionalProperties\":false}",
            execute: ExecuteOpenAsset,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static McpToolDefinition CreateFrameSelectedTool() => new McpToolDefinition(
            name: "molca_unity_frame_selected",
            description: "Frames the current selection in the last active Scene View. Does not mutate data.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteFrameSelected,
            mode: McpToolMode.Any,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteSelect(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            // Resolve every requested object in declaration order (assets, then GameObjects, then raw
            // instance ids), de-duplicating so the same object passed twice does not appear twice. A
            // failure to resolve any single reference aborts the whole call so the caller gets a precise
            // error rather than a silently partial selection.
            var resolved = new List<Object>();
            var seen = new HashSet<int>();

            void Add(Object obj)
            {
                if (obj == null || !seen.Add(obj.GetInstanceID())) return;
                resolved.Add(obj);
            }

            foreach (var path in EnumerateStrings(args, "path", "paths"))
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(path);
                if (obj == null) return Error($"no asset at '{path}'.");
                Add(obj);
            }

            foreach (var target in EnumerateStrings(args, "target", "targets"))
            {
                var go = GameObjectEditingService.Resolve(target, out var error);
                if (go == null) return Error(error);
                Add(go);
            }

            foreach (var instanceId in EnumerateInts(args, "instanceId", "instanceIds"))
            {
                var obj = EditorUtility.EntityIdToObject(instanceId);
                if (obj == null) return Error($"no object with instance id {instanceId}.");
                Add(obj);
            }

            // Filter selection: "select all GameObjects with a MeshRenderer" resolves deterministically here
            // instead of the model dumping the scene and eyeballing the list. Combinable with explicit
            // selectors above; an empty match with no other selectors is a clear miss, not a silent no-op.
            var nameContains = args.Value<string>("nameContains");
            var componentType = args.Value<string>("componentType");
            if (!string.IsNullOrWhiteSpace(nameContains) || !string.IsNullOrWhiteSpace(componentType))
            {
                var matches = GameObjectEditingService.FindByFilters(nameContains, componentType);
                if (matches.Count == 0 && resolved.Count == 0)
                    return Error($"no GameObjects match the filter (componentType='{componentType}', nameContains='{nameContains}').");
                foreach (var go in matches) Add(go);
            }

            if (resolved.Count == 0)
                return Error("pass 'path'/'paths', 'target'/'targets', 'instanceId'/'instanceIds', or a 'componentType'/'nameContains' filter.");

            // Selection.objects sets the multi-selection; the first entry becomes Selection.activeObject.
            Selection.objects = resolved.ToArray();

            var selected = new JArray();
            foreach (var obj in resolved)
            {
                selected.Add(new JObject
                {
                    ["name"] = obj.name,
                    ["type"] = obj.GetType().Name,
                    ["instanceId"] = obj.GetInstanceID()
                });
            }

            return new JObject
            {
                ["count"] = resolved.Count,
                ["active"] = resolved[0].name,
                ["selected"] = selected
            }.ToString(Formatting.None);
        }

        /// <summary>
        /// Yields the values of a singular string property followed by each item of a plural array
        /// property, skipping null/blank entries, so callers can pass either or both forms.
        /// </summary>
        private static IEnumerable<string> EnumerateStrings(JObject args, string singular, string plural)
        {
            var single = args.Value<string>(singular);
            if (!string.IsNullOrWhiteSpace(single)) yield return single;

            if (args[plural] is JArray array)
                foreach (var token in array)
                {
                    var value = token?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(value)) yield return value;
                }
        }

        /// <summary>
        /// Yields the value of a singular integer property followed by each item of a plural array
        /// property, so callers can pass either or both forms.
        /// </summary>
        private static IEnumerable<int> EnumerateInts(JObject args, string singular, string plural)
        {
            var single = args.Value<int?>(singular);
            if (single.HasValue) yield return single.Value;

            if (args[plural] is JArray array)
                foreach (var token in array)
                    if (token != null && token.Type == JTokenType.Integer)
                        yield return token.Value<int>();
        }

        private static string ExecutePingAsset(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj == null) return Error($"no asset at '{path}'.");

            EditorGUIUtility.PingObject(obj);
            return new JObject { ["pinged"] = path }.ToString(Formatting.None);
        }

        private static string ExecuteOpenAsset(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj == null) return Error($"no asset at '{path}'.");

            var opened = AssetDatabase.OpenAsset(obj);
            return new JObject { ["path"] = path, ["opened"] = opened }.ToString(Formatting.None);
        }

        private static string ExecuteFrameSelected(string argumentsJson)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null) return Error("no active Scene View to frame in.");
            sceneView.FrameSelected();
            return new JObject
            {
                ["framed"] = Selection.activeObject != null ? Selection.activeObject.name : null
            }.ToString(Formatting.None);
        }

        private static string ExecuteContextSnapshot(string argumentsJson)
        {
            var active = SceneManager.GetActiveScene();
            var scenes = new JArray();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["isActive"] = scene == active,
                    ["isLoaded"] = scene.isLoaded,
                    ["isDirty"] = scene.isDirty
                });
            }

            var selection = new JArray();
            foreach (var obj in Selection.objects)
            {
                if (obj == null) continue;
                var item = new JObject
                {
                    ["name"] = obj.name,
                    ["type"] = obj.GetType().Name,
                    ["instanceId"] = obj.GetInstanceID()
                };
                var assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath)) item["assetPath"] = assetPath;
                if (obj is GameObject go) item["hierarchyPath"] = GameObjectEditingService.GetHierarchyPath(go);
                selection.Add(item);
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();

            return new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["activeScene"] = active.name,
                ["sceneCount"] = scenes.Count,
                ["scenes"] = scenes,
                ["selectionCount"] = selection.Count,
                ["activeSelection"] = Selection.activeObject != null ? Selection.activeObject.name : null,
                ["selection"] = selection,
                ["openPrefabStage"] = stage != null ? stage.assetPath : null
            }.ToString(Formatting.None);
        }
    }
}
