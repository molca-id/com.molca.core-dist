using System;
using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only Unity physics inspection and diagnostic tools. Lists colliders and rigidbodies across
    /// loaded scenes and runs simple raycast/overlap queries against the active physics scene.
    /// </summary>
    /// <remarks>Read-only; main thread only.</remarks>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateCollidersTool() => new McpToolDefinition(
            name: "molca_unity_colliders",
            description: "Lists Collider components in the loaded scene(s) with hierarchy path, collider type, "
                       + "enabled state, isTrigger flag, physic material, bounds, and the attached Rigidbody path "
                       + "(if any). Optional 'limit' caps results (default 300).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Max entries to return (default 300).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteColliders,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateRigidbodiesTool() => new McpToolDefinition(
            name: "molca_unity_rigidbodies",
            description: "Lists Rigidbody components in the loaded scene(s) with hierarchy path, mass, "
                       + "isKinematic, useGravity, drag, constraints, interpolation, and collision detection mode.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteRigidbodies,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreatePhysicsQueryTool() => new McpToolDefinition(
            name: "molca_unity_physics_query",
            description: "Runs a physics diagnostic query against the active physics scene. 'kind' is one of "
                       + "'raycast' (origin + direction [+ maxDistance]), 'overlapSphere' (origin + radius), or "
                       + "'overlapBox' (origin + halfExtents). Returns hit GameObject paths, layers, distances, "
                       + "and points. Edit-mode results reflect static collider positions; run in Play for live "
                       + "results.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"kind\":{\"type\":\"string\",\"description\":\"raycast | overlapSphere | overlapBox.\"}," +
                "\"origin\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3,\"description\":\"World-space [x,y,z] origin/center.\"}," +
                "\"direction\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3,\"description\":\"Ray direction for raycast.\"}," +
                "\"maxDistance\":{\"type\":\"number\",\"description\":\"Max ray distance (default infinity).\"}," +
                "\"radius\":{\"type\":\"number\",\"description\":\"Sphere radius for overlapSphere.\"}," +
                "\"halfExtents\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3,\"description\":\"Box half-extents for overlapBox.\"}," +
                "\"layerMask\":{\"type\":\"integer\",\"description\":\"Layer mask (default all layers).\"}}," +
                "\"required\":[\"kind\",\"origin\"],\"additionalProperties\":false}",
            execute: ExecutePhysicsQuery,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateColliderSetTool() => new McpToolDefinition(
            name: "molca_unity_collider_set",
            description: "Sets Collider properties on a GameObject: any of enabled, isTrigger, materialPath "
                       + "(a PhysicMaterial asset path, empty string to clear). Resolve by hierarchy path or "
                       + "instance id. Only provided fields are changed. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id with a Collider.\"}," +
                "\"enabled\":{\"type\":\"boolean\"},\"isTrigger\":{\"type\":\"boolean\"}," +
                "\"materialPath\":{\"type\":\"string\",\"description\":\"PhysicMaterial asset path, or empty to clear.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteColliderSet,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreateRigidbodySetTool() => new McpToolDefinition(
            name: "molca_unity_rigidbody_set",
            description: "Sets Rigidbody properties on a GameObject: any of mass, isKinematic, useGravity, "
                       + "linearDamping, angularDamping. Resolve by hierarchy path or instance id. Only provided "
                       + "fields are changed. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id with a Rigidbody.\"}," +
                "\"mass\":{\"type\":\"number\"},\"isKinematic\":{\"type\":\"boolean\"},\"useGravity\":{\"type\":\"boolean\"}," +
                "\"linearDamping\":{\"type\":\"number\"},\"angularDamping\":{\"type\":\"number\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteRigidbodySet,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteColliderSet(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            var collider = go.GetComponent<Collider>();
            if (collider == null) return Error($"GameObject '{GameObjectEditingService.GetHierarchyPath(go)}' has no Collider.");

            Undo.RecordObject(collider, $"MCP Set Collider {collider.name}");
            if (args["enabled"] != null) collider.enabled = args.Value<bool>("enabled");
            if (args["isTrigger"] != null) collider.isTrigger = args.Value<bool>("isTrigger");
            var materialPath = args.Value<string>("materialPath");
            if (args["materialPath"] != null)
            {
                if (string.IsNullOrEmpty(materialPath))
                {
                    collider.sharedMaterial = null;
                }
                else
                {
                    var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(materialPath);
                    if (material == null) return Error($"no PhysicsMaterial found at '{materialPath}'.");
                    collider.sharedMaterial = material;
                }
            }
            EditorUtility.SetDirty(collider);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(collider.gameObject),
                ["type"] = collider.GetType().Name,
                ["enabled"] = collider.enabled,
                ["isTrigger"] = collider.isTrigger,
                ["material"] = collider.sharedMaterial != null ? collider.sharedMaterial.name : null
            }.ToString(Formatting.None);
        }

        private static string ExecuteRigidbodySet(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            var body = go.GetComponent<Rigidbody>();
            if (body == null) return Error($"GameObject '{GameObjectEditingService.GetHierarchyPath(go)}' has no Rigidbody.");

            Undo.RecordObject(body, $"MCP Set Rigidbody {body.name}");
            if (args["mass"] != null) body.mass = args.Value<float>("mass");
            if (args["isKinematic"] != null) body.isKinematic = args.Value<bool>("isKinematic");
            if (args["useGravity"] != null) body.useGravity = args.Value<bool>("useGravity");
            if (args["linearDamping"] != null) body.linearDamping = args.Value<float>("linearDamping");
            if (args["angularDamping"] != null) body.angularDamping = args.Value<float>("angularDamping");
            EditorUtility.SetDirty(body);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(body.gameObject),
                ["mass"] = body.mass,
                ["isKinematic"] = body.isKinematic,
                ["useGravity"] = body.useGravity,
                ["linearDamping"] = body.linearDamping,
                ["angularDamping"] = body.angularDamping
            }.ToString(Formatting.None);
        }

        private static string ExecuteColliders(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var limit = args["limit"] != null ? Math.Max(1, args.Value<int>("limit")) : 300;

            var entries = new JArray();
            var truncated = false;
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                if (truncated) break;
                foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                {
                    if (entries.Count >= limit) { truncated = true; break; }
                    entries.Add(new JObject
                    {
                        ["path"] = GameObjectEditingService.GetHierarchyPath(collider.gameObject),
                        ["instanceId"] = collider.GetInstanceID(),
                        ["type"] = collider.GetType().Name,
                        ["enabled"] = collider.enabled,
                        ["isTrigger"] = collider.isTrigger,
                        ["material"] = collider.sharedMaterial != null ? collider.sharedMaterial.name : null,
                        ["bounds"] = BoundsToJson(collider.bounds),
                        ["attachedRigidbody"] = collider.attachedRigidbody != null
                            ? GameObjectEditingService.GetHierarchyPath(collider.attachedRigidbody.gameObject)
                            : null
                    });
                }
            }

            return new JObject
            {
                ["count"] = entries.Count,
                ["truncated"] = truncated,
                ["colliders"] = entries
            }.ToString(Formatting.None);
        }

        private static string ExecuteRigidbodies(string argumentsJson)
        {
            var entries = new JArray();
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
                {
                    entries.Add(new JObject
                    {
                        ["path"] = GameObjectEditingService.GetHierarchyPath(body.gameObject),
                        ["instanceId"] = body.GetInstanceID(),
                        ["mass"] = body.mass,
                        ["isKinematic"] = body.isKinematic,
                        ["useGravity"] = body.useGravity,
                        ["linearDrag"] = body.linearDamping,
                        ["angularDrag"] = body.angularDamping,
                        ["constraints"] = body.constraints.ToString(),
                        ["interpolation"] = body.interpolation.ToString(),
                        ["collisionDetection"] = body.collisionDetectionMode.ToString()
                    });
                }
            }

            return new JObject
            {
                ["count"] = entries.Count,
                ["rigidbodies"] = entries
            }.ToString(Formatting.None);
        }

        private static string ExecutePhysicsQuery(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var kind = args.Value<string>("kind");
            var origin = ReadVector3(args["origin"]);
            if (origin == null) return Error("'origin' must be a [x,y,z] array.");
            var layerMask = args.Value<int?>("layerMask") ?? Physics.AllLayers;

            switch (kind)
            {
                case "raycast":
                {
                    var direction = ReadVector3(args["direction"]);
                    if (direction == null) return Error("'direction' must be a [x,y,z] array for a raycast.");
                    var maxDistance = args.Value<float?>("maxDistance") ?? Mathf.Infinity;
                    var hits = Physics.RaycastAll(origin.Value, direction.Value.normalized, maxDistance, layerMask);
                    var arr = new JArray();
                    foreach (var hit in hits)
                        arr.Add(new JObject
                        {
                            ["path"] = GameObjectEditingService.GetHierarchyPath(hit.collider.gameObject),
                            ["layer"] = LayerMask.LayerToName(hit.collider.gameObject.layer),
                            ["distance"] = hit.distance,
                            ["point"] = Vector3ToJson(hit.point)
                        });
                    return new JObject { ["kind"] = kind, ["hitCount"] = arr.Count, ["hits"] = arr }
                        .ToString(Formatting.None);
                }

                case "overlapSphere":
                {
                    var radius = args.Value<float?>("radius");
                    if (radius == null) return Error("'radius' is required for overlapSphere.");
                    return OverlapResult(kind, Physics.OverlapSphere(origin.Value, radius.Value, layerMask));
                }

                case "overlapBox":
                {
                    var halfExtents = ReadVector3(args["halfExtents"]);
                    if (halfExtents == null) return Error("'halfExtents' must be a [x,y,z] array for overlapBox.");
                    return OverlapResult(kind, Physics.OverlapBox(origin.Value, halfExtents.Value, Quaternion.identity, layerMask));
                }

                default:
                    return Error("'kind' must be raycast, overlapSphere, or overlapBox.");
            }
        }

        private static string OverlapResult(string kind, Collider[] colliders)
        {
            var arr = new JArray();
            foreach (var collider in colliders)
                arr.Add(new JObject
                {
                    ["path"] = GameObjectEditingService.GetHierarchyPath(collider.gameObject),
                    ["layer"] = LayerMask.LayerToName(collider.gameObject.layer),
                    ["type"] = collider.GetType().Name
                });
            return new JObject { ["kind"] = kind, ["hitCount"] = arr.Count, ["hits"] = arr }
                .ToString(Formatting.None);
        }
    }
}
