using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only Unity material and shader property inspection tools.
    /// </summary>
    public sealed partial class UnityMcpToolProvider
    {
        private static readonly string[] CommonColorProperties =
        {
            "_BaseColor",
            "_Color",
            "_TintColor",
            "_EmissionColor",
            "_SpecColor"
        };

        private static McpToolDefinition CreateMaterialTool() => new McpToolDefinition(
            name: "molca_unity_material",
            description: "Inspects a Unity Material, including selected material sub-assets inside FBX/model "
                       + "assets. Pass instanceId/globalObjectId/name/path, or omit arguments to inspect the "
                       + "active selected Material. Returns shader, asset/sub-asset metadata, and color properties.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"instanceId\":{\"type\":\"integer\",\"description\":\"Material instance id from molca_unity_selection.\"}," +
                "\"globalObjectId\":{\"type\":\"string\",\"description\":\"GlobalObjectId from molca_unity_selection for sub-assets.\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"Material name. Searches selected objects first, then project assets/sub-assets.\"}," +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path containing the material, e.g. an FBX or .mat file.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteMaterial,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateMaterialSetColorTool() => new McpToolDefinition(
            name: "molca_unity_material_set_color",
            description: "Sets a color property on a Unity Material, including material sub-assets inside "
                       + "FBX/model assets. Resolve the material with instanceId/globalObjectId/name/path, "
                       + "choose a property such as _BaseColor or _Color from molca_unity_material, and pass "
                       + "either hex (#RRGGBB or #RRGGBBAA) or rgba [r,g,b,a] values. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"instanceId\":{\"type\":\"integer\",\"description\":\"Material instance id from molca_unity_selection or molca_unity_material.\"}," +
                "\"globalObjectId\":{\"type\":\"string\",\"description\":\"GlobalObjectId from molca_unity_selection or molca_unity_material.\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"Material name. Searches selected objects first, then project assets/sub-assets.\"}," +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path containing the material, e.g. an FBX or .mat file.\"}," +
                "\"property\":{\"type\":\"string\",\"description\":\"Color property name to set, e.g. _BaseColor or _Color.\"}," +
                "\"hex\":{\"type\":\"string\",\"description\":\"Color as #RRGGBB, RRGGBB, #RRGGBBAA, or RRGGBBAA.\"}," +
                "\"rgba\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":4,\"description\":\"Linear float color components, 0..1.\"}}," +
                "\"required\":[\"property\"],\"additionalProperties\":false}",
            execute: ExecuteMaterialSetColor,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteMaterial(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var material = ResolveMaterial(args, out var error);
            if (material == null) return Error(error ?? "no material matched the request.");

            var path = AssetDatabase.GetAssetPath(material);
            var shader = material.shader;
            var colors = new JArray();
            foreach (var propertyName in EnumerateColorProperties(material))
            {
                var color = material.GetColor(propertyName);
                colors.Add(new JObject
                {
                    ["name"] = propertyName,
                    ["value"] = ColorToObject(color),
                    ["hexRgb"] = ColorUtility.ToHtmlStringRGB(color),
                    ["hexRgba"] = ColorUtility.ToHtmlStringRGBA(color)
                });
            }

            var floats = new JArray();
            var vectors = new JArray();
            var textures = new JArray();
            EnumerateNonColorProperties(material, floats, vectors, textures);

            return new JObject
            {
                ["name"] = material.name,
                ["type"] = material.GetType().Name,
                ["instanceId"] = material.GetInstanceID(),
                ["globalObjectId"] = GlobalObjectId.GetGlobalObjectIdSlow(material).ToString(),
                ["assetPath"] = string.IsNullOrEmpty(path) ? null : path,
                ["assetGuid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
                ["isMainAsset"] = AssetDatabase.IsMainAsset(material),
                ["isSubAsset"] = AssetDatabase.IsSubAsset(material),
                ["shader"] = shader != null ? shader.name : null,
                ["renderQueue"] = material.renderQueue,
                ["colorPropertyCount"] = colors.Count,
                ["colors"] = colors,
                ["floats"] = floats,
                ["vectors"] = vectors,
                ["textures"] = textures
            }.ToString(Formatting.None);
        }

        private static string ExecuteMaterialSetColor(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var material = ResolveMaterial(args, out var error);
            if (material == null) return Error(error ?? "no material matched the request.");

            var property = args.Value<string>("property");
            if (string.IsNullOrWhiteSpace(property))
                return Error("'property' is required.");
            if (!material.HasProperty(property))
                return Error($"material '{material.name}' does not have color property '{property}'.");

            var color = ReadColor(args, out error);
            if (!color.HasValue) return Error(error);

            var before = material.GetColor(property);
            Undo.RecordObject(material, $"MCP Set Material Color {material.name}.{property}");
            material.SetColor(property, color.Value);
            EditorUtility.SetDirty(material);

            var path = AssetDatabase.GetAssetPath(material);
            if (!string.IsNullOrEmpty(path))
                AssetDatabase.SaveAssetIfDirty(material);

            return new JObject
            {
                ["name"] = material.name,
                ["instanceId"] = material.GetInstanceID(),
                ["globalObjectId"] = GlobalObjectId.GetGlobalObjectIdSlow(material).ToString(),
                ["assetPath"] = string.IsNullOrEmpty(path) ? null : path,
                ["assetGuid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
                ["isSubAsset"] = AssetDatabase.IsSubAsset(material),
                ["shader"] = material.shader != null ? material.shader.name : null,
                ["property"] = property,
                ["previous"] = ColorResult(before),
                ["current"] = ColorResult(color.Value)
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateMaterialSetPropertyTool() => new McpToolDefinition(
            name: "molca_unity_material_set_property",
            description: "Sets a non-color shader property on a Unity Material: a float/range ('float'), a vector "
                       + "('vector' as [x,y,z,w]), or a texture slot ('texturePath' to a texture asset, or empty "
                       + "string to clear). Resolve the material with instanceId/globalObjectId/name/path and pick "
                       + "a property from molca_unity_material. For color properties use molca_unity_material_set_color. "
                       + "One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"instanceId\":{\"type\":\"integer\",\"description\":\"Material instance id.\"}," +
                "\"globalObjectId\":{\"type\":\"string\",\"description\":\"GlobalObjectId for sub-assets.\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"Material name.\"}," +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path containing the material.\"}," +
                "\"property\":{\"type\":\"string\",\"description\":\"Shader property name, e.g. _Metallic or _MainTex.\"}," +
                "\"float\":{\"type\":\"number\",\"description\":\"Float/range value to set.\"}," +
                "\"vector\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":4,\"description\":\"Vector value [x,y,z,w].\"}," +
                "\"texturePath\":{\"type\":\"string\",\"description\":\"Texture asset path to assign, or empty string to clear.\"}}," +
                "\"required\":[\"property\"],\"additionalProperties\":false}",
            execute: ExecuteMaterialSetProperty,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreateMaterialCreateTool() => new McpToolDefinition(
            name: "molca_unity_material_create",
            description: "Creates a new Material asset from a named shader at a project path. 'shader' is a shader "
                       + "name (e.g. 'Universal Render Pipeline/Lit'); 'path' is the target .mat asset path. "
                       + "Creating a new asset is not Unity-Undo reversible; delete the asset to revert.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"shader\":{\"type\":\"string\",\"description\":\"Shader name to assign, e.g. Universal Render Pipeline/Lit.\"}," +
                "\"path\":{\"type\":\"string\",\"description\":\"Target asset path ending in .mat.\"}}," +
                "\"required\":[\"shader\",\"path\"],\"additionalProperties\":false}",
            execute: ExecuteMaterialCreate,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteMaterialSetProperty(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var material = ResolveMaterial(args, out var error);
            if (material == null) return Error(error ?? "no material matched the request.");

            var property = args.Value<string>("property");
            if (string.IsNullOrWhiteSpace(property))
                return Error("'property' is required.");
            if (!material.HasProperty(property))
                return Error($"material '{material.name}' does not have property '{property}'.");

            var hasFloat = args["float"] != null;
            var vector = args["vector"] as JArray;
            var hasTexture = args["texturePath"] != null;
            if ((hasFloat ? 1 : 0) + (vector != null ? 1 : 0) + (hasTexture ? 1 : 0) != 1)
                return Error("pass exactly one of 'float', 'vector', or 'texturePath'.");

            Undo.RecordObject(material, $"MCP Set Material Property {material.name}.{property}");
            var applied = new JObject { ["property"] = property };

            if (hasFloat)
            {
                material.SetFloat(property, args.Value<float>("float"));
                applied["float"] = material.GetFloat(property);
            }
            else if (vector != null)
            {
                var v = new Vector4(
                    vector.Count > 0 ? vector[0].Value<float>() : 0f,
                    vector.Count > 1 ? vector[1].Value<float>() : 0f,
                    vector.Count > 2 ? vector[2].Value<float>() : 0f,
                    vector.Count > 3 ? vector[3].Value<float>() : 0f);
                material.SetVector(property, v);
                applied["vector"] = new JArray { v.x, v.y, v.z, v.w };
            }
            else
            {
                var texturePath = args.Value<string>("texturePath");
                if (string.IsNullOrEmpty(texturePath))
                {
                    material.SetTexture(property, null);
                    applied["texturePath"] = null;
                }
                else
                {
                    var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                    if (texture == null) return Error($"no Texture asset found at '{texturePath}'.");
                    material.SetTexture(property, texture);
                    applied["texturePath"] = texturePath;
                }
            }

            EditorUtility.SetDirty(material);
            var path = AssetDatabase.GetAssetPath(material);
            if (!string.IsNullOrEmpty(path)) AssetDatabase.SaveAssetIfDirty(material);

            applied["name"] = material.name;
            applied["assetPath"] = string.IsNullOrEmpty(path) ? null : path;
            return applied.ToString(Formatting.None);
        }

        private static string ExecuteMaterialCreate(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var shaderName = args.Value<string>("shader");
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(shaderName)) return Error("'shader' is required.");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                return Error("'path' must end in .mat.");
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
                return Error($"an asset already exists at '{path}'.");

            var shader = Shader.Find(shaderName);
            if (shader == null) return Error($"no shader named '{shaderName}'.");

            var material = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();

            return new JObject
            {
                ["name"] = material.name,
                ["assetPath"] = path,
                ["assetGuid"] = AssetDatabase.AssetPathToGUID(path),
                ["shader"] = shader.name,
                ["instanceId"] = material.GetInstanceID()
            }.ToString(Formatting.None);
        }

        private static Material ResolveMaterial(JObject args, out string error)
        {
            error = null;

            var instanceId = args.Value<int?>("instanceId");
            if (instanceId.HasValue)
            {
                var obj = EditorUtility.EntityIdToObject(instanceId.Value);
                if (obj is Material material) return material;
                error = $"instanceId {instanceId.Value} is not a Material.";
                return null;
            }

            var globalObjectId = args.Value<string>("globalObjectId");
            if (!string.IsNullOrWhiteSpace(globalObjectId))
            {
                if (!GlobalObjectId.TryParse(globalObjectId, out var id))
                {
                    error = "globalObjectId is not valid.";
                    return null;
                }

                var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
                if (obj is Material material) return material;
                error = $"globalObjectId '{globalObjectId}' does not resolve to a Material.";
                return null;
            }

            var name = args.Value<string>("name");
            var path = args.Value<string>("path");
            var selected = FindMaterialInSelection(name);
            if (selected != null && (string.IsNullOrWhiteSpace(path)
                || string.Equals(AssetDatabase.GetAssetPath(selected), path, StringComparison.OrdinalIgnoreCase)))
                return selected;

            if (!string.IsNullOrWhiteSpace(path))
            {
                var foundAtPath = FindMaterialAtPath(path, name);
                if (foundAtPath != null) return foundAtPath;
                error = string.IsNullOrWhiteSpace(name)
                    ? $"no Material found at '{path}'."
                    : $"no Material named '{name}' found at '{path}'.";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                var foundByName = FindMaterialByName(name);
                if (foundByName != null) return foundByName;
                error = $"no Material named '{name}' found in the current selection or AssetDatabase.";
                return null;
            }

            error = "select a Material or pass instanceId, globalObjectId, name, or path.";
            return null;
        }

        private static Material FindMaterialInSelection(string name)
        {
            foreach (var obj in Selection.objects)
            {
                if (obj is not Material material) continue;
                if (string.IsNullOrWhiteSpace(name)
                    || string.Equals(material.name, name, StringComparison.OrdinalIgnoreCase))
                    return material;
            }
            return null;
        }

        private static Material FindMaterialByName(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets($"{name} t:Material"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var material = FindMaterialAtPath(path, name);
                if (material != null) return material;
            }

            foreach (var guid in AssetDatabase.FindAssets(name))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var material = FindMaterialAtPath(path, name);
                if (material != null) return material;
            }

            return null;
        }

        private static Material FindMaterialAtPath(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is not Material material) continue;
                if (string.IsNullOrWhiteSpace(name)
                    || string.Equals(material.name, name, StringComparison.OrdinalIgnoreCase))
                    return material;
            }
            return null;
        }

        private static string[] EnumerateColorProperties(Material material)
        {
            var names = CommonColorProperties
                .Where(material.HasProperty)
                .ToList();

            var shader = material.shader;
            if (shader != null)
            {
                var count = shader.GetPropertyCount();
                for (var i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) != ShaderPropertyType.Color) continue;
                    var name = shader.GetPropertyName(i);
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                        names.Add(name);
                }
            }

            return names.ToArray();
        }

        /// <summary>
        /// Collects a material's non-color shader properties (float/range, vector, and texture) into the
        /// supplied arrays. Texture entries report the assigned texture's asset path when available.
        /// </summary>
        private static void EnumerateNonColorProperties(Material material, JArray floats, JArray vectors, JArray textures)
        {
            var shader = material.shader;
            if (shader == null) return;

            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                if (string.IsNullOrEmpty(name) || !material.HasProperty(name)) continue;

                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        floats.Add(new JObject { ["name"] = name, ["value"] = material.GetFloat(name) });
                        break;
                    case ShaderPropertyType.Vector:
                        var v = material.GetVector(name);
                        vectors.Add(new JObject
                        {
                            ["name"] = name,
                            ["value"] = new JArray { v.x, v.y, v.z, v.w }
                        });
                        break;
                    case ShaderPropertyType.Texture:
                        var texture = material.GetTexture(name);
                        textures.Add(new JObject
                        {
                            ["name"] = name,
                            ["texture"] = texture != null ? texture.name : null,
                            ["assetPath"] = texture != null ? AssetDatabase.GetAssetPath(texture) : null
                        });
                        break;
                }
            }
        }

        private static JObject ColorToObject(Color color)
            => new JObject
            {
                ["r"] = color.r,
                ["g"] = color.g,
                ["b"] = color.b,
                ["a"] = color.a
            };

        private static Color? ReadColor(JObject args, out string error)
        {
            error = null;
            var hex = args.Value<string>("hex");
            var rgba = args["rgba"] as JArray;
            if (!string.IsNullOrWhiteSpace(hex) && rgba != null)
            {
                error = "pass either 'hex' or 'rgba', not both.";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(hex))
            {
                var value = hex.Trim();
                if (!value.StartsWith("#", StringComparison.Ordinal)) value = "#" + value;
                if (ColorUtility.TryParseHtmlString(value, out var color)) return color;
                error = "'hex' must be #RRGGBB, RRGGBB, #RRGGBBAA, or RRGGBBAA.";
                return null;
            }

            if (rgba != null)
            {
                if (rgba.Count < 3 || rgba.Count > 4)
                {
                    error = "'rgba' must contain 3 or 4 numeric components.";
                    return null;
                }

                return new Color(
                    rgba[0].Value<float>(),
                    rgba[1].Value<float>(),
                    rgba[2].Value<float>(),
                    rgba.Count == 4 ? rgba[3].Value<float>() : 1f);
            }

            error = "pass a color with 'hex' or 'rgba'.";
            return null;
        }

        private static JObject ColorResult(Color color)
            => new JObject
            {
                ["value"] = ColorToObject(color),
                ["hexRgb"] = ColorUtility.ToHtmlStringRGB(color),
                ["hexRgba"] = ColorUtility.ToHtmlStringRGBA(color)
            };
    }
}
