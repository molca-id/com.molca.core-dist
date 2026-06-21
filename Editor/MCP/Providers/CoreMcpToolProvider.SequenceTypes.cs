using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only <c>molca_sequence_list_types</c> tool: the discovery counterpart to the Sprint 19/20
    /// authoring tools. Lists every concrete <see cref="Step"/> and <see cref="StepAuxiliary"/> type the
    /// project can instantiate, each with its writable serialized fields (name, type, and enum options),
    /// so an assistant can plan an <c>add_steps</c>/<c>add_auxiliary</c> + <c>set_*_fields</c> call in one
    /// shot instead of discovering the catalog through rejection errors.
    /// </summary>
    public partial class CoreMcpToolProvider
    {
        private static McpToolDefinition CreateSequenceListTypesTool() => new McpToolDefinition(
            name: "molca_sequence_list_types",
            description: "Lists the concrete Step and StepAuxiliary types available in the project, each with "
                       + "its writable serialized fields (field name, type, and enum options where applicable). "
                       + "Use this to discover what can be created with molca_sequence_add_steps / "
                       + "molca_sequence_add_auxiliary and configured with the set-fields tools. Optional "
                       + "'kind' filter: 'step', 'auxiliary', or 'all' (default).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"step\",\"auxiliary\",\"all\"]," +
                "\"description\":\"Which type family to list; defaults to 'all'.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteListTypes,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteListTypes(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var kind = (args.Value<string>("kind") ?? "all").ToLowerInvariant();

            var result = new JObject();
            if (kind == "step" || kind == "all")
            {
                var stepTypes = TypeCache.GetTypesDerivedFrom<Step>()
                    .Append(typeof(Step))
                    .Where(t => !t.IsAbstract);
                result["stepTypes"] = DescribeTypes(stepTypes, isStep: true);
            }
            if (kind == "auxiliary" || kind == "all")
            {
                var auxTypes = TypeCache.GetTypesDerivedFrom<StepAuxiliary>()
                    .Where(t => !t.IsAbstract);
                result["auxiliaryTypes"] = DescribeTypes(auxTypes, isStep: false);
            }
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Describes each type as {name, fullName, fields[]}. Fields are discovered by reflection over the
        /// serializable members (public instance fields and <c>[SerializeField]</c> non-public fields) up
        /// the inheritance chain, mirroring what the set-fields tools can write. The managed
        /// <c>auxiliaries</c> list is excluded — it is edited through the dedicated auxiliary tools.
        /// </summary>
        private static JArray DescribeTypes(IEnumerable<Type> types, bool isStep)
        {
            var arr = new JArray();
            foreach (var type in types.OrderBy(t => t.Name))
            {
                arr.Add(new JObject
                {
                    ["name"] = type.Name,
                    ["fullName"] = type.FullName,
                    ["fields"] = DescribeFields(type, isStep)
                });
            }
            return arr;
        }

        private static JArray DescribeFields(Type type, bool isStep)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fields = new JArray();
            var seen = new HashSet<string>();

            // Walk base types so inherited serialized fields are included (Step.stepId, etc.).
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (!IsSerialized(field) || !seen.Add(field.Name)) continue;
                    if (isStep && field.Name == "auxiliaries") continue; // edited via auxiliary tools

                    var entry = new JObject
                    {
                        ["name"] = field.Name,
                        ["type"] = field.FieldType.Name
                    };
                    if (field.FieldType.IsEnum)
                        entry["enumOptions"] = new JArray(Enum.GetNames(field.FieldType));
                    else if (IsCollection(field.FieldType, out var element))
                        entry["elementType"] = element.Name;
                    fields.Add(entry);
                }
            }
            return fields;
        }

        /// <summary>True if Unity would serialize this field (public, or non-public with [SerializeField]).</summary>
        private static bool IsSerialized(FieldInfo field)
        {
            if (field.IsStatic || field.IsLiteral || field.IsInitOnly) return false;
            if (field.IsDefined(typeof(NonSerializedAttribute), false)) return false;
            return field.IsPublic || field.IsDefined(typeof(SerializeField), false);
        }

        /// <summary>Resolves the element type of an array or <see cref="List{T}"/> serialized field.</summary>
        private static bool IsCollection(Type type, out Type elementType)
        {
            elementType = null;
            if (type == typeof(string)) return false;
            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return true;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
            return false;
        }
    }
}
