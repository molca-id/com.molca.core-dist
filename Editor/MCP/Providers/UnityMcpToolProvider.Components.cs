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
    /// Unity component type discovery, component inspection, and serialized field editing tools.
    /// </summary>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateComponentTypesTool() => new McpToolDefinition(
            name: "molca_unity_component_types",
            description: "Searches concrete Unity Component types by name/full name. Use before adding a "
                       + "component when the exact type name is unknown.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"Case-insensitive type name/full name substring.\"}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Max entries to return (default 100).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteComponentTypes,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteComponentTypes(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var query = args.Value<string>("query");
            var limit = args["limit"] != null ? Math.Max(1, args.Value<int>("limit")) : 100;
            var types = new JArray();
            var matches = TypeCache.GetTypesDerivedFrom<Component>()
                .Where(t => !t.IsAbstract)
                .Where(t => string.IsNullOrWhiteSpace(query)
                    || t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || (t.FullName != null && t.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(t => t.FullName)
                .Take(limit + 1)
                .ToList();

            foreach (var type in matches.Take(limit))
            {
                types.Add(new JObject
                {
                    ["name"] = type.Name,
                    ["fullName"] = type.FullName,
                    ["namespace"] = type.Namespace,
                    ["assembly"] = type.Assembly.GetName().Name
                });
            }

            return new JObject
            {
                ["count"] = types.Count,
                ["truncated"] = matches.Count > limit,
                ["types"] = types
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateGameObjectComponentsTool() => new McpToolDefinition(
            name: "molca_unity_gameobject_components",
            description: "Lists components on a GameObject with stable component indexes for follow-up "
                       + "inspection. Resolve the GameObject by hierarchy path or instance id.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Hierarchy path or instance id.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteGameObjectComponents,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteGameObjectComponents(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            var components = new JArray();
            var all = go.GetComponents<Component>();
            for (int i = 0; i < all.Length; i++)
            {
                var component = all[i];
                if (component == null)
                {
                    components.Add(new JObject { ["index"] = i, ["missing"] = true });
                    continue;
                }

                var type = component.GetType();
                var item = new JObject
                {
                    ["index"] = i,
                    ["type"] = type.Name,
                    ["fullName"] = type.FullName,
                    ["instanceId"] = component.GetInstanceID()
                };
                if (component is Behaviour behaviour) item["enabled"] = behaviour.enabled;
                if (component is Renderer renderer) item["enabled"] = renderer.enabled;
                components.Add(item);
            }

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["count"] = components.Count,
                ["components"] = components
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateComponentFieldsTool() => new McpToolDefinition(
            name: "molca_unity_component_fields",
            description: "Lists serialized fields on a component. Identify the component by GameObject "
                       + "target plus component index from molca_unity_gameobject_components.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id.\"}," +
                "\"componentIndex\":{\"type\":\"integer\",\"description\":\"Component index on the GameObject.\"}," +
                "\"includeChildren\":{\"type\":\"boolean\",\"description\":\"Include nested child properties (default false).\"}}," +
                "\"required\":[\"target\",\"componentIndex\"],\"additionalProperties\":false}",
            execute: ExecuteComponentFields,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteComponentFields(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var component = ResolveComponent(args, out var go, out var index, out var error);
            if (component == null) return Error(error);

            var includeChildren = args.Value<bool?>("includeChildren") == true;
            var serialized = new SerializedObject(component);
            var iterator = serialized.GetIterator();
            var fields = new JArray();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = includeChildren;
                fields.Add(new JObject
                {
                    ["path"] = iterator.propertyPath,
                    ["displayName"] = iterator.displayName,
                    ["type"] = iterator.propertyType.ToString(),
                    ["editable"] = iterator.editable,
                    ["isArray"] = iterator.isArray,
                    ["value"] = SerializedValueToJson(iterator)
                });
            }

            return new JObject
            {
                ["target"] = GameObjectEditingService.GetHierarchyPath(go),
                ["componentIndex"] = index,
                ["componentType"] = component.GetType().FullName,
                ["fieldCount"] = fields.Count,
                ["fields"] = fields
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateComponentSetFieldsTool() => new McpToolDefinition(
            name: "molca_unity_component_set_fields",
            description: "Sets serialized fields on a component by GameObject target and component index. "
                       + "Use molca_unity_component_fields first to inspect valid property paths. One undo "
                       + "group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id.\"}," +
                "\"componentIndex\":{\"type\":\"integer\",\"description\":\"Component index from molca_unity_gameobject_components.\"}," +
                "\"fields\":{\"type\":\"object\",\"description\":\"Serialized property path to value map.\"}}," +
                "\"required\":[\"target\",\"componentIndex\",\"fields\"],\"additionalProperties\":false}",
            execute: ExecuteComponentSetFields,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteComponentSetFields(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var component = ResolveComponent(args, out var go, out var index, out var error);
            if (component == null) return Error(error);

            if (!(args["fields"] is JObject fieldsObj))
                return Error("'fields' object is required.");

            var result = GameObjectEditingService.SetComponentFields(component, ToFieldNodeMap(fieldsObj));
            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["componentIndex"] = index,
                ["componentType"] = component.GetType().FullName,
                ["applied"] = new JArray(result.Applied),
                ["rejected"] = RejectedToJson(result.Rejected)
            }.ToString(Formatting.None);
        }

        private static Type ResolveComponentType(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "'type' is required.";
                return null;
            }

            var matches = TypeCache.GetTypesDerivedFrom<Component>()
                .Where(t => !t.IsAbstract && (t.Name == name || t.FullName == name))
                .ToList();

            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0)
            {
                error = $"no concrete Component type named '{name}'.";
                return null;
            }
            error = $"ambiguous component type '{name}': {string.Join(", ", matches.Select(m => m.FullName))}.";
            return null;
        }

        private static Component ResolveComponent(JObject args, out GameObject go, out int index, out string error)
        {
            go = GameObjectEditingService.Resolve(args.Value<string>("target"), out error);
            index = args.Value<int?>("componentIndex") ?? -1;
            if (go == null) return null;

            var components = go.GetComponents<Component>();
            if (index < 0 || index >= components.Length)
            {
                error = $"componentIndex {index} is out of range (0..{components.Length - 1}).";
                return null;
            }

            var component = components[index];
            if (component == null)
            {
                error = $"componentIndex {index} is a missing script/component.";
                return null;
            }

            return component;
        }

        private static Dictionary<string, FieldNode> ToFieldNodeMap(JObject fields)
        {
            var map = new Dictionary<string, FieldNode>();
            foreach (var pair in fields)
                map[pair.Key] = ToFieldNode(pair.Value);
            return map;
        }

        private static FieldNode ToFieldNode(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    var members = new Dictionary<string, FieldNode>();
                    foreach (var pair in obj)
                        members[pair.Key] = ToFieldNode(pair.Value);
                    return FieldNode.FromMembers(members);

                case JArray arr:
                    return FieldNode.FromList(arr.Select(ToFieldNode).ToList());

                default:
                    return FieldNode.FromScalar(ScalarToString(token));
            }
        }

        private static string ScalarToString(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.String: return token.Value<string>();
                case JTokenType.Boolean: return token.Value<bool>() ? "true" : "false";
                case JTokenType.Null: return "null";
                case JTokenType.Integer:
                    return token.Value<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JTokenType.Float:
                    return token.Value<double>().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JTokenType.Array:
                    return string.Join(",", ((JArray)token).Select(ScalarToString));
                default:
                    return token.ToString(Formatting.None);
            }
        }

        private static JArray RejectedToJson(IReadOnlyList<KeyValuePair<string, string>> rejected)
        {
            var arr = new JArray();
            foreach (var pair in rejected)
                arr.Add(new JObject { ["field"] = pair.Key, ["reason"] = pair.Value });
            return arr;
        }

        private static JToken SerializedValueToJson(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.intValue;
                case SerializedPropertyType.Boolean:
                    return property.boolValue;
                case SerializedPropertyType.Float:
                    return property.floatValue;
                case SerializedPropertyType.String:
                    return property.stringValue;
                case SerializedPropertyType.Color:
                    return "#" + ColorUtility.ToHtmlStringRGBA(property.colorValue);
                case SerializedPropertyType.ObjectReference:
                    return property.objectReferenceValue != null
                        ? new JObject
                        {
                            ["name"] = property.objectReferenceValue.name,
                            ["type"] = property.objectReferenceValue.GetType().Name,
                            ["instanceId"] = property.objectReferenceValue.GetInstanceID(),
                            ["assetPath"] = AssetDatabase.GetAssetPath(property.objectReferenceValue)
                        }
                        : JValue.CreateNull();
                case SerializedPropertyType.LayerMask:
                    return property.intValue;
                case SerializedPropertyType.Enum:
                    return property.enumDisplayNames != null
                        && property.enumValueIndex >= 0
                        && property.enumValueIndex < property.enumDisplayNames.Length
                            ? property.enumDisplayNames[property.enumValueIndex]
                            : property.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2:
                    return new JArray { property.vector2Value.x, property.vector2Value.y };
                case SerializedPropertyType.Vector3:
                    return Vector3ToJson(property.vector3Value);
                case SerializedPropertyType.Vector4:
                    return new JArray { property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w };
                case SerializedPropertyType.Rect:
                    return new JObject
                    {
                        ["x"] = property.rectValue.x,
                        ["y"] = property.rectValue.y,
                        ["width"] = property.rectValue.width,
                        ["height"] = property.rectValue.height
                    };
                case SerializedPropertyType.Bounds:
                    return new JObject
                    {
                        ["center"] = Vector3ToJson(property.boundsValue.center),
                        ["size"] = Vector3ToJson(property.boundsValue.size)
                    };
                case SerializedPropertyType.Quaternion:
                    return new JArray { property.quaternionValue.x, property.quaternionValue.y, property.quaternionValue.z, property.quaternionValue.w };
                default:
                    return property.hasVisibleChildren ? "<object>" : property.ToString();
            }
        }
    }
}
