using System.IO;
using System.Linq;
using Molca.Editor.Networking;
using Molca.Networking.Http;
using Molca.Networking.Http.Models;
using Molca.Networking.Utils;
using Molca.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Networking tools: read the HTTP module config and the project's <see cref="HttpRequestAsset"/>
    /// catalog, and author request assets (create / edit fields). Reads are <see cref="McpToolMode.Any"/>
    /// / <see cref="McpToolKind.ReadOnly"/>; authoring is <see cref="McpToolMode.Edit"/> /
    /// <see cref="McpToolKind.Action"/> via Unity Undo, gated by the allowlist+confirmation guardrails.
    /// </summary>
    /// <remarks>
    /// All URL/header output is redacted via <see cref="LogRedaction"/> so credentials never cross MCP.
    /// Authoring is refused for read-only protected zones (<c>Packages/</c>, <c>Assets/_MolcaSDK/</c>) —
    /// the caller is directed to author in the project area. <see cref="HttpModule"/> <i>config</i> writes
    /// are intentionally not duplicated here: <c>HttpModule</c> is a <see cref="SettingModule"/>, so
    /// <c>molca_settings_set_fields</c> already authors it.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── molca_network_config (read) ──────────────────────────────────────────────────────

        private static McpToolDefinition CreateNetworkConfigTool() => new McpToolDefinition(
            name: "molca_network_config",
            description: "Reads the HttpModule networking config: base URL, default timeout, max concurrent "
                       + "requests, retry policy, redirect/SSL/logging flags, history settings, and the default "
                       + "header keys (values redacted). To change these, use molca_settings_set_fields on "
                       + "HttpModule. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteNetworkConfig,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteNetworkConfig(string argumentsJson)
        {
            var module = ResolveHttpModule(out string assetPath);
            if (module == null)
                return Error("No HttpModule found in GlobalSettings or the project. Add an HTTP Settings module.");

            var headers = new JArray();
            foreach (var kvp in module.GetDefaultHeaders())
                headers.Add(new JObject { ["key"] = kvp.Key, ["value"] = LogRedaction.RedactHeaderValue(kvp.Key, kvp.Value) });

            return new JObject
            {
                ["assetPath"] = assetPath,
                ["baseUrl"] = module.BaseUrl,
                ["defaultTimeout"] = module.DefaultTimeout,
                ["maxConcurrentRequests"] = module.MaxConcurrentRequests,
                ["enableRequestHistory"] = module.EnableRequestHistory,
                ["maxHistorySize"] = module.MaxHistorySize,
                ["enableRetry"] = module.EnableRetry,
                ["maxRetries"] = module.MaxRetries,
                ["retryBaseDelaySeconds"] = module.RetryBaseDelaySeconds,
                ["followRedirects"] = module.FollowRedirects,
                ["validateSSL"] = module.ValidateSSL,
                ["enableLogging"] = module.EnableLogging,
                ["defaultHeaders"] = headers
            }.ToString(Formatting.None);
        }

        // ── molca_network_list_requests (read) ───────────────────────────────────────────────

        private static McpToolDefinition CreateNetworkListRequestsTool() => new McpToolDefinition(
            name: "molca_network_list_requests",
            description: "Lists every HttpRequestAsset in the project: asset path, name, method, redacted URL, "
                       + "full-url flag, header/param keys, and body type. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteNetworkListRequests,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteNetworkListRequests(string argumentsJson)
        {
            var arr = new JArray();
            foreach (var guid in AssetDatabase.FindAssets("t:HttpRequestAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<HttpRequestAsset>(path);
                if (asset == null) continue;
                var r = asset.request ?? new HttpRequest();
                arr.Add(new JObject
                {
                    ["assetPath"] = path,
                    ["name"] = r.name,
                    ["method"] = r.method.ToString(),
                    ["url"] = LogRedaction.RedactUrl(r.url),
                    ["useFullUrl"] = r.useFullUrl,
                    ["bodyType"] = r.bodyType.ToString(),
                    ["headerKeys"] = new JArray(r.headers.Select(h => h.key)),
                    ["paramKeys"] = new JArray(r.queryParams.Select(p => p.key))
                });
            }

            return new JObject { ["count"] = arr.Count, ["requests"] = arr }.ToString(Formatting.None);
        }

        // ── molca_network_get_request (read) ─────────────────────────────────────────────────

        private static McpToolDefinition CreateNetworkGetRequestTool() => new McpToolDefinition(
            name: "molca_network_get_request",
            description: "Reads one HttpRequestAsset in full (by asset path or name): method, redacted URL, "
                       + "headers and query params (sensitive values masked), body, timeout, response type, "
                       + "and the asset's validation result. Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"asset\":{\"type\":\"string\",\"description\":\"Asset path (…/Foo.asset) or request asset name.\"}}," +
                "\"required\":[\"asset\"],\"additionalProperties\":false}",
            execute: ExecuteNetworkGetRequest,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteNetworkGetRequest(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var asset = ResolveRequestAsset(args.Value<string>("asset"), out string assetPath, out string error);
            if (asset == null) return Error(error);

            var r = asset.request ?? new HttpRequest();
            bool valid = asset.Validate(out string[] errors);

            return new JObject
            {
                ["assetPath"] = assetPath,
                ["name"] = r.name,
                ["method"] = r.method.ToString(),
                ["url"] = LogRedaction.RedactUrl(r.url),
                ["useFullUrl"] = r.useFullUrl,
                ["timeout"] = r.timeout,
                ["followRedirects"] = r.followRedirects,
                ["validateSSL"] = r.validateSSL,
                ["responseType"] = r.expectedResponseType.ToString(),
                ["bodyType"] = r.bodyType.ToString(),
                ["jsonBody"] = r.jsonBody,
                ["headers"] = KeyValueArray(r.headers.Select(h => (h.key, h.value, h.isEnabled))),
                ["queryParams"] = KeyValueArray(r.queryParams.Select(p => (p.key, p.value, p.isEnabled))),
                ["validation"] = new JObject { ["valid"] = valid, ["errors"] = new JArray(errors) }
            }.ToString(Formatting.None);
        }

        // ── molca_network_create_request (action) ────────────────────────────────────────────

        private static McpToolDefinition CreateNetworkCreateRequestTool() => new McpToolDefinition(
            name: "molca_network_create_request",
            description: "Creates a new HttpRequestAsset. Supply 'path' (…/Foo.asset) or 'folder'+'name'. "
                       + "Optional authoring fields: method, url, useFullUrl, timeout, responseType, bodyType, "
                       + "jsonBody, headers, queryParams (headers/queryParams as a {key:value} object or an "
                       + "array of {key,value,enabled}). Refuses read-only zones (Packages/, Assets/_MolcaSDK/). "
                       + "Undoable via Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\"},\"folder\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"}," +
                "\"method\":{\"type\":\"string\"},\"url\":{\"type\":\"string\"},\"useFullUrl\":{\"type\":\"boolean\"}," +
                "\"timeout\":{\"type\":\"integer\"},\"responseType\":{\"type\":\"string\"},\"bodyType\":{\"type\":\"string\"}," +
                "\"jsonBody\":{\"type\":\"string\"},\"headers\":{},\"queryParams\":{}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteNetworkCreateRequest,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteNetworkCreateRequest(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            string path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
            {
                string folder = args.Value<string>("folder");
                string name = args.Value<string>("name");
                if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(name))
                    return Error("Provide 'path' (…/Foo.asset), or both 'folder' and 'name'.");
                path = $"{folder.TrimEnd('/')}/{name}.asset";
            }
            path = path.Replace('\\', '/');
            if (!path.EndsWith(".asset")) path += ".asset";

            if (IsProtectedPath(path))
                return Error($"'{path}' is in a read-only zone (Packages/ or Assets/_MolcaSDK/). "
                           + "Author request assets in your project area instead.");

            string folderPath = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
                return Error($"Target folder '{folderPath}' does not exist. Create it first.");

            string uniquePath = AssetDatabase.GenerateUniqueAssetPath(path);

            var asset = ScriptableObject.CreateInstance<HttpRequestAsset>();
            asset.request ??= new HttpRequest();
            if (string.IsNullOrEmpty(asset.request.name))
                asset.request.name = Path.GetFileNameWithoutExtension(uniquePath);

            var result = HttpRequestEditingService.Apply(asset.request, ExtractAuthoringFields(args));

            AssetDatabase.CreateAsset(asset, uniquePath);
            Undo.RegisterCreatedObjectUndo(asset, "Create HTTP Request");
            AssetDatabase.SaveAssets();

            return new JObject
            {
                ["created"] = true,
                ["assetPath"] = uniquePath,
                ["applied"] = new JArray(result.Applied),
                ["rejected"] = RejectedJson(result)
            }.ToString(Formatting.None);
        }

        // ── molca_network_set_request_fields (action) ────────────────────────────────────────

        private static McpToolDefinition CreateNetworkSetRequestFieldsTool() => new McpToolDefinition(
            name: "molca_network_set_request_fields",
            description: "Edits fields on an existing HttpRequestAsset (by asset path or name). 'fields' is an "
                       + "object of fieldName -> value: name, method, url, useFullUrl, timeout, followRedirects, "
                       + "validateSSL, responseType, bodyType, jsonBody, headers, queryParams. headers/queryParams "
                       + "replace the whole list. Refuses read-only zones. One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"asset\":{\"type\":\"string\",\"description\":\"Asset path or request name.\"}," +
                "\"fields\":{\"type\":\"object\",\"description\":\"fieldName -> value map.\"}}," +
                "\"required\":[\"asset\",\"fields\"],\"additionalProperties\":false}",
            execute: ExecuteNetworkSetRequestFields,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteNetworkSetRequestFields(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var asset = ResolveRequestAsset(args.Value<string>("asset"), out string assetPath, out string error);
            if (asset == null) return Error(error);

            if (IsProtectedPath(assetPath))
                return Error($"'{assetPath}' is in a read-only zone. Request assets there can only be edited in their project.");

            if (!(args["fields"] is JObject fields) || !fields.HasValues)
                return Error("'fields' must be a non-empty object of fieldName -> value.");

            var result = HttpRequestEditingService.SetFields(asset, fields);

            return new JObject
            {
                ["assetPath"] = assetPath,
                ["applied"] = new JArray(result.Applied),
                ["rejected"] = RejectedJson(result),
                ["writableFields"] = result.Rejected.Count > 0
                    ? new JArray(HttpRequestEditingService.WritableFields)
                    : new JArray()
            }.ToString(Formatting.None);
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────

        private static HttpModule ResolveHttpModule(out string assetPath)
        {
            assetPath = null;
            var project = MolcaProjectSettings.Instance;
            var modules = project?.GlobalSettings?.modules;
            if (modules != null)
            {
                var fromGlobal = modules.OfType<HttpModule>().FirstOrDefault();
                if (fromGlobal != null) { assetPath = AssetDatabase.GetAssetPath(fromGlobal); return fromGlobal; }
            }

            // Fallback: any HttpModule asset in the project (edit-mode read uses authored defaults).
            var guid = AssetDatabase.FindAssets("t:HttpModule").FirstOrDefault();
            if (string.IsNullOrEmpty(guid)) return null;
            assetPath = AssetDatabase.GUIDToAssetPath(guid);
            return AssetDatabase.LoadAssetAtPath<HttpModule>(assetPath);
        }

        private static HttpRequestAsset ResolveRequestAsset(string idOrPath, out string assetPath, out string error)
        {
            assetPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(idOrPath))
            {
                error = "'asset' is required (asset path or request name).";
                return null;
            }

            string normalized = idOrPath.Replace('\\', '/');
            if (normalized.EndsWith(".asset"))
            {
                var byPath = AssetDatabase.LoadAssetAtPath<HttpRequestAsset>(normalized);
                if (byPath != null) { assetPath = normalized; return byPath; }
            }

            string target = Path.GetFileNameWithoutExtension(normalized);
            foreach (var guid in AssetDatabase.FindAssets($"t:HttpRequestAsset {target}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == target)
                {
                    assetPath = path;
                    return AssetDatabase.LoadAssetAtPath<HttpRequestAsset>(path);
                }
            }

            error = $"No HttpRequestAsset matching '{idOrPath}'. Use molca_network_list_requests to see available assets.";
            return null;
        }

        // Copies only the writable authoring fields from a create-tool arg object into a fields map.
        private static JObject ExtractAuthoringFields(JObject args)
        {
            var fields = new JObject();
            foreach (var key in HttpRequestEditingService.WritableFields)
                if (args[key] != null)
                    fields[key] = args[key];
            return fields;
        }

        private static JArray KeyValueArray(System.Collections.Generic.IEnumerable<(string key, string value, bool enabled)> entries)
        {
            var arr = new JArray();
            foreach (var (key, value, enabled) in entries)
            {
                arr.Add(new JObject
                {
                    ["key"] = key,
                    // Mask values whose key marks them sensitive (auth tokens, api keys, …).
                    ["value"] = LogRedaction.RedactHeaderValue(key, value),
                    ["enabled"] = enabled
                });
            }
            return arr;
        }

        private static JObject RejectedJson(HttpRequestEditingService.Result result)
        {
            var obj = new JObject();
            foreach (var kvp in result.Rejected)
                obj[kvp.Key] = kvp.Value;
            return obj;
        }
    }
}
