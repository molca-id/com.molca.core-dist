using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Unity GameObject mutation tools routed through <see cref="GameObjectEditingService"/> so every
    /// mutation is one Unity Undo group.
    /// </summary>
    public sealed partial class UnityMcpToolProvider
    {
        /// <summary>
        /// Resolves the GameObject(s) an action tool should act on from a single 'target' or a plural
        /// 'targets' array (each entry a hierarchy path, object name, or instance id). Multi-apply is opt-in:
        /// a lone name matching several objects is NOT expanded automatically — this returns
        /// <paramref name="failure"/> listing each candidate's unique instance id so the caller can ask the
        /// user which to act on and re-issue with the chosen id(s) ("apply to all" = pass every id in
        /// 'targets'). Explicitly listed selectors are treated as already chosen and applied in full.
        /// Deliberately not used by destructive tools (delete / remove-component), which stay single-target so
        /// ambiguity always resolves to an explicit confirmation rather than a silent apply-to-all.
        /// </summary>
        /// <returns>True with <paramref name="targets"/> populated (deduped); false with a reason in <paramref name="failure"/>.</returns>
        private static bool TryResolveTargets(JObject args, out List<GameObject> targets, out string failure)
        {
            targets = new List<GameObject>();
            failure = null;
            var seen = new HashSet<int>();
            var any = false;

            foreach (var selector in EnumerateStrings(args, "target", "targets"))
            {
                any = true;
                var matches = GameObjectEditingService.ResolveAll(selector, out var err);
                if (matches.Count == 0) { failure = err; return false; }
                if (matches.Count > 1)
                {
                    failure = GameObjectEditingService.AmbiguityMessage(selector, matches);
                    return false;
                }
                if (seen.Add(matches[0].GetInstanceID())) targets.Add(matches[0]);
            }

            if (!any) failure = "target is required (a hierarchy path or an instance id).";
            return any;
        }

        /// <summary>
        /// Renders an action's per-target result JSON: the single object unchanged when exactly one target was
        /// acted on (backward-compatible with the single-target tools), otherwise a
        /// <c>{"applied":n,"results":[…]}</c> envelope so a multi-apply reports every affected object.
        /// </summary>
        private static string BatchResult(JArray results) =>
            results.Count == 1
                ? results[0].ToString(Formatting.None)
                : new JObject { ["applied"] = results.Count, ["results"] = results }.ToString(Formatting.None);

        /// <summary>The '/'-separated 'target'/'targets' schema fragment shared by the multi-apply action tools.</summary>
        private const string TargetsSchema =
            "\"target\":{\"type\":\"string\",\"description\":\"Hierarchy path, object name, or instance id.\"}," +
            "\"targets\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
            "\"description\":\"Several targets to apply to at once (paths/names/instance ids). Use to act on all of an ambiguous set.\"}";

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
            description: "Sets the active state of one GameObject ('target') or several ('targets'). "
                       + "Revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                TargetsSchema + "," +
                "\"active\":{\"type\":\"boolean\",\"description\":\"Desired active-self state.\"}}," +
                "\"required\":[\"active\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectSetActive,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectSetActive(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (!TryResolveTargets(args, out var targets, out var failure)) return Error(failure);

            var active = args.Value<bool>("active");
            var results = new JArray();
            foreach (var go in targets)
            {
                GameObjectEditingService.SetActive(go, active);
                results.Add(new JObject
                {
                    ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                    ["active"] = go.activeSelf
                });
            }
            return BatchResult(results);
        }

        private static McpToolDefinition CreateGameObjectSetTransformTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_set_transform",
            description: "Sets the local position, rotation (Euler degrees), and/or scale of one GameObject "
                       + "('target') or several ('targets'). Each is an optional [x,y,z] array; omitted ones "
                       + "are left unchanged. Revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                TargetsSchema + "," +
                "\"position\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"Local position [x,y,z].\"}," +
                "\"eulerAngles\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"Local rotation in Euler degrees [x,y,z].\"}," +
                "\"scale\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"Local scale [x,y,z].\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteGameObjectSetTransform,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectSetTransform(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (!TryResolveTargets(args, out var targets, out var failure)) return Error(failure);

            var position = ReadVector3(args["position"]);
            var euler = ReadVector3(args["eulerAngles"]);
            var scale = ReadVector3(args["scale"]);
            if (position == null && euler == null && scale == null)
                return Error("provide at least one of 'position', 'eulerAngles', or 'scale' as an [x,y,z] array.");

            var results = new JArray();
            foreach (var go in targets)
            {
                GameObjectEditingService.SetLocalTransform(go, position, euler, scale);
                var t = go.transform;
                results.Add(new JObject
                {
                    ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                    ["position"] = Vector3ToJson(t.localPosition),
                    ["eulerAngles"] = Vector3ToJson(t.localEulerAngles),
                    ["scale"] = Vector3ToJson(t.localScale)
                });
            }
            return BatchResult(results);
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
            description: "Duplicates one GameObject ('target') or several ('targets'), preserving each one's "
                       + "parent. Revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                TargetsSchema + "}," +
                "\"additionalProperties\":false}",
            execute: ExecuteGameObjectDuplicate,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectDuplicate(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (!TryResolveTargets(args, out var targets, out var failure)) return Error(failure);

            var results = new JArray();
            foreach (var go in targets)
            {
                var duplicate = GameObjectEditingService.Duplicate(go);
                if (duplicate == null) return Error($"duplicate failed for '{GameObjectEditingService.GetHierarchyPath(go)}'.");
                results.Add(new JObject
                {
                    ["path"] = GameObjectEditingService.GetHierarchyPath(duplicate),
                    ["name"] = duplicate.name,
                    ["instanceId"] = duplicate.GetInstanceID()
                });
            }
            return BatchResult(results);
        }

        private static McpToolDefinition CreateGameObjectReparentTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_reparent",
            description: "Reparents one GameObject ('target') or several ('targets') under another GameObject, "
                       + "or to the scene root when 'newParent' is omitted. Revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                TargetsSchema + "," +
                "\"newParent\":{\"type\":\"string\",\"description\":\"New parent hierarchy path or instance id; omit for root.\"}," +
                "\"worldPositionStays\":{\"type\":\"boolean\",\"description\":\"Preserve world transform (default true).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteGameObjectReparent,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectReparent(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (!TryResolveTargets(args, out var targets, out var failure)) return Error(failure);

            GameObject newParent = null;
            var parentArg = args.Value<string>("newParent");
            if (!string.IsNullOrWhiteSpace(parentArg))
            {
                newParent = GameObjectEditingService.Resolve(parentArg, out var parentError);
                if (newParent == null) return Error(parentError);
            }

            var worldPositionStays = args.Value<bool?>("worldPositionStays") ?? true;
            var parentPath = newParent != null ? GameObjectEditingService.GetHierarchyPath(newParent) : null;
            var results = new JArray();
            foreach (var go in targets)
            {
                if (!GameObjectEditingService.Reparent(go, newParent, worldPositionStays, out var reparentError))
                    return Error(reparentError);
                results.Add(new JObject
                {
                    ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                    ["parent"] = parentPath
                });
            }
            return BatchResult(results);
        }

        private static McpToolDefinition CreateGameObjectAddComponentTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_add_component",
            description: "Adds a component of the given 'type' (class name or full name) to one GameObject "
                       + "('target') or several ('targets'). Revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                TargetsSchema + "," +
                "\"type\":{\"type\":\"string\",\"description\":\"Concrete Component type name (or full name).\"}}," +
                "\"required\":[\"type\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectAddComponent,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectAddComponent(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (!TryResolveTargets(args, out var targets, out var failure)) return Error(failure);

            var type = ResolveComponentType(args.Value<string>("type"), out var typeError);
            if (type == null) return Error(typeError);

            var results = new JArray();
            foreach (var go in targets)
            {
                var component = GameObjectEditingService.AddComponent(go, type, out var addError);
                results.Add(new JObject
                {
                    ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                    // Per-target outcome: a component may fail to add on some objects (e.g. a duplicate that
                    // disallows multiples) while succeeding on others — report each rather than aborting all.
                    ["component"] = component != null ? type.Name : null,
                    ["error"] = component == null ? addError : null
                });
            }
            return BatchResult(results);
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
