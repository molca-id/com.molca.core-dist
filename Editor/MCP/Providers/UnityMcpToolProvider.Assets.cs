using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// General Unity AssetDatabase and prefab discovery tools, plus safe prefab instantiation.
    /// </summary>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateAssetsTool() => new McpToolDefinition(
            name: "molca_unity_assets",
            description: "Finds project assets by text query, optional Unity type, and optional folder. "
                       + "Returns AssetDatabase path, GUID, type, name, and folder status.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"AssetDatabase search text, e.g. name substring.\"}," +
                "\"type\":{\"type\":\"string\",\"description\":\"Optional Unity asset type filter, e.g. Prefab, Material, Texture2D, Scene.\"}," +
                "\"folder\":{\"type\":\"string\",\"description\":\"Optional folder path under Assets/ to search.\"}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Max entries to return (default 50).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteAssets,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteAssets(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var query = args.Value<string>("query") ?? string.Empty;
            var type = args.Value<string>("type");
            var folder = args.Value<string>("folder");
            var limit = args["limit"] != null ? Math.Max(1, args.Value<int>("limit")) : 50;

            var filter = string.IsNullOrWhiteSpace(type) ? query.Trim() : $"{query} t:{type}".Trim();
            var folders = string.IsNullOrWhiteSpace(folder) ? null : new[] { folder };
            var guids = folders == null ? AssetDatabase.FindAssets(filter) : AssetDatabase.FindAssets(filter, folders);

            var assets = new JArray();
            var truncated = false;
            for (var i = 0; i < guids.Length; i++)
            {
                if (assets.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                var guid = guids[i];
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                var mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
                assets.Add(new JObject
                {
                    ["path"] = path,
                    ["guid"] = guid,
                    ["name"] = System.IO.Path.GetFileNameWithoutExtension(path),
                    ["type"] = mainType != null ? mainType.Name : null,
                    ["isFolder"] = AssetDatabase.IsValidFolder(path)
                });
            }

            return new JObject
            {
                ["query"] = query,
                ["type"] = type,
                ["folder"] = folder,
                ["count"] = assets.Count,
                ["truncated"] = truncated,
                ["assets"] = assets
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateAssetDependenciesTool() => new McpToolDefinition(
            name: "molca_unity_asset_dependencies",
            description: "Lists AssetDatabase dependencies for an asset path, including dependency path, GUID, and main type.",
            inputSchemaJson:
                "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path, e.g. Assets/Prefabs/Foo.prefab.\"}," +
                "\"recursive\":{\"type\":\"boolean\",\"description\":\"Whether to include recursive dependencies (default false).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteAssetDependencies,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteAssetDependencies(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            if (AssetDatabase.LoadMainAssetAtPath(path) == null && !AssetDatabase.IsValidFolder(path))
                return Error($"asset not found at '{path}'.");

            var recursive = args["recursive"] != null && args.Value<bool>("recursive");
            var deps = new JArray();
            foreach (var dep in AssetDatabase.GetDependencies(path, recursive).Where(p => p != path).OrderBy(p => p))
            {
                var mainType = AssetDatabase.GetMainAssetTypeAtPath(dep);
                deps.Add(new JObject
                {
                    ["path"] = dep,
                    ["guid"] = AssetDatabase.AssetPathToGUID(dep),
                    ["type"] = mainType != null ? mainType.Name : null
                });
            }

            return new JObject
            {
                ["path"] = path,
                ["recursive"] = recursive,
                ["count"] = deps.Count,
                ["dependencies"] = deps
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreatePrefabContentsTool() => new McpToolDefinition(
            name: "molca_unity_prefab_contents",
            description: "Loads a prefab asset for read-only inspection and returns its hierarchy paths, active states, and components.",
            inputSchemaJson:
                "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Prefab asset path.\"}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Max GameObjects to return (default 200).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecutePrefabContents,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecutePrefabContents(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                return Error($"prefab not found at '{path}'.");

            var limit = args["limit"] != null ? Math.Max(1, args.Value<int>("limit")) : 200;
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(path);
                var objects = new JArray();
                var truncated = false;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (objects.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    objects.Add(new JObject
                    {
                        ["name"] = t.gameObject.name,
                        ["path"] = GameObjectEditingService.GetHierarchyPath(t.gameObject),
                        ["activeSelf"] = t.gameObject.activeSelf,
                        ["components"] = new JArray(t.gameObject.GetComponents<Component>()
                            .Select(c => c != null ? c.GetType().Name : "<missing>"))
                    });
                }

                return new JObject
                {
                    ["path"] = path,
                    ["rootName"] = root.name,
                    ["count"] = objects.Count,
                    ["truncated"] = truncated,
                    ["objects"] = objects
                }.ToString(Formatting.None);
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static McpToolDefinition CreatePrefabInstantiateTool() => new McpToolDefinition(
            name: "molca_unity_prefab_instantiate",
            description: "Instantiates a prefab asset into the active scene. Edit-mode action, allowlist-confirmed, and revertible with Unity Undo.",
            inputSchemaJson:
                "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Prefab asset path.\"}," +
                "\"parent\":{\"type\":\"string\",\"description\":\"Optional parent GameObject path, name, or instance id.\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"Optional instance name override.\"}," +
                "\"position\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3}," +
                "\"eulerAngles\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3}," +
                "\"scale\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3}}," +
                "\"additionalProperties\":false}",
            execute: ExecutePrefabInstantiate,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecutePrefabInstantiate(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) return Error($"prefab not found at '{path}'.");

            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null) return Error($"failed to instantiate prefab '{path}'.");
            Undo.RegisterCreatedObjectUndo(instance, "MCP Instantiate Prefab");

            var parentArg = args.Value<string>("parent");
            if (!string.IsNullOrWhiteSpace(parentArg))
            {
                var parent = GameObjectEditingService.Resolve(parentArg, out var parentError);
                if (parent == null)
                {
                    Undo.DestroyObjectImmediate(instance);
                    return Error(parentError);
                }

                if (!GameObjectEditingService.Reparent(instance, parent, worldPositionStays: false, out var reparentError))
                {
                    Undo.DestroyObjectImmediate(instance);
                    return Error(reparentError);
                }
            }

            Undo.RecordObject(instance.transform, "MCP Configure Prefab Instance Transform");
            var newName = args.Value<string>("name");
            if (!string.IsNullOrWhiteSpace(newName))
            {
                Undo.RecordObject(instance, "MCP Rename Prefab Instance");
                instance.name = newName.Trim();
            }

            var position = ReadVector3(args["position"]);
            var euler = ReadVector3(args["eulerAngles"]);
            var scale = ReadVector3(args["scale"]);
            if (position.HasValue) instance.transform.localPosition = position.Value;
            if (euler.HasValue) instance.transform.localEulerAngles = euler.Value;
            if (scale.HasValue) instance.transform.localScale = scale.Value;

            EditorUtility.SetDirty(instance);
            return new JObject
            {
                ["path"] = path,
                ["name"] = instance.name,
                ["instanceId"] = instance.GetInstanceID(),
                ["hierarchyPath"] = GameObjectEditingService.GetHierarchyPath(instance)
            }.ToString(Formatting.None);
        }
    }
}
