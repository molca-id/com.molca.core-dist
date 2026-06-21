using System;
using System.Linq;
using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only Unity UI / Canvas inspection tools. Lists canvases, walks RectTransform layout trees,
    /// and runs basic broken-layout diagnostics. Built on engine-core <see cref="Canvas"/> and
    /// <see cref="RectTransform"/> plus component type-name classification so it does not require a hard
    /// UGUI (<c>UnityEngine.UI</c>) assembly reference and stays compilable if a consumer removes UGUI.
    /// </summary>
    /// <remarks>Read-only; main thread only.</remarks>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateCanvasesTool() => new McpToolDefinition(
            name: "molca_unity_canvases",
            description: "Lists Canvas components in the loaded scene(s) with hierarchy path, render mode, "
                       + "target/world camera, sorting layer/order, pixel-perfect flag, and CanvasScaler "
                       + "settings (reflected without a hard UGUI dependency).",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteCanvases,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateUiTreeTool() => new McpToolDefinition(
            name: "molca_unity_ui_tree",
            description: "Walks the RectTransform tree under a UI root (a Canvas or any GameObject) reporting "
                       + "each element's hierarchy path, UI component types, and RectTransform layout (anchors, "
                       + "anchoredPosition, sizeDelta, pivot). Resolve the root by hierarchy path or instance id; "
                       + "'limit' caps results (default 300).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Root GameObject hierarchy path or instance id (e.g. a Canvas).\"}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Max elements to return (default 300).\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteUiTree,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateUiDiagnosticsTool() => new McpToolDefinition(
            name: "molca_unity_ui_diagnostics",
            description: "Runs basic UI layout/health diagnostics across loaded canvases: missing scripts on UI "
                       + "objects, zero-size RectTransforms, and RectTransforms with negative scale. Reports "
                       + "issues with hierarchy paths so they can be fixed.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteUiDiagnostics,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateUiSetRectTool() => new McpToolDefinition(
            name: "molca_unity_ui_set_rect",
            description: "Sets RectTransform layout on a UI GameObject: any of anchorMin, anchorMax, "
                       + "anchoredPosition, sizeDelta, pivot (each a [x,y] array). Resolve by hierarchy path or "
                       + "instance id. Only provided fields are changed. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id with a RectTransform.\"}," +
                "\"anchorMin\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2}," +
                "\"anchorMax\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2}," +
                "\"anchoredPosition\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2}," +
                "\"sizeDelta\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2}," +
                "\"pivot\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteUiSetRect,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteUiSetRect(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            var rect = go.GetComponent<RectTransform>();
            if (rect == null) return Error($"GameObject '{GameObjectEditingService.GetHierarchyPath(go)}' has no RectTransform.");

            Undo.RecordObject(rect, $"MCP Set RectTransform {rect.name}");
            if (ReadVector2(args["anchorMin"], out var anchorMin)) rect.anchorMin = anchorMin;
            if (ReadVector2(args["anchorMax"], out var anchorMax)) rect.anchorMax = anchorMax;
            if (ReadVector2(args["anchoredPosition"], out var anchoredPos)) rect.anchoredPosition = anchoredPos;
            if (ReadVector2(args["sizeDelta"], out var sizeDelta)) rect.sizeDelta = sizeDelta;
            if (ReadVector2(args["pivot"], out var pivot)) rect.pivot = pivot;
            EditorUtility.SetDirty(rect);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(rect.gameObject),
                ["rect"] = RectTransformToJson(rect)
            }.ToString(Formatting.None);
        }

        private static bool ReadVector2(JToken token, out Vector2 value)
        {
            value = Vector2.zero;
            if (token is not JArray array || array.Count != 2) return false;
            value = new Vector2(array[0].Value<float>(), array[1].Value<float>());
            return true;
        }

        private static string ExecuteCanvases(string argumentsJson)
        {
            var canvases = new JArray();
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                foreach (var canvas in root.GetComponentsInChildren<Canvas>(true))
                {
                    var item = new JObject
                    {
                        ["path"] = GameObjectEditingService.GetHierarchyPath(canvas.gameObject),
                        ["instanceId"] = canvas.GetInstanceID(),
                        ["enabled"] = canvas.enabled,
                        ["renderMode"] = canvas.renderMode.ToString(),
                        ["sortingLayer"] = canvas.sortingLayerName,
                        ["sortingOrder"] = canvas.sortingOrder,
                        ["pixelPerfect"] = canvas.pixelPerfect,
                        ["worldCamera"] = canvas.worldCamera != null
                            ? GameObjectEditingService.GetHierarchyPath(canvas.worldCamera.gameObject)
                            : null,
                        ["isRootCanvas"] = canvas.isRootCanvas
                    };

                    AppendCanvasScaler(canvas.gameObject, item);
                    canvases.Add(item);
                }
            }

            return new JObject
            {
                ["count"] = canvases.Count,
                ["canvases"] = canvases
            }.ToString(Formatting.None);
        }

        private static string ExecuteUiTree(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);
            var limit = args["limit"] != null ? Math.Max(1, args.Value<int>("limit")) : 300;

            var elements = new JArray();
            var truncated = false;
            foreach (var rect in go.GetComponentsInChildren<RectTransform>(true))
            {
                if (elements.Count >= limit) { truncated = true; break; }
                elements.Add(new JObject
                {
                    ["path"] = GameObjectEditingService.GetHierarchyPath(rect.gameObject),
                    ["active"] = rect.gameObject.activeSelf,
                    ["uiComponents"] = new JArray(rect.gameObject.GetComponents<Component>()
                        .Where(c => c != null && c is not Transform)
                        .Select(c => c.GetType().Name)),
                    ["rect"] = RectTransformToJson(rect)
                });
            }

            return new JObject
            {
                ["root"] = GameObjectEditingService.GetHierarchyPath(go),
                ["count"] = elements.Count,
                ["truncated"] = truncated,
                ["elements"] = elements
            }.ToString(Formatting.None);
        }

        private static string ExecuteUiDiagnostics(string argumentsJson)
        {
            var issues = new JArray();
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                foreach (var canvas in root.GetComponentsInChildren<Canvas>(true))
                {
                    foreach (var rect in canvas.GetComponentsInChildren<RectTransform>(true))
                    {
                        var path = GameObjectEditingService.GetHierarchyPath(rect.gameObject);

                        if (rect.gameObject.GetComponents<Component>().Any(c => c == null))
                            issues.Add(Issue(path, "missingScript", "A component (likely a MonoBehaviour script) is missing."));

                        var size = rect.rect.size;
                        if (size.x == 0f || size.y == 0f)
                            issues.Add(Issue(path, "zeroSize", $"RectTransform has zero size ({size.x} x {size.y})."));

                        var scale = rect.localScale;
                        if (scale.x < 0f || scale.y < 0f || scale.z < 0f)
                            issues.Add(Issue(path, "negativeScale", $"RectTransform has negative scale {Vector3ToJson(scale)}."));
                    }
                }
            }

            return new JObject
            {
                ["issueCount"] = issues.Count,
                ["issues"] = issues
            }.ToString(Formatting.None);
        }

        /// <summary>
        /// Reflects the optional UGUI <c>CanvasScaler</c> component's scale settings onto <paramref name="target"/>
        /// without referencing the <c>UnityEngine.UI</c> assembly. No-op when UGUI is absent.
        /// </summary>
        private static void AppendCanvasScaler(GameObject go, JObject target)
        {
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null || component.GetType().Name != "CanvasScaler") continue;
                var type = component.GetType();
                target["scalerMode"] = ReflectToString(type, component, "uiScaleMode");
                var refRes = ReflectValue(type, component, "referenceResolution");
                if (refRes is Vector2 v)
                    target["referenceResolution"] = new JArray { v.x, v.y };
                return;
            }
        }

        private static string ReflectToString(Type type, object instance, string member)
            => ReflectValue(type, instance, member)?.ToString();

        private static object ReflectValue(Type type, object instance, string member)
        {
            var property = type.GetProperty(member);
            return property != null ? property.GetValue(instance) : null;
        }

        private static JObject RectTransformToJson(RectTransform rect) => new JObject
        {
            ["anchorMin"] = new JArray { rect.anchorMin.x, rect.anchorMin.y },
            ["anchorMax"] = new JArray { rect.anchorMax.x, rect.anchorMax.y },
            ["anchoredPosition"] = new JArray { rect.anchoredPosition.x, rect.anchoredPosition.y },
            ["sizeDelta"] = new JArray { rect.sizeDelta.x, rect.sizeDelta.y },
            ["pivot"] = new JArray { rect.pivot.x, rect.pivot.y },
            ["size"] = new JArray { rect.rect.width, rect.rect.height }
        };

        private static JObject Issue(string path, string kind, string message) => new JObject
        {
            ["path"] = path,
            ["kind"] = kind,
            ["message"] = message
        };
    }
}
