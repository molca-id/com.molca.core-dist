using System;
using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Unity GameObject mutation tools routed through <see cref="GameObjectEditingService"/> so every
    /// mutation is one Unity Undo group.
    /// </summary>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateGameObjectRenameTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_rename",
            description: "Renames a GameObject (by hierarchy path or instance id). One undo group; revert "
                       + "with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Hierarchy path (e.g. 'A/B/C') or instance id.\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"The new GameObject name.\"}}," +
                "\"required\":[\"target\",\"name\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectRename,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectRename(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            var newName = args.Value<string>("name");
            if (string.IsNullOrEmpty(newName)) return Error("'name' must be non-empty.");

            GameObjectEditingService.Rename(go, newName);
            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID()
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateGameObjectSetActiveTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_set_active",
            description: "Sets a GameObject's active state (by hierarchy path or instance id). One undo "
                       + "group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Hierarchy path or instance id.\"}," +
                "\"active\":{\"type\":\"boolean\",\"description\":\"Desired active-self state.\"}}," +
                "\"required\":[\"target\",\"active\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectSetActive,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectSetActive(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            GameObjectEditingService.SetActive(go, args.Value<bool>("active"));
            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["active"] = go.activeSelf
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateGameObjectSetTransformTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_set_transform",
            description: "Sets a GameObject's local position, rotation (Euler degrees), and/or scale (by "
                       + "hierarchy path or instance id). Each is an optional [x,y,z] array; omitted ones "
                       + "are left unchanged. One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Hierarchy path or instance id.\"}," +
                "\"position\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"Local position [x,y,z].\"}," +
                "\"eulerAngles\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"Local rotation in Euler degrees [x,y,z].\"}," +
                "\"scale\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"Local scale [x,y,z].\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectSetTransform,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectSetTransform(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            var position = ReadVector3(args["position"]);
            var euler = ReadVector3(args["eulerAngles"]);
            var scale = ReadVector3(args["scale"]);
            if (position == null && euler == null && scale == null)
                return Error("provide at least one of 'position', 'eulerAngles', or 'scale' as an [x,y,z] array.");

            GameObjectEditingService.SetLocalTransform(go, position, euler, scale);
            var t = go.transform;
            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["position"] = Vector3ToJson(t.localPosition),
                ["eulerAngles"] = Vector3ToJson(t.localEulerAngles),
                ["scale"] = Vector3ToJson(t.localScale)
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateGameObjectCreateTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_create",
            description: "Creates a GameObject in the scene. Empty by default, or a 'primitive' "
                       + "(Cube/Sphere/Capsule/Cylinder/Plane/Quad). Optional 'parent' is a hierarchy path "
                       + "or instance id. Returns the new object's path and instance id. One undo group; "
                       + "revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\",\"description\":\"Name for the new GameObject.\"}," +
                "\"parent\":{\"type\":\"string\",\"description\":\"Parent hierarchy path or instance id (root if omitted).\"}," +
                "\"primitive\":{\"type\":\"string\",\"description\":\"Cube/Sphere/Capsule/Cylinder/Plane/Quad; omit for an empty GameObject.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteGameObjectCreate,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectCreate(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            GameObject parent = null;
            var parentArg = args.Value<string>("parent");
            if (!string.IsNullOrWhiteSpace(parentArg))
            {
                parent = GameObjectEditingService.Resolve(parentArg, out var parentError);
                if (parent == null) return Error(parentError);
            }

            PrimitiveType? primitive = null;
            var primitiveArg = args.Value<string>("primitive");
            if (!string.IsNullOrWhiteSpace(primitiveArg))
            {
                if (!Enum.TryParse<PrimitiveType>(primitiveArg, true, out var pt))
                    return Error($"unknown primitive '{primitiveArg}' (expected Cube/Sphere/Capsule/Cylinder/Plane/Quad).");
                primitive = pt;
            }

            var go = GameObjectEditingService.Create(args.Value<string>("name"), parent, primitive);
            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID()
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateGameObjectDeleteTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_delete",
            description: "Deletes a GameObject (by hierarchy path or instance id) and its children. One undo "
                       + "group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Hierarchy path or instance id.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectDelete,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectDelete(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            var path = GameObjectEditingService.GetHierarchyPath(go);
            GameObjectEditingService.Delete(go);
            return new JObject { ["deleted"] = path }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateGameObjectDuplicateTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_duplicate",
            description: "Duplicates a GameObject (by hierarchy path or instance id), preserving its parent. "
                       + "One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Hierarchy path or instance id.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectDuplicate,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectDuplicate(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            var duplicate = GameObjectEditingService.Duplicate(go);
            if (duplicate == null) return Error("duplicate failed.");
            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(duplicate),
                ["name"] = duplicate.name,
                ["instanceId"] = duplicate.GetInstanceID()
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateGameObjectReparentTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_reparent",
            description: "Reparents a GameObject under another GameObject, or to the scene root when "
                       + "'newParent' is omitted. One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Hierarchy path or instance id.\"}," +
                "\"newParent\":{\"type\":\"string\",\"description\":\"New parent hierarchy path or instance id; omit for root.\"}," +
                "\"worldPositionStays\":{\"type\":\"boolean\",\"description\":\"Preserve world transform (default true).\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectReparent,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectReparent(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            GameObject newParent = null;
            var parentArg = args.Value<string>("newParent");
            if (!string.IsNullOrWhiteSpace(parentArg))
            {
                newParent = GameObjectEditingService.Resolve(parentArg, out var parentError);
                if (newParent == null) return Error(parentError);
            }

            var worldPositionStays = args.Value<bool?>("worldPositionStays") ?? true;
            if (!GameObjectEditingService.Reparent(go, newParent, worldPositionStays, out var reparentError))
                return Error(reparentError);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["parent"] = newParent != null ? GameObjectEditingService.GetHierarchyPath(newParent) : null
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateGameObjectAddComponentTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_add_component",
            description: "Adds a component of the given 'type' (class name or full name) to a GameObject (by "
                       + "hierarchy path or instance id). One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Hierarchy path or instance id.\"}," +
                "\"type\":{\"type\":\"string\",\"description\":\"Concrete Component type name (or full name).\"}}," +
                "\"required\":[\"target\",\"type\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectAddComponent,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectAddComponent(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            var type = ResolveComponentType(args.Value<string>("type"), out var typeError);
            if (type == null) return Error(typeError);

            var component = GameObjectEditingService.AddComponent(go, type, out var addError);
            if (component == null) return Error(addError);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["component"] = type.Name
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateGameObjectRemoveComponentTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_remove_component",
            description: "Removes a component by GameObject target and component index. Transform cannot be "
                       + "removed. One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id.\"}," +
                "\"componentIndex\":{\"type\":\"integer\",\"description\":\"Component index from molca_unity_gameobject_components.\"}}," +
                "\"required\":[\"target\",\"componentIndex\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectRemoveComponent,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectRemoveComponent(string argumentsJson)
        {
            var component = ResolveComponent(ParseArgs(argumentsJson), out var go, out var index, out var error);
            if (component == null) return Error(error);
            var typeName = component.GetType().Name;
            if (!GameObjectEditingService.RemoveComponent(component, out var removeError))
                return Error(removeError);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["removedComponentIndex"] = index,
                ["removedComponent"] = typeName
            }.ToString(Formatting.None);
        }
    }
}
