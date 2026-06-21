using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only Unity project-level inspection tools: tags, layers, sorting layers, build target /
    /// scripting backend info, and scene/build/graphics-relevant project settings.
    /// </summary>
    /// <remarks>Read-only; main thread only. Mutations to these settings are deferred to Wave 2 with strong guardrails.</remarks>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateTagsLayersTool() => new McpToolDefinition(
            name: "molca_unity_tags_layers",
            description: "Lists the project's tags, layers (index -> name, populated layers only), and sorting "
                       + "layers. Use before molca_unity_gameobject tag/layer mutations (Wave 2).",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteTagsLayers,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateProjectSettingsTool() => new McpToolDefinition(
            name: "molca_unity_project_settings",
            description: "Reports scene/build/graphics-relevant project settings: active build target and group, "
                       + "scripting backend, API compatibility level, color space, company/product name, default "
                       + "fixed timestep, and physics gravity. Read-only summary.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteProjectSettings,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateGameObjectSetTagTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_set_tag",
            description: "Sets the tag on a GameObject. The tag must already exist in the project (see "
                       + "molca_unity_tags_layers). Resolve by hierarchy path or instance id. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id.\"}," +
                "\"tag\":{\"type\":\"string\",\"description\":\"Existing tag name to assign.\"}}," +
                "\"required\":[\"target\",\"tag\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectSetTag,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreateGameObjectSetLayerTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_set_layer",
            description: "Sets the layer on a GameObject by layer name or index. The layer must exist (see "
                       + "molca_unity_tags_layers). Optional 'includeChildren' applies it to all descendants. "
                       + "Resolve by hierarchy path or instance id. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id.\"}," +
                "\"layer\":{\"type\":\"string\",\"description\":\"Layer name to assign.\"}," +
                "\"layerIndex\":{\"type\":\"integer\",\"description\":\"Layer index (0..31) to assign.\"}," +
                "\"includeChildren\":{\"type\":\"boolean\",\"description\":\"Apply to all descendants (default false).\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectSetLayer,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteGameObjectSetTag(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            var tag = args.Value<string>("tag");
            if (string.IsNullOrWhiteSpace(tag)) return Error("'tag' is required.");
            if (System.Array.IndexOf(UnityEditorInternal.InternalEditorUtility.tags, tag) < 0)
                return Error($"tag '{tag}' does not exist; create it in Tags & Layers first.");

            Undo.RecordObject(go, $"MCP Set Tag {go.name}");
            go.tag = tag;
            EditorUtility.SetDirty(go);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["tag"] = go.tag
            }.ToString(Formatting.None);
        }

        private static string ExecuteGameObjectSetLayer(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            int layer;
            var layerName = args.Value<string>("layer");
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                layer = LayerMask.NameToLayer(layerName);
                if (layer < 0) return Error($"layer '{layerName}' does not exist.");
            }
            else if (args["layerIndex"] != null)
            {
                layer = args.Value<int>("layerIndex");
                if (layer < 0 || layer > 31) return Error("'layerIndex' must be 0..31.");
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer)))
                    return Error($"layer index {layer} is not defined.");
            }
            else
            {
                return Error("pass 'layer' (name) or 'layerIndex'.");
            }

            var includeChildren = args.Value<bool?>("includeChildren") == true;
            var targets = includeChildren
                ? go.GetComponentsInChildren<Transform>(true)
                : new[] { go.transform };
            Undo.RecordObjects(System.Array.ConvertAll(targets, t => (Object)t.gameObject), $"MCP Set Layer {go.name}");
            foreach (var t in targets) t.gameObject.layer = layer;
            EditorUtility.SetDirty(go);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["layer"] = LayerMask.LayerToName(layer),
                ["layerIndex"] = layer,
                ["appliedToCount"] = targets.Length
            }.ToString(Formatting.None);
        }

        private static string ExecuteTagsLayers(string argumentsJson)
        {
            var tags = new JArray(UnityEditorInternal.InternalEditorUtility.tags);

            var layers = new JArray();
            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name)) continue;
                layers.Add(new JObject { ["index"] = i, ["name"] = name });
            }

            var sortingLayers = new JArray();
            foreach (var layer in SortingLayer.layers)
                sortingLayers.Add(new JObject { ["id"] = layer.id, ["name"] = layer.name, ["value"] = layer.value });

            return new JObject
            {
                ["tags"] = tags,
                ["layers"] = layers,
                ["sortingLayers"] = sortingLayers
            }.ToString(Formatting.None);
        }

        private static string ExecuteProjectSettings(string argumentsJson)
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var group = BuildPipeline.GetBuildTargetGroup(target);
            var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group);

            return new JObject
            {
                ["companyName"] = PlayerSettings.companyName,
                ["productName"] = PlayerSettings.productName,
                ["activeBuildTarget"] = target.ToString(),
                ["buildTargetGroup"] = group.ToString(),
                ["scriptingBackend"] = PlayerSettings.GetScriptingBackend(namedTarget).ToString(),
                ["apiCompatibilityLevel"] = PlayerSettings.GetApiCompatibilityLevel(namedTarget).ToString(),
                ["colorSpace"] = PlayerSettings.colorSpace.ToString(),
                ["fixedTimestep"] = Time.fixedDeltaTime,
                ["gravity"] = Vector3ToJson(Physics.gravity),
                ["developmentBuild"] = EditorUserBuildSettings.development
            }.ToString(Formatting.None);
        }
    }
}
