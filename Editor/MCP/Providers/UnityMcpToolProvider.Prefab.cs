using System;
using System.Linq;
using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only Unity prefab inspection tools. Reports prefab connection status and instance overrides
    /// so the assistant understands prefab state before any apply/revert mutation tool (Wave 2).
    /// </summary>
    /// <remarks>
    /// Complements the prefab-asset tools in <c>UnityMcpToolProvider.Assets.cs</c>
    /// (<c>molca_unity_prefab_contents</c>, <c>molca_unity_prefab_instantiate</c>).
    /// Read-only; main thread only.
    /// </remarks>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreatePrefabStatusTool() => new McpToolDefinition(
            name: "molca_unity_prefab_status",
            description: "Reports prefab connection status for a GameObject: whether it is a prefab instance or "
                       + "asset, the source prefab asset path, the prefab asset type, the instance status "
                       + "(connected/missing asset), and the nearest instance root. Resolve by hierarchy path "
                       + "or instance id.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecutePrefabStatus,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreatePrefabOverridesTool() => new McpToolDefinition(
            name: "molca_unity_prefab_overrides",
            description: "Lists prefab instance overrides on a GameObject: modified properties, added "
                       + "components, removed components, and added GameObjects. Resolve by hierarchy path or "
                       + "instance id. Returns an error if the target is not a prefab instance.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecutePrefabOverrides,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreatePrefabApplyTool() => new McpToolDefinition(
            name: "molca_unity_prefab_apply",
            description: "Applies all overrides on a prefab instance back to its source prefab asset. Resolve by "
                       + "hierarchy path or instance id. Writes the prefab asset file (not Unity-Undo reversible; "
                       + "the asset is snapshotted for revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Prefab instance GameObject hierarchy path or instance id.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecutePrefabApply,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.FileSnapshot);

        private static McpToolDefinition CreatePrefabRevertTool() => new McpToolDefinition(
            name: "molca_unity_prefab_revert",
            description: "Reverts all overrides on a prefab instance back to the source prefab asset's values. "
                       + "Resolve by hierarchy path or instance id. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Prefab instance GameObject hierarchy path or instance id.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecutePrefabRevert,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreatePrefabSaveTool() => new McpToolDefinition(
            name: "molca_unity_prefab_save",
            description: "Saves a scene GameObject as a new prefab asset at the given path and connects the scene "
                       + "object to it. Resolve by hierarchy path or instance id. Creating a new asset is not "
                       + "Unity-Undo reversible; delete the asset to revert.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Scene GameObject hierarchy path or instance id.\"}," +
                "\"path\":{\"type\":\"string\",\"description\":\"Target prefab asset path ending in .prefab.\"}}," +
                "\"required\":[\"target\",\"path\"],\"additionalProperties\":false}",
            execute: ExecutePrefabSave,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static McpToolDefinition CreatePrefabUnpackTool() => new McpToolDefinition(
            name: "molca_unity_prefab_unpack",
            description: "Unpacks a prefab instance, breaking its connection to the source prefab. 'mode' is "
                       + "'OutermostRoot' (default) or 'Completely'. Resolve by hierarchy path or instance id. "
                       + "One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Prefab instance GameObject hierarchy path or instance id.\"}," +
                "\"mode\":{\"type\":\"string\",\"description\":\"OutermostRoot or Completely (default OutermostRoot).\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecutePrefabUnpack,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecutePrefabApply(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return Error($"'{GameObjectEditingService.GetHierarchyPath(go)}' is not a prefab instance.");

            var root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            var undoId = string.IsNullOrEmpty(assetPath)
                ? null
                : McpUndoStack.Snapshot(assetPath, "molca_unity_prefab_apply",
                    $"Apply prefab overrides ({System.IO.Path.GetFileName(assetPath)})");

            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.AutomatedAction);

            return new JObject
            {
                ["root"] = GameObjectEditingService.GetHierarchyPath(root),
                ["assetPath"] = string.IsNullOrEmpty(assetPath) ? null : assetPath,
                ["revertible"] = undoId != null
            }.ToString(Formatting.None);
        }

        private static string ExecutePrefabRevert(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return Error($"'{GameObjectEditingService.GetHierarchyPath(go)}' is not a prefab instance.");

            var root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            PrefabUtility.RevertPrefabInstance(root, InteractionMode.AutomatedAction);

            return new JObject
            {
                ["root"] = GameObjectEditingService.GetHierarchyPath(root),
                ["reverted"] = true
            }.ToString(Formatting.None);
        }

        private static string ExecutePrefabSave(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return Error("'path' must end in .prefab.");
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                return Error($"an asset already exists at '{path}'.");

            var saved = PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.AutomatedAction, out var success);
            if (!success || saved == null) return Error($"failed to save prefab at '{path}'.");

            return new JObject
            {
                ["sourceObject"] = GameObjectEditingService.GetHierarchyPath(go),
                ["assetPath"] = path,
                ["assetGuid"] = AssetDatabase.AssetPathToGUID(path)
            }.ToString(Formatting.None);
        }

        private static string ExecutePrefabUnpack(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return Error($"'{GameObjectEditingService.GetHierarchyPath(go)}' is not a prefab instance.");

            var modeArg = args.Value<string>("mode");
            var mode = PrefabUnpackMode.OutermostRoot;
            if (!string.IsNullOrWhiteSpace(modeArg) && !Enum.TryParse(modeArg, true, out mode))
                return Error("'mode' must be OutermostRoot or Completely.");

            var root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            PrefabUtility.UnpackPrefabInstance(root, mode, InteractionMode.AutomatedAction);

            return new JObject
            {
                ["root"] = GameObjectEditingService.GetHierarchyPath(root),
                ["mode"] = mode.ToString(),
                ["unpacked"] = true
            }.ToString(Formatting.None);
        }

        private static string ExecutePrefabStatus(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            var isInstance = PrefabUtility.IsPartOfPrefabInstance(go);
            var isAsset = PrefabUtility.IsPartOfPrefabAsset(go);
            var root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            var sourcePath = isInstance
                ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go)
                : (isAsset ? AssetDatabase.GetAssetPath(go) : null);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["instanceId"] = go.GetInstanceID(),
                ["isPrefabInstance"] = isInstance,
                ["isPrefabAsset"] = isAsset,
                ["prefabAssetType"] = PrefabUtility.GetPrefabAssetType(go).ToString(),
                ["instanceStatus"] = PrefabUtility.GetPrefabInstanceStatus(go).ToString(),
                ["sourceAssetPath"] = string.IsNullOrEmpty(sourcePath) ? null : sourcePath,
                ["nearestInstanceRoot"] = root != null ? GameObjectEditingService.GetHierarchyPath(root) : null,
                ["isOutermostRoot"] = root == go
            }.ToString(Formatting.None);
        }

        private static string ExecutePrefabOverrides(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return Error($"GameObject '{GameObjectEditingService.GetHierarchyPath(go)}' is not a prefab instance.");

            var root = PrefabUtility.GetNearestPrefabInstanceRoot(go) ?? go;

            var modifications = new JArray();
            foreach (var mod in PrefabUtility.GetPropertyModifications(root) ?? new PropertyModification[0])
            {
                if (mod == null || mod.target == null) continue;
                modifications.Add(new JObject
                {
                    ["target"] = mod.target.GetType().Name,
                    ["propertyPath"] = mod.propertyPath,
                    ["value"] = mod.value,
                    ["objectReference"] = mod.objectReference != null ? mod.objectReference.name : null
                });
            }

            var addedComponents = new JArray(PrefabUtility.GetAddedComponents(root)
                .Where(a => a.instanceComponent != null)
                .Select(a => (JToken)new JObject
                {
                    ["component"] = a.instanceComponent.GetType().Name,
                    ["path"] = GameObjectEditingService.GetHierarchyPath(a.instanceComponent.gameObject)
                }));

            var removedComponents = new JArray(PrefabUtility.GetRemovedComponents(root)
                .Where(r => r.assetComponent != null)
                .Select(r => (JToken)new JObject
                {
                    ["component"] = r.assetComponent.GetType().Name
                }));

            var addedGameObjects = new JArray(PrefabUtility.GetAddedGameObjects(root)
                .Where(a => a.instanceGameObject != null)
                .Select(a => (JToken)new JObject
                {
                    ["path"] = GameObjectEditingService.GetHierarchyPath(a.instanceGameObject)
                }));

            return new JObject
            {
                ["root"] = GameObjectEditingService.GetHierarchyPath(root),
                ["modifiedPropertyCount"] = modifications.Count,
                ["modifiedProperties"] = modifications,
                ["addedComponents"] = addedComponents,
                ["removedComponents"] = removedComponents,
                ["addedGameObjects"] = addedGameObjects
            }.ToString(Formatting.None);
        }
    }
}
