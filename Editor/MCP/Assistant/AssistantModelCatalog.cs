using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Provider-aware model discovery for the in-window model picker (Sprint 71). For the
    /// <see cref="LlmProviderKind.Local"/> backend it queries the running endpoint for pulled models
    /// (Ollama <c>GET {root}/api/tags</c>, degrading to the OpenAI-compatible <c>GET {baseUrl}/models</c>);
    /// for cloud providers it returns a curated, extensible known-model list. Every path degrades to
    /// free-text (an empty list is not an error), so the picker always works even when the endpoint is down.
    /// </summary>
    /// <remarks>
    /// Editor-only. Discovery is async, cancellable, and briefly cached per (provider, base URL) so
    /// re-opening the dropdown doesn't re-hit the endpoint. API keys are read from
    /// <see cref="AssistantApiAuth"/> only to authorize a secured local endpoint — the catalog never stores
    /// or surfaces a key. Parsing is factored into pure static methods (<see cref="ParseOllamaTags"/> /
    /// <see cref="ParseOpenAiModels"/>) so it is unit-testable without a live server.
    /// </remarks>
    public static class AssistantModelCatalog
    {
        /// <summary>Discovery timeout; short so an unreachable local endpoint fails fast (seconds).</summary>
        private const int DiscoveryTimeoutSeconds = 4;

        /// <summary>Cache lifetime for a discovery result, in seconds.</summary>
        private const double CacheSeconds = 20.0;

        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>();

        /// <summary>
        /// Curated, extensible known-model lists for the cloud providers. The first entry is a sensible
        /// default; the list is not exhaustive — the picker always keeps a free-text field for anything
        /// unlisted (a new tag, a fine-tune, a DeepSeek model behind the OpenAI base URL).
        /// </summary>
        public static IReadOnlyList<string> CuratedModelsFor(LlmProviderKind provider) => provider switch
        {
            LlmProviderKind.Anthropic => new[]
            {
                "claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5-20251001",
                "claude-3-5-sonnet-latest", "claude-3-5-haiku-latest"
            },
            LlmProviderKind.OpenAI => new[]
            {
                "gpt-4o-mini", "gpt-4o", "gpt-4.1", "gpt-4.1-mini", "o3-mini",
                // DeepSeek shares the OpenAI wire format (via a custom base URL); listed for convenience.
                "deepseek-chat", "deepseek-reasoner"
            },
            _ => Array.Empty<string>()
        };

        /// <summary>
        /// Discovers the models available for <paramref name="settings"/>' current provider. Cloud providers
        /// return their curated list immediately; <see cref="LlmProviderKind.Local"/> queries the endpoint.
        /// </summary>
        /// <param name="settings">Source of provider, base URL, and (for a secured endpoint) the key.</param>
        /// <param name="forceRefresh">Bypass the brief cache (the "Detect local models" action).</param>
        /// <param name="cancellationToken">Cancels an in-flight local discovery request.</param>
        public static async Awaitable<ModelCatalogResult> DiscoverAsync(
            AssistantSettings settings, bool forceRefresh, CancellationToken cancellationToken)
        {
            var provider = settings.Provider;
            if (provider != LlmProviderKind.Local)
            {
                var curated = CuratedModelsFor(provider);
                return new ModelCatalogResult(curated, reachable: true, fromNetwork: false,
                    curated.Count > 0 ? $"{curated.Count} known models" : "Enter a model id");
            }

            var baseUrl = settings.BaseUrl;
            var cacheKey = provider + "|" + baseUrl;
            if (!forceRefresh && Cache.TryGetValue(cacheKey, out var cached) &&
                EditorApplication.timeSinceStartup - cached.StampedAt < CacheSeconds)
                return cached.Result;

            var result = await DiscoverLocalAsync(baseUrl, AssistantApiAuth.GetKey(provider), cancellationToken);
            Cache[cacheKey] = new CacheEntry { Result = result, StampedAt = EditorApplication.timeSinceStartup };
            return result;
        }

        /// <summary>Clears the discovery cache (e.g. after the base URL changes in the Hub).</summary>
        public static void InvalidateCache() => Cache.Clear();

        /// <summary>
        /// Queries a local OpenAI-compatible runtime for its pulled models: Ollama's native
        /// <c>{root}/api/tags</c> first (richest), then the OpenAI-compatible <c>{baseUrl}/models</c>. An
        /// unreachable endpoint or an empty catalog yields a reachable=false / empty result with an
        /// actionable hint, never an exception.
        /// </summary>
        private static async Awaitable<ModelCatalogResult> DiscoverLocalAsync(
            string baseUrl, string key, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(key)) headers["Authorization"] = "Bearer " + key;

            // Ollama's tag API lives at the server root, not under the OpenAI-compat /v1 prefix.
            var root = StripOpenAiSuffix(baseUrl);
            var tagsResult = await AssistantHttp.GetAsync(
                CombineUrl(root, "api/tags"), headers, DiscoveryTimeoutSeconds, cancellationToken);
            if (tagsResult.IsSuccess)
            {
                var models = ParseOllamaTags(tagsResult.Body);
                if (models.Count > 0)
                    return new ModelCatalogResult(models, reachable: true, fromNetwork: true,
                        $"Ollama reachable — {models.Count} model(s) pulled");
                return new ModelCatalogResult(models, reachable: true, fromNetwork: true,
                    "Ollama reachable, but no models pulled. Pull one, e.g. `ollama pull gemma4:e4b`.");
            }

            // Fallback to the OpenAI-compatible /models list (some local runtimes expose only this).
            var modelsResult = await AssistantHttp.GetAsync(
                CombineUrl(baseUrl, "models"), headers, DiscoveryTimeoutSeconds, cancellationToken);
            if (modelsResult.IsSuccess)
            {
                var models = ParseOpenAiModels(modelsResult.Body);
                return new ModelCatalogResult(models, reachable: true, fromNetwork: true,
                    models.Count > 0 ? $"Reachable — {models.Count} model(s)" : "Reachable, but no models listed.");
            }

            return new ModelCatalogResult(Array.Empty<string>(), reachable: false, fromNetwork: true,
                $"No local runtime at {baseUrl}. Start Ollama (`ollama serve`) and pull a model, then Detect again.");
        }

        /// <summary>
        /// Parses an Ollama <c>/api/tags</c> body (<c>{"models":[{"name":"gemma4:e4b"},…]}</c>) into a
        /// de-duplicated, ordered list of model tags. Returns an empty list for null/blank/malformed input.
        /// </summary>
        public static IReadOnlyList<string> ParseOllamaTags(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            try
            {
                var array = JObject.Parse(json)["models"] as JArray;
                if (array == null) return Array.Empty<string>();
                return Dedup(array.Select(m => (string)m["name"] ?? (string)m["model"]));
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>
        /// Parses an OpenAI-compatible <c>/models</c> body (<c>{"data":[{"id":"…"},…]}</c>) into a
        /// de-duplicated, ordered list of model ids. Returns an empty list for null/blank/malformed input.
        /// </summary>
        public static IReadOnlyList<string> ParseOpenAiModels(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            try
            {
                var array = JObject.Parse(json)["data"] as JArray;
                if (array == null) return Array.Empty<string>();
                return Dedup(array.Select(m => (string)m["id"]));
            }
            catch { return Array.Empty<string>(); }
        }

        private static IReadOnlyList<string> Dedup(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                ordered.Add(name.Trim());
            }
            return ordered;
        }

        /// <summary>Strips a trailing <c>/v1</c> (with optional trailing slash) so Ollama's root APIs resolve.</summary>
        internal static string StripOpenAiSuffix(string baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).TrimEnd('/');
            return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring(0, trimmed.Length - 3).TrimEnd('/')
                : trimmed;
        }

        private static string CombineUrl(string root, string path)
            => (root ?? string.Empty).TrimEnd('/') + "/" + path.TrimStart('/');

        /// <summary>
        /// Writes <paramref name="provider"/> and <paramref name="model"/> onto <paramref name="settings"/>
        /// through the same <see cref="SerializedObject"/> path the Hub uses (Sprint 71), so
        /// <see cref="ToolCallTransport.Auto"/>/<see cref="ToolExposureMode.Auto"/> re-resolve and the change
        /// survives restarts. A blank model clears the field so the provider default applies.
        /// </summary>
        public static void ApplySelection(AssistantSettings settings, LlmProviderKind provider, string model)
        {
            if (settings == null) return;
            var so = new SerializedObject(settings);
            so.FindProperty("provider").enumValueIndex = (int)provider;
            so.FindProperty("model").stringValue = model?.Trim() ?? string.Empty;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            InvalidateCache();
        }

        private struct CacheEntry
        {
            public ModelCatalogResult Result;
            public double StampedAt;
        }
    }

    /// <summary>
    /// The outcome of a model-discovery pass (Sprint 71): the model ids to offer, whether the endpoint was
    /// reachable (local only), whether the list came from the network vs a curated list, and a short status
    /// message for the picker's hint line.
    /// </summary>
    public sealed class ModelCatalogResult
    {
        /// <summary>Discovered/known model ids to offer in the dropdown (may be empty → free-text only).</summary>
        public IReadOnlyList<string> Models { get; }

        /// <summary>Whether the discovery endpoint was reachable. Always true for a curated cloud list.</summary>
        public bool Reachable { get; }

        /// <summary>True when the list was discovered from a live endpoint, false for a curated cloud list.</summary>
        public bool FromNetwork { get; }

        /// <summary>Short human-readable status/hint for the picker.</summary>
        public string Message { get; }

        /// <summary>Creates a discovery result.</summary>
        public ModelCatalogResult(IReadOnlyList<string> models, bool reachable, bool fromNetwork, string message)
        {
            Models = models ?? Array.Empty<string>();
            Reachable = reachable;
            FromNetwork = fromNetwork;
            Message = message ?? string.Empty;
        }
    }
}
