using System;
using System.Linq;
using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only Unity renderer discovery and inspection tools. Lists renderers across loaded
    /// scenes and reports their assigned materials, shaders, and texture slots so the assistant
    /// can locate which scene objects use a given material before any mutation tool runs.
    /// </summary>
    /// <remarks>
    /// Pairs with the material inspection tools in <c>UnityMcpToolProvider.Materials.cs</c>.
    /// Read-only; main thread only (driven by the MCP bridge).
    /// </remarks>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateRenderersTool() => new McpToolDefinition(
            name: "molca_unity_renderers",
            description: "Lists Renderer components in the loaded scene(s) with hierarchy path, renderer type, "
                       + "enabled state, bounds, sorting layer/order, and shared material slot names. Optional "
                       + "'nameContains' filters by GameObject name substring, 'type' filters by renderer type "
                       + "name (e.g. MeshRenderer, SkinnedMeshRenderer), and 'limit' caps results (default 200).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"nameContains\":{\"type\":\"string\",\"description\":\"Case-insensitive GameObject name substring filter.\"}," +
                "\"type\":{\"type\":\"string\",\"description\":\"Renderer type name filter, e.g. MeshRenderer or SkinnedMeshRenderer.\"}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Max entries to return (default 200).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteRenderers,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateRendererTool() => new McpToolDefinition(
            name: "molca_unity_renderer",
            description: "Inspects one Renderer in detail: every material slot with the assigned material's name, "
                       + "instanceId, globalObjectId, shader, and texture slots (texture property name -> assigned "
                       + "texture asset path). Resolve by GameObject hierarchy path or instance id.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id carrying the renderer.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteRenderer,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateRendererSetEnabledTool() => new McpToolDefinition(
            name: "molca_unity_renderer_set_enabled",
            description: "Enables or disables a Renderer component on a GameObject. Resolve by hierarchy path or "
                       + "instance id. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id.\"}," +
                "\"enabled\":{\"type\":\"boolean\",\"description\":\"Desired enabled state.\"}}," +
                "\"required\":[\"target\",\"enabled\"],\"additionalProperties\":false}",
            execute: ExecuteRendererSetEnabled,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreateRendererSetMaterialTool() => new McpToolDefinition(
            name: "molca_unity_renderer_set_material",
            description: "Assigns a material asset to a Renderer's shared material slot. Resolve the renderer by "
                       + "GameObject hierarchy path or instance id, choose 'slot' (default 0), and pass either "
                       + "'materialPath' (a .mat asset path) or 'materialInstanceId'. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id carrying the renderer.\"}," +
                "\"slot\":{\"type\":\"integer\",\"description\":\"Material slot index (default 0).\"}," +
                "\"materialPath\":{\"type\":\"string\",\"description\":\"Material asset path to assign.\"}," +
                "\"materialInstanceId\":{\"type\":\"integer\",\"description\":\"Material instance id to assign.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteRendererSetMaterial,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteRendererSetEnabled(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var renderer = ResolveRenderer(args, out var error);
            if (renderer == null) return Error(error);
            if (args["enabled"] == null) return Error("'enabled' is required.");

            var enabled = args.Value<bool>("enabled");
            Undo.RecordObject(renderer, $"MCP Set Renderer Enabled {renderer.name}");
            renderer.enabled = enabled;
            EditorUtility.SetDirty(renderer);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(renderer.gameObject),
                ["type"] = renderer.GetType().Name,
                ["enabled"] = renderer.enabled
            }.ToString(Formatting.None);
        }

        private static string ExecuteRendererSetMaterial(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var renderer = ResolveRenderer(args, out var error);
            if (renderer == null) return Error(error);

            var slot = args.Value<int?>("slot") ?? 0;
            var materials = renderer.sharedMaterials;
            if (slot < 0 || slot >= materials.Length)
                return Error($"slot {slot} is out of range (0..{materials.Length - 1}).");

            Material material;
            var materialPath = args.Value<string>("materialPath");
            var materialInstanceId = args.Value<int?>("materialInstanceId");
            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null) return Error($"no Material asset found at '{materialPath}'.");
            }
            else if (materialInstanceId.HasValue)
            {
                material = EditorUtility.EntityIdToObject(materialInstanceId.Value) as Material;
                if (material == null) return Error($"instance id {materialInstanceId.Value} is not a Material.");
            }
            else
            {
                return Error("pass 'materialPath' or 'materialInstanceId'.");
            }

            Undo.RecordObject(renderer, $"MCP Set Renderer Material {renderer.name}[{slot}]");
            materials[slot] = material;
            renderer.sharedMaterials = materials;
            EditorUtility.SetDirty(renderer);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(renderer.gameObject),
                ["slot"] = slot,
                ["material"] = material.name,
                ["materialAssetPath"] = AssetDatabase.GetAssetPath(material)
            }.ToString(Formatting.None);
        }

        /// <summary>Resolves the Renderer on the GameObject identified by the 'target' argument.</summary>
        private static Renderer ResolveRenderer(JObject args, out string error)
        {
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out error);
            if (go == null) return null;
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                error = $"GameObject '{GameObjectEditingService.GetHierarchyPath(go)}' has no Renderer component.";
            return renderer;
        }

        private static string ExecuteRenderers(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var filter = args.Value<string>("nameContains");
            var typeFilter = args.Value<string>("type");
            var limit = args["limit"] != null ? Math.Max(1, args.Value<int>("limit")) : 200;

            var entries = new JArray();
            var truncated = false;
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                if (truncated) break;
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var go = renderer.gameObject;
                    if (!string.IsNullOrEmpty(filter) &&
                        go.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (!string.IsNullOrEmpty(typeFilter) &&
                        renderer.GetType().Name.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (entries.Count >= limit) { truncated = true; break; }

                    var materials = new JArray();
                    foreach (var material in renderer.sharedMaterials)
                    {
                        materials.Add(material != null
                            ? new JObject
                            {
                                ["name"] = material.name,
                                ["instanceId"] = material.GetInstanceID(),
                                ["shader"] = material.shader != null ? material.shader.name : null
                            }
                            : (JToken)JValue.CreateNull());
                    }

                    entries.Add(new JObject
                    {
                        ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                        ["type"] = renderer.GetType().Name,
                        ["enabled"] = renderer.enabled,
                        ["instanceId"] = renderer.GetInstanceID(),
                        ["sortingLayer"] = renderer.sortingLayerName,
                        ["sortingOrder"] = renderer.sortingOrder,
                        ["bounds"] = BoundsToJson(renderer.bounds),
                        ["materialCount"] = materials.Count,
                        ["materials"] = materials
                    });
                }
            }

            return new JObject
            {
                ["count"] = entries.Count,
                ["truncated"] = truncated,
                ["renderers"] = entries
            }.ToString(Formatting.None);
        }

        private static string ExecuteRenderer(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return Error($"GameObject '{GameObjectEditingService.GetHierarchyPath(go)}' has no Renderer component.");

            var slots = new JArray();
            var shared = renderer.sharedMaterials;
            for (var i = 0; i < shared.Length; i++)
            {
                var material = shared[i];
                if (material == null)
                {
                    slots.Add(new JObject { ["slot"] = i, ["material"] = JValue.CreateNull() });
                    continue;
                }

                var assetPath = AssetDatabase.GetAssetPath(material);
                slots.Add(new JObject
                {
                    ["slot"] = i,
                    ["name"] = material.name,
                    ["instanceId"] = material.GetInstanceID(),
                    ["globalObjectId"] = GlobalObjectId.GetGlobalObjectIdSlow(material).ToString(),
                    ["assetPath"] = string.IsNullOrEmpty(assetPath) ? null : assetPath,
                    ["shader"] = material.shader != null ? material.shader.name : null,
                    ["textures"] = EnumerateTextureSlots(material)
                });
            }

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["type"] = renderer.GetType().Name,
                ["enabled"] = renderer.enabled,
                ["instanceId"] = renderer.GetInstanceID(),
                ["castShadows"] = renderer.shadowCastingMode.ToString(),
                ["receiveShadows"] = renderer.receiveShadows,
                ["sortingLayer"] = renderer.sortingLayerName,
                ["sortingOrder"] = renderer.sortingOrder,
                ["lightmapIndex"] = renderer.lightmapIndex,
                ["bounds"] = BoundsToJson(renderer.bounds),
                ["materialCount"] = slots.Count,
                ["materials"] = slots
            }.ToString(Formatting.None);
        }

        /// <summary>
        /// Enumerates a material's texture shader properties and the asset path of each assigned texture.
        /// </summary>
        private static JArray EnumerateTextureSlots(Material material)
        {
            var textures = new JArray();
            var shader = material.shader;
            if (shader == null) return textures;

            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var name = shader.GetPropertyName(i);
                if (string.IsNullOrEmpty(name) || !material.HasProperty(name)) continue;

                var texture = material.GetTexture(name);
                textures.Add(new JObject
                {
                    ["property"] = name,
                    ["texture"] = texture != null ? texture.name : null,
                    ["assetPath"] = texture != null ? AssetDatabase.GetAssetPath(texture) : null
                });
            }

            return textures;
        }

        private static JObject BoundsToJson(Bounds bounds) => new JObject
        {
            ["center"] = Vector3ToJson(bounds.center),
            ["size"] = Vector3ToJson(bounds.size)
        };
    }
}
