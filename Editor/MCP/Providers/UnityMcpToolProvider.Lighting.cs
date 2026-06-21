using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only Unity lighting and camera discovery tools. Lists cameras, lights, and reflection/light
    /// probes across loaded scenes so the assistant can inspect lighting setup before any mutation tool.
    /// </summary>
    /// <remarks>Read-only; main thread only (driven by the MCP bridge).</remarks>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateCamerasTool() => new McpToolDefinition(
            name: "molca_unity_cameras",
            description: "Lists Camera components in the loaded scene(s) with hierarchy path, enabled state, "
                       + "depth, field of view / orthographic size, projection, clear flags, culling mask, "
                       + "target texture, and whether it is Camera.main.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteCameras,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateLightsTool() => new McpToolDefinition(
            name: "molca_unity_lights",
            description: "Lists Light components in the loaded scene(s) with hierarchy path, type, enabled state, "
                       + "intensity, color, range, spot angle, shadow type, bake/light mode, and culling mask.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteLights,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateProbesTool() => new McpToolDefinition(
            name: "molca_unity_probes",
            description: "Lists ReflectionProbe and LightProbeGroup components in the loaded scene(s) with "
                       + "hierarchy path and key settings (mode, resolution, bounds, probe count).",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteProbes,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateCameraSetTool() => new McpToolDefinition(
            name: "molca_unity_camera_set",
            description: "Sets Camera properties on a GameObject: any of enabled, depth, fieldOfView, "
                       + "orthographic, orthographicSize, nearClip, farClip, clearFlags. Resolve by hierarchy "
                       + "path or instance id. Only provided fields are changed. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id with a Camera.\"}," +
                "\"enabled\":{\"type\":\"boolean\"},\"depth\":{\"type\":\"number\"}," +
                "\"fieldOfView\":{\"type\":\"number\"},\"orthographic\":{\"type\":\"boolean\"}," +
                "\"orthographicSize\":{\"type\":\"number\"},\"nearClip\":{\"type\":\"number\"},\"farClip\":{\"type\":\"number\"}," +
                "\"clearFlags\":{\"type\":\"string\",\"description\":\"Skybox, SolidColor, Depth, or Nothing.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteCameraSet,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreateLightSetTool() => new McpToolDefinition(
            name: "molca_unity_light_set",
            description: "Sets Light properties on a GameObject: any of enabled, intensity, color (hex or rgba), "
                       + "range, spotAngle, type. Resolve by hierarchy path or instance id. Only provided fields "
                       + "are changed. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id with a Light.\"}," +
                "\"enabled\":{\"type\":\"boolean\"},\"intensity\":{\"type\":\"number\"}," +
                "\"hex\":{\"type\":\"string\",\"description\":\"Color as #RRGGBB or #RRGGBBAA.\"}," +
                "\"rgba\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":4}," +
                "\"range\":{\"type\":\"number\"},\"spotAngle\":{\"type\":\"number\"}," +
                "\"type\":{\"type\":\"string\",\"description\":\"Directional, Point, Spot, or Area.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteLightSet,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteCameraSet(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            var camera = go.GetComponent<Camera>();
            if (camera == null) return Error($"GameObject '{GameObjectEditingService.GetHierarchyPath(go)}' has no Camera.");

            Undo.RecordObject(camera, $"MCP Set Camera {camera.name}");
            if (args["enabled"] != null) camera.enabled = args.Value<bool>("enabled");
            if (args["depth"] != null) camera.depth = args.Value<float>("depth");
            if (args["fieldOfView"] != null) camera.fieldOfView = args.Value<float>("fieldOfView");
            if (args["orthographic"] != null) camera.orthographic = args.Value<bool>("orthographic");
            if (args["orthographicSize"] != null) camera.orthographicSize = args.Value<float>("orthographicSize");
            if (args["nearClip"] != null) camera.nearClipPlane = args.Value<float>("nearClip");
            if (args["farClip"] != null) camera.farClipPlane = args.Value<float>("farClip");
            var clearFlags = args.Value<string>("clearFlags");
            if (!string.IsNullOrWhiteSpace(clearFlags))
            {
                if (!System.Enum.TryParse<CameraClearFlags>(clearFlags, true, out var flags))
                    return Error("'clearFlags' must be Skybox, SolidColor, Depth, or Nothing.");
                camera.clearFlags = flags;
            }
            EditorUtility.SetDirty(camera);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(camera.gameObject),
                ["enabled"] = camera.enabled,
                ["depth"] = camera.depth,
                ["fieldOfView"] = camera.fieldOfView,
                ["orthographic"] = camera.orthographic,
                ["clearFlags"] = camera.clearFlags.ToString()
            }.ToString(Formatting.None);
        }

        private static string ExecuteLightSet(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            var light = go.GetComponent<Light>();
            if (light == null) return Error($"GameObject '{GameObjectEditingService.GetHierarchyPath(go)}' has no Light.");

            Undo.RecordObject(light, $"MCP Set Light {light.name}");
            if (args["enabled"] != null) light.enabled = args.Value<bool>("enabled");
            if (args["intensity"] != null) light.intensity = args.Value<float>("intensity");
            if (args["range"] != null) light.range = args.Value<float>("range");
            if (args["spotAngle"] != null) light.spotAngle = args.Value<float>("spotAngle");
            var color = ReadColor(args, out var colorError);
            if (color.HasValue) light.color = color.Value;
            else if (args["hex"] != null || args["rgba"] != null) return Error(colorError);
            var typeArg = args.Value<string>("type");
            if (!string.IsNullOrWhiteSpace(typeArg))
            {
                if (!System.Enum.TryParse<LightType>(typeArg, true, out var lightType))
                    return Error("'type' must be Directional, Point, Spot, or Area.");
                light.type = lightType;
            }
            EditorUtility.SetDirty(light);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(light.gameObject),
                ["type"] = light.type.ToString(),
                ["enabled"] = light.enabled,
                ["intensity"] = light.intensity,
                ["color"] = "#" + ColorUtility.ToHtmlStringRGBA(light.color)
            }.ToString(Formatting.None);
        }

        private static string ExecuteCameras(string argumentsJson)
        {
            var main = Camera.main;
            var cameras = new JArray();
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                {
                    cameras.Add(new JObject
                    {
                        ["path"] = GameObjectEditingService.GetHierarchyPath(camera.gameObject),
                        ["instanceId"] = camera.GetInstanceID(),
                        ["enabled"] = camera.enabled,
                        ["depth"] = camera.depth,
                        ["orthographic"] = camera.orthographic,
                        ["fieldOfView"] = camera.fieldOfView,
                        ["orthographicSize"] = camera.orthographicSize,
                        ["nearClip"] = camera.nearClipPlane,
                        ["farClip"] = camera.farClipPlane,
                        ["clearFlags"] = camera.clearFlags.ToString(),
                        ["cullingMask"] = camera.cullingMask,
                        ["targetTexture"] = camera.targetTexture != null ? camera.targetTexture.name : null,
                        ["isMain"] = camera == main
                    });
                }
            }

            return new JObject
            {
                ["count"] = cameras.Count,
                ["mainCamera"] = main != null ? GameObjectEditingService.GetHierarchyPath(main.gameObject) : null,
                ["cameras"] = cameras
            }.ToString(Formatting.None);
        }

        private static string ExecuteLights(string argumentsJson)
        {
            var lights = new JArray();
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                {
                    lights.Add(new JObject
                    {
                        ["path"] = GameObjectEditingService.GetHierarchyPath(light.gameObject),
                        ["instanceId"] = light.GetInstanceID(),
                        ["type"] = light.type.ToString(),
                        ["enabled"] = light.enabled,
                        ["intensity"] = light.intensity,
                        ["color"] = "#" + ColorUtility.ToHtmlStringRGBA(light.color),
                        ["range"] = light.range,
                        ["spotAngle"] = light.spotAngle,
                        ["shadows"] = light.shadows.ToString(),
                        ["lightmapBakeType"] = light.lightmapBakeType.ToString(),
                        ["cullingMask"] = light.cullingMask
                    });
                }
            }

            return new JObject
            {
                ["count"] = lights.Count,
                ["lights"] = lights
            }.ToString(Formatting.None);
        }

        private static string ExecuteProbes(string argumentsJson)
        {
            var reflectionProbes = new JArray();
            var lightProbeGroups = new JArray();
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                foreach (var probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                {
                    reflectionProbes.Add(new JObject
                    {
                        ["path"] = GameObjectEditingService.GetHierarchyPath(probe.gameObject),
                        ["instanceId"] = probe.GetInstanceID(),
                        ["enabled"] = probe.enabled,
                        ["mode"] = probe.mode.ToString(),
                        ["resolution"] = probe.resolution,
                        ["importance"] = probe.importance,
                        ["intensity"] = probe.intensity,
                        ["boxSize"] = Vector3ToJson(probe.size),
                        ["boxOffset"] = Vector3ToJson(probe.center)
                    });
                }

                foreach (var group in root.GetComponentsInChildren<LightProbeGroup>(true))
                {
                    lightProbeGroups.Add(new JObject
                    {
                        ["path"] = GameObjectEditingService.GetHierarchyPath(group.gameObject),
                        ["instanceId"] = group.GetInstanceID(),
                        ["enabled"] = group.enabled,
                        ["probeCount"] = group.probePositions != null ? group.probePositions.Length : 0
                    });
                }
            }

            return new JObject
            {
                ["reflectionProbeCount"] = reflectionProbes.Count,
                ["reflectionProbes"] = reflectionProbes,
                ["lightProbeGroupCount"] = lightProbeGroups.Count,
                ["lightProbeGroups"] = lightProbeGroups
            }.ToString(Formatting.None);
        }
    }
}
