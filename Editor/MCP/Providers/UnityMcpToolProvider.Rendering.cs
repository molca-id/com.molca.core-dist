using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only Unity rendering and quality inspection tools: the active render pipeline asset, quality
    /// levels, and graphics capability summary. Useful context for shader/material authoring decisions.
    /// </summary>
    /// <remarks>Read-only; main thread only.</remarks>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateRenderPipelineTool() => new McpToolDefinition(
            name: "molca_unity_render_pipeline",
            description: "Reports the active render pipeline: whether a Scriptable Render Pipeline (URP/HDRP) is "
                       + "assigned, the pipeline asset's name/type/path, and the quality-level override pipeline "
                       + "asset if one is set. Reports 'Built-in' when no SRP asset is assigned.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteRenderPipeline,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateQualitySettingsTool() => new McpToolDefinition(
            name: "molca_unity_quality_settings",
            description: "Lists the project's quality levels with the current level index and per-level settings: "
                       + "vSync count, anisotropic filtering, LOD bias, shadow distance, and the SRP asset "
                       + "assigned to that level (if any).",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteQualitySettings,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateGraphicsCapabilitiesTool() => new McpToolDefinition(
            name: "molca_unity_graphics_capabilities",
            description: "Reports graphics device capabilities relevant to shader/material authoring: graphics "
                       + "device type and name, max texture size, supported shadow/compute/instancing features, "
                       + "and HDR display support.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteGraphicsCapabilities,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteRenderPipeline(string argumentsJson)
        {
            var current = GraphicsSettings.currentRenderPipeline;
            var defaultPipeline = GraphicsSettings.defaultRenderPipeline;
            var qualityOverride = QualitySettings.renderPipeline;

            return new JObject
            {
                ["isScriptableRenderPipeline"] = current != null,
                ["pipelineName"] = current != null ? current.name : "Built-in",
                ["pipelineType"] = current != null ? current.GetType().FullName : null,
                ["defaultPipelineAsset"] = PipelineAssetInfo(defaultPipeline),
                ["qualityLevelOverride"] = PipelineAssetInfo(qualityOverride)
            }.ToString(Formatting.None);
        }

        private static string ExecuteQualitySettings(string argumentsJson)
        {
            var names = QualitySettings.names;
            var currentLevel = QualitySettings.GetQualityLevel();
            var levels = new JArray();
            for (var i = 0; i < names.Length; i++)
            {
                var pipeline = QualitySettings.GetRenderPipelineAssetAt(i);
                levels.Add(new JObject
                {
                    ["index"] = i,
                    ["name"] = names[i],
                    ["isCurrent"] = i == currentLevel,
                    ["renderPipelineAsset"] = pipeline != null ? pipeline.name : null
                });
            }

            return new JObject
            {
                ["currentLevel"] = currentLevel,
                ["vSyncCount"] = QualitySettings.vSyncCount,
                ["anisotropicFiltering"] = QualitySettings.anisotropicFiltering.ToString(),
                ["lodBias"] = QualitySettings.lodBias,
                ["shadowDistance"] = QualitySettings.shadowDistance,
                ["levelCount"] = levels.Count,
                ["levels"] = levels
            }.ToString(Formatting.None);
        }

        private static string ExecuteGraphicsCapabilities(string argumentsJson)
        {
            return new JObject
            {
                ["graphicsDeviceType"] = SystemInfo.graphicsDeviceType.ToString(),
                ["graphicsDeviceName"] = SystemInfo.graphicsDeviceName,
                ["graphicsShaderLevel"] = SystemInfo.graphicsShaderLevel,
                ["maxTextureSize"] = SystemInfo.maxTextureSize,
                ["supportsComputeShaders"] = SystemInfo.supportsComputeShaders,
                ["supportsInstancing"] = SystemInfo.supportsInstancing,
                ["supportsShadows"] = SystemInfo.supportsShadows,
                ["supportsRayTracing"] = SystemInfo.supportsRayTracing,
                ["hdrDisplaySupported"] = SystemInfo.hdrDisplaySupportFlags != HDRDisplaySupportFlags.None
            }.ToString(Formatting.None);
        }

        private static JToken PipelineAssetInfo(RenderPipelineAsset asset)
        {
            if (asset == null) return JValue.CreateNull();
            var path = AssetDatabase.GetAssetPath(asset);
            return new JObject
            {
                ["name"] = asset.name,
                ["type"] = asset.GetType().FullName,
                ["assetPath"] = string.IsNullOrEmpty(path) ? null : path
            };
        }
    }
}
