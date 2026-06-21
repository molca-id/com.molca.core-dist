using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Unity asset importer inspection (read-only) and import-setting mutation tools. Reads texture /
    /// model / audio importer settings and applies bounded changes followed by a reimport.
    /// </summary>
    /// <remarks>
    /// Import-setting mutations trigger an asset reimport, which is not Unity-Undo reversible — the
    /// import-set tools are tagged <see cref="McpToolReversibility.FileSnapshot"/> (the .meta file is
    /// backed up so the importer settings can be reverted). Main thread only.
    /// </remarks>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateImporterInspectTool() => new McpToolDefinition(
            name: "molca_unity_importer_inspect",
            description: "Inspects the AssetImporter settings for an asset path. Reports the importer type and "
                       + "the common settings for TextureImporter (type, max size, compression, sRGB) and "
                       + "ModelImporter (global scale, import animation/materials/blendshapes).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path to inspect.\"}}," +
                "\"required\":[\"path\"],\"additionalProperties\":false}",
            execute: ExecuteImporterInspect,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateTextureImportSetTool() => new McpToolDefinition(
            name: "molca_unity_texture_import_set",
            description: "Sets TextureImporter settings on a texture asset: any of textureType (e.g. Default, "
                       + "Sprite, NormalMap), maxTextureSize, textureCompression (e.g. Uncompressed, Compressed, "
                       + "LZMA), sRGB. Applies the change and reimports the asset (not Unity-Undo reversible; the "
                       + ".meta is snapshotted for revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Texture asset path.\"}," +
                "\"textureType\":{\"type\":\"string\",\"description\":\"TextureImporterType name, e.g. Default or NormalMap.\"}," +
                "\"maxTextureSize\":{\"type\":\"integer\",\"description\":\"Max texture size (power of two, e.g. 1024).\"}," +
                "\"textureCompression\":{\"type\":\"string\",\"description\":\"TextureImporterCompression name.\"}," +
                "\"sRGB\":{\"type\":\"boolean\",\"description\":\"Treat as sRGB (color) texture.\"}}," +
                "\"required\":[\"path\"],\"additionalProperties\":false}",
            execute: ExecuteTextureImportSet,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.FileSnapshot);

        private static McpToolDefinition CreateModelImportSetTool() => new McpToolDefinition(
            name: "molca_unity_model_import_set",
            description: "Sets ModelImporter settings on a model asset: any of globalScale, importAnimation, "
                       + "importMaterials, importBlendShapes. Applies the change and reimports the asset (not "
                       + "Unity-Undo reversible; the .meta is snapshotted for revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Model asset path (e.g. an FBX).\"}," +
                "\"globalScale\":{\"type\":\"number\"},\"importAnimation\":{\"type\":\"boolean\"}," +
                "\"importMaterials\":{\"type\":\"boolean\"},\"importBlendShapes\":{\"type\":\"boolean\"}}," +
                "\"required\":[\"path\"],\"additionalProperties\":false}",
            execute: ExecuteModelImportSet,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.FileSnapshot);

        private static McpToolDefinition CreateReimportAssetTool() => new McpToolDefinition(
            name: "molca_unity_reimport_asset",
            description: "Forces a reimport of an asset at the given path. Reimport is not reversible.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path to reimport.\"}}," +
                "\"required\":[\"path\"],\"additionalProperties\":false}",
            execute: ExecuteReimportAsset,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteImporterInspect(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) return Error($"no importer for asset at '{path}'.");

            var result = new JObject
            {
                ["path"] = path,
                ["importerType"] = importer.GetType().Name
            };

            if (importer is TextureImporter texture)
            {
                result["texture"] = new JObject
                {
                    ["textureType"] = texture.textureType.ToString(),
                    ["maxTextureSize"] = texture.maxTextureSize,
                    ["textureCompression"] = texture.textureCompression.ToString(),
                    ["sRGB"] = texture.sRGBTexture,
                    ["mipmapEnabled"] = texture.mipmapEnabled
                };
            }
            else if (importer is ModelImporter model)
            {
                result["model"] = new JObject
                {
                    ["globalScale"] = model.globalScale,
                    ["importAnimation"] = model.importAnimation,
                    ["importMaterials"] = model.materialImportMode != ModelImporterMaterialImportMode.None,
                    ["importBlendShapes"] = model.importBlendShapes
                };
            }

            return result.ToString(Formatting.None);
        }

        private static string ExecuteTextureImportSet(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
                return Error($"asset at '{path}' is not a texture.");

            var undoId = McpUndoStack.Snapshot(path + ".meta", "molca_unity_texture_import_set",
                $"Texture import settings ({System.IO.Path.GetFileName(path)})");

            var textureType = args.Value<string>("textureType");
            if (!string.IsNullOrWhiteSpace(textureType))
            {
                if (!Enum.TryParse<TextureImporterType>(textureType, true, out var t))
                    return Error($"'{textureType}' is not a valid TextureImporterType.");
                importer.textureType = t;
            }
            if (args["maxTextureSize"] != null) importer.maxTextureSize = args.Value<int>("maxTextureSize");
            var compression = args.Value<string>("textureCompression");
            if (!string.IsNullOrWhiteSpace(compression))
            {
                if (!Enum.TryParse<TextureImporterCompression>(compression, true, out var c))
                    return Error($"'{compression}' is not a valid TextureImporterCompression.");
                importer.textureCompression = c;
            }
            if (args["sRGB"] != null) importer.sRGBTexture = args.Value<bool>("sRGB");

            importer.SaveAndReimport();

            return new JObject
            {
                ["path"] = path,
                ["textureType"] = importer.textureType.ToString(),
                ["maxTextureSize"] = importer.maxTextureSize,
                ["textureCompression"] = importer.textureCompression.ToString(),
                ["sRGB"] = importer.sRGBTexture,
                ["revertible"] = undoId != null
            }.ToString(Formatting.None);
        }

        private static string ExecuteModelImportSet(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
                return Error($"asset at '{path}' is not a model.");

            var undoId = McpUndoStack.Snapshot(path + ".meta", "molca_unity_model_import_set",
                $"Model import settings ({System.IO.Path.GetFileName(path)})");

            if (args["globalScale"] != null) importer.globalScale = args.Value<float>("globalScale");
            if (args["importAnimation"] != null) importer.importAnimation = args.Value<bool>("importAnimation");
            if (args["importBlendShapes"] != null) importer.importBlendShapes = args.Value<bool>("importBlendShapes");
            if (args["importMaterials"] != null)
                importer.materialImportMode = args.Value<bool>("importMaterials")
                    ? ModelImporterMaterialImportMode.ImportStandard
                    : ModelImporterMaterialImportMode.None;

            importer.SaveAndReimport();

            return new JObject
            {
                ["path"] = path,
                ["globalScale"] = importer.globalScale,
                ["importAnimation"] = importer.importAnimation,
                ["importMaterials"] = importer.materialImportMode != ModelImporterMaterialImportMode.None,
                ["importBlendShapes"] = importer.importBlendShapes,
                ["revertible"] = undoId != null
            }.ToString(Formatting.None);
        }

        private static string ExecuteReimportAsset(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            if (AssetImporter.GetAtPath(path) == null) return Error($"no importer for asset at '{path}'.");

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return new JObject { ["path"] = path, ["reimported"] = true }.ToString(Formatting.None);
        }
    }
}
