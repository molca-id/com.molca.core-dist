using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Molca.Networking.Http;
using Molca.Networking.Http.Models;

namespace Molca.Editor.Networking
{
    /// <summary>
    /// Authoring service for <see cref="HttpRequestAsset"/> — applies a JSON field map to the asset's
    /// embedded <see cref="HttpRequest"/>. Mirrors <c>SettingsFieldEditingService</c>: each write is a
    /// single Unity Undo group (Ctrl+Z reverts), the asset is marked dirty and saved, and unknown fields
    /// are reported back rather than silently ignored.
    /// </summary>
    /// <remarks>
    /// This authors the asset's serialized request on disk (Inspector-equivalent), never a runtime
    /// request — per the SOs-out rule, request assets are read-only config at play time. Header/param
    /// <i>values</i> are authored placeholders; real auth tokens are injected at runtime by
    /// <see cref="Molca.Networking.Auth.AuthTokenInterceptor"/>, so secrets must not be stored here.
    /// </remarks>
    public static class HttpRequestEditingService
    {
        /// <summary>The fields a caller may set on a request, with the value shape each expects.</summary>
        public static readonly IReadOnlyList<string> WritableFields = new[]
        {
            "name", "method", "url", "useFullUrl", "timeout", "followRedirects", "validateSSL",
            "responseType", "bodyType", "jsonBody", "headers", "queryParams"
        };

        /// <summary>Outcome of an apply: which fields were written and why any were rejected.</summary>
        public sealed class Result
        {
            public readonly List<string> Applied = new List<string>();
            public readonly Dictionary<string, string> Rejected = new Dictionary<string, string>();
        }

        /// <summary>
        /// Applies <paramref name="fields"/> to an existing asset under one Undo group, then marks it
        /// dirty and saves. Returns the applied/rejected breakdown.
        /// </summary>
        public static Result SetFields(HttpRequestAsset asset, JObject fields)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            Undo.RecordObject(asset, "Edit HTTP Request");
            var result = Apply(asset.request ??= new HttpRequest(), fields);

            if (result.Applied.Count > 0)
            {
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssetIfDirty(asset);
            }
            return result;
        }

        /// <summary>
        /// Applies <paramref name="fields"/> to a <see cref="HttpRequest"/> in memory (no Unity
        /// persistence). Shared by <see cref="SetFields"/> and request-asset creation.
        /// </summary>
        public static Result Apply(HttpRequest request, JObject fields)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var result = new Result();
            if (fields == null) return result;

            foreach (var prop in fields.Properties())
            {
                string key = prop.Name;
                JToken value = prop.Value;
                try
                {
                    if (TryApplyOne(request, key, value, out string reason))
                        result.Applied.Add(key);
                    else
                        result.Rejected[key] = reason;
                }
                catch (Exception e)
                {
                    result.Rejected[key] = e.Message;
                }
            }
            return result;
        }

        private static bool TryApplyOne(HttpRequest request, string key, JToken value, out string reason)
        {
            reason = null;
            switch (key)
            {
                case "name": request.name = value.ToString(); return true;
                case "url": request.url = value.ToString(); return true;
                case "jsonBody": request.SetJsonBody(value.ToString()); return true;
                case "useFullUrl": request.useFullUrl = value.ToObject<bool>(); return true;
                case "followRedirects": request.followRedirects = value.ToObject<bool>(); return true;
                case "validateSSL": request.validateSSL = value.ToObject<bool>(); return true;
                case "timeout": request.timeout = value.ToObject<int>(); return true;

                case "method":
                    if (Enum.TryParse<HttpMethod>(value.ToString(), true, out var m)) { request.method = m; return true; }
                    reason = $"Unknown HttpMethod '{value}'. Valid: {string.Join(", ", Enum.GetNames(typeof(HttpMethod)))}";
                    return false;

                case "responseType":
                    if (Enum.TryParse<ResponseType>(value.ToString(), true, out var rt)) { request.expectedResponseType = rt; return true; }
                    reason = $"Unknown ResponseType '{value}'. Valid: {string.Join(", ", Enum.GetNames(typeof(ResponseType)))}";
                    return false;

                case "bodyType":
                    if (Enum.TryParse<BodyType>(value.ToString(), true, out var bt)) { request.bodyType = bt; return true; }
                    reason = $"Unknown BodyType '{value}'. Valid: {string.Join(", ", Enum.GetNames(typeof(BodyType)))}";
                    return false;

                case "headers":
                    request.headers = ReadKeyValueList(value, (k, v, en) => new HttpHeader(k, v) { isEnabled = en });
                    return true;

                case "queryParams":
                    request.queryParams = ReadKeyValueList(value, (k, v, en) => new HttpParam(k, v) { isEnabled = en });
                    return true;

                default:
                    reason = $"Unknown or read-only field. Writable: {string.Join(", ", WritableFields)}";
                    return false;
            }
        }

        // Accepts either a JSON object map ({"Accept":"application/json"}) or an array of
        // {key,value,enabled?} objects. Replaces the whole list (authoring-equivalent to the Inspector).
        private static List<T> ReadKeyValueList<T>(JToken value, Func<string, string, bool, T> factory)
        {
            var list = new List<T>();
            if (value is JObject obj)
            {
                foreach (var p in obj.Properties())
                    list.Add(factory(p.Name, p.Value?.ToString() ?? string.Empty, true));
            }
            else if (value is JArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JObject entry)
                    {
                        string k = entry.Value<string>("key") ?? string.Empty;
                        string v = entry.Value<string>("value") ?? string.Empty;
                        bool en = entry["enabled"]?.ToObject<bool>() ?? true;
                        list.Add(factory(k, v, en));
                    }
                }
            }
            return list;
        }
    }
}
