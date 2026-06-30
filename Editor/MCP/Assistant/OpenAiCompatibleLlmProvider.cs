using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// <see cref="ILlmProvider"/> for any OpenAI Chat Completions-compatible endpoint, called over raw
    /// HTTP via <see cref="UnityWebRequest"/>. Works with OpenAI itself and compatible vendors such as
    /// DeepSeek by pointing the base URL at the vendor (e.g. <c>https://api.deepseek.com</c>) and setting
    /// the model (e.g. <c>deepseek-chat</c>). Also drives local/self-hosted runtimes that expose the same
    /// wire format — notably Ollama (<c>http://localhost:11434/v1</c>) — by constructing with
    /// <see cref="LlmProviderKind.Local"/> and an optional (often empty) key. Streaming SSE and
    /// OpenAI-style function calling.
    /// </summary>
    /// <remarks>
    /// Reports its <see cref="Kind"/> as whatever kind it was constructed for (<see cref="LlmProviderKind.OpenAI"/>
    /// or <see cref="LlmProviderKind.Local"/>). This is a deliberately vendor-neutral implementation of the
    /// OpenAI wire format — it is not Anthropic/Claude code. When <c>requireApiKey</c> is <c>false</c> (local
    /// runtimes that don't authenticate), an empty key is allowed and the <c>Authorization</c> header is only
    /// sent when a key is actually present.
    /// </remarks>
    public sealed class OpenAiCompatibleLlmProvider : ILlmProvider
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly LlmProviderKind _kind;
        private readonly bool _requireApiKey;
        private readonly int _maxAttempts;

        /// <summary>
        /// Creates an OpenAI-cloud provider with a base URL (no trailing slash needed) and required API key.
        /// </summary>
        public OpenAiCompatibleLlmProvider(string baseUrl, string apiKey)
            : this(baseUrl, apiKey, LlmProviderKind.OpenAI, requireApiKey: true)
        {
        }

        /// <summary>
        /// Creates the provider for a specific <paramref name="kind"/>, allowing a keyless endpoint when
        /// <paramref name="requireApiKey"/> is <c>false</c> (e.g. a local Ollama server).
        /// </summary>
        /// <param name="baseUrl">OpenAI-compatible base URL (no trailing slash needed).</param>
        /// <param name="apiKey">Bearer key, or empty/null when the endpoint does not authenticate.</param>
        /// <param name="kind">The provider kind this instance reports as.</param>
        /// <param name="requireApiKey">When <c>true</c>, an empty key throws at send time.</param>
        /// <param name="maxAttempts">Maximum total HTTP attempts per call including the first (Sprint 68); <c>1</c> disables retry.</param>
        public OpenAiCompatibleLlmProvider(string baseUrl, string apiKey, LlmProviderKind kind, bool requireApiKey, int maxAttempts = 1)
        {
            _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
            _apiKey = apiKey;
            _kind = kind;
            _requireApiKey = requireApiKey;
            _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
        }

        /// <inheritdoc/>
        public LlmProviderKind Kind => _kind;

        /// <inheritdoc/>
        public async Awaitable<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken, IProgress<string> onTextDelta = null)
        {
            if (_requireApiKey && string.IsNullOrEmpty(_apiKey))
                throw new InvalidOperationException("No API key configured.");
            if (string.IsNullOrEmpty(_baseUrl))
                throw new InvalidOperationException("No base URL configured.");

            var streaming = onTextDelta != null;
            var url = _baseUrl + "/chat/completions";
            var body = BuildBody(request, streaming);
            // Reassignable so a streaming retry (Sprint 68) can drop a partially-filled accumulator and start
            // clean; the SSE callback closes over the variable, not the instance, so it always feeds the live one.
            var accumulator = streaming ? new OpenAiStreamAccumulator(onTextDelta) : null;

            // Pause-independent transport (Sprint 65): HttpClient on a background task pumped via the editor
            // update loop, so the turn streams and completes while Play mode is paused. The SSE callback runs
            // on the main thread.
            // Only authenticate when a key is present: local runtimes (Ollama) reject/ignore a bogus header.
            var headers = new System.Collections.Generic.Dictionary<string, string>();
            if (!string.IsNullOrEmpty(_apiKey))
                headers["Authorization"] = "Bearer " + _apiKey;

            var result = await AssistantHttp.PostAsync(
                url, headers, body, streaming,
                streaming ? (Action<string>)(line => accumulator.OnLine(line)) : null,
                timeoutSeconds: 120, cancellationToken,
                maxAttempts: _maxAttempts,
                onStreamRestart: streaming ? () => accumulator = new OpenAiStreamAccumulator(onTextDelta) : null);

            if (!result.IsSuccess)
                throw new Exception(ExtractError(result.Body, result.StatusCode));

            return streaming ? accumulator.Build() : ParseResponse(result.Body);
        }

        /// <summary>Builds the OpenAI-compatible request body; internal for streaming-options tests (Sprint 68).</summary>
        internal static string BuildBody(LlmRequest request, bool streaming)
        {
            var messages = new JArray();

            // OpenAI takes the system prompt as the first message rather than a top-level field.
            if (!string.IsNullOrEmpty(request.System))
                messages.Add(new JObject { ["role"] = "system", ["content"] = request.System });

            foreach (var m in request.Messages)
            {
                if (m.Role == LlmRole.User)
                {
                    // Tool results become individual "tool" messages (must follow the assistant turn
                    // that requested them — preserved by history append order).
                    if (m.ToolResults != null)
                    {
                        foreach (var r in m.ToolResults)
                        {
                            messages.Add(new JObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = r.ToolCallId,
                                ["content"] = r.Content ?? string.Empty
                            });
                        }
                    }
                    if (!string.IsNullOrEmpty(m.Text))
                        messages.Add(new JObject { ["role"] = "user", ["content"] = m.Text });
                }
                else // Assistant
                {
                    var msg = new JObject { ["role"] = "assistant" };
                    msg["content"] = string.IsNullOrEmpty(m.Text) ? null : m.Text;
                    if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                    {
                        var calls = new JArray();
                        foreach (var c in m.ToolCalls)
                        {
                            calls.Add(new JObject
                            {
                                ["id"] = c.Id,
                                ["type"] = "function",
                                ["function"] = new JObject
                                {
                                    ["name"] = c.Name,
                                    ["arguments"] = string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson
                                }
                            });
                        }
                        msg["tool_calls"] = calls;
                    }
                    messages.Add(msg);
                }
            }

            var root = new JObject
            {
                ["model"] = request.Model,
                ["max_tokens"] = request.MaxTokens,
                ["messages"] = messages
            };
            if (streaming)
            {
                root["stream"] = true;
                // Ask for usage on the terminal SSE chunk (Sprint 68) so streaming turns report real input/output
                // token counts instead of silently billing 0; the accumulator reads it from the final chunk.
                root["stream_options"] = new JObject { ["include_usage"] = true };
            }

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var tools = new JArray();
                foreach (var t in request.Tools)
                {
                    tools.Add(new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["parameters"] = JToken.Parse(string.IsNullOrWhiteSpace(t.InputSchemaJson)
                                ? "{\"type\":\"object\",\"properties\":{}}"
                                : t.InputSchemaJson)
                        }
                    });
                }
                root["tools"] = tools;
                root["tool_choice"] = "auto";
            }

            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Parses a non-streaming OpenAI-compatible response; internal for usage-parsing tests (Sprint 53).</summary>
        internal static LlmResponse ParseResponse(string json)
        {
            var root = JObject.Parse(json);
            var choice = (root["choices"] as JArray)?.Count > 0 ? root["choices"][0] : null;
            var message = choice?["message"];

            var response = new LlmResponse
            {
                StopReason = choice?.Value<string>("finish_reason"),
                Text = message?.Value<string>("content") ?? string.Empty,
                PromptTokens = root["usage"]?.Value<int>("prompt_tokens") ?? 0,
                CompletionTokens = root["usage"]?.Value<int>("completion_tokens") ?? 0
            };

            if (message?["tool_calls"] is JArray toolCalls)
            {
                foreach (var call in toolCalls)
                {
                    var fn = call["function"];
                    response.ToolCalls.Add(new LlmToolCall(
                        call.Value<string>("id"),
                        fn?.Value<string>("name"),
                        fn?.Value<string>("arguments") ?? "{}"));
                }
            }
            return response;
        }

        private static string ExtractError(string body, int statusCode)
        {
            try
            {
                var err = JObject.Parse(body)["error"]?.Value<string>("message");
                if (!string.IsNullOrEmpty(err))
                    return $"LLM API error ({statusCode}): {err}";
            }
            catch { /* fall through */ }
            return $"LLM request failed (HTTP {statusCode}).";
        }

        /// <summary>
        /// Reassembles a non-streaming-equivalent <see cref="LlmResponse"/> from the OpenAI Chat
        /// Completions SSE stream (Sprint 24.7): content deltas forward live; streamed tool_calls are
        /// accumulated by index (id/name arrive once, arguments in pieces) so the round-trip stays correct.
        /// </summary>
        private sealed class OpenAiStreamAccumulator
        {
            private readonly IProgress<string> _onTextDelta;
            private readonly StringBuilder _text = new StringBuilder();
            private readonly System.Collections.Generic.Dictionary<int, PendingTool> _tools =
                new System.Collections.Generic.Dictionary<int, PendingTool>();
            private string _finishReason;
            private int _promptTokens;
            private int _completionTokens;

            public OpenAiStreamAccumulator(IProgress<string> onTextDelta) { _onTextDelta = onTextDelta; }

            public void OnLine(string line)
            {
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data:", StringComparison.Ordinal)) return;
                var payload = line.Substring(5).Trim();
                if (payload.Length == 0 || payload == "[DONE]") return;

                JObject root;
                try { root = JObject.Parse(payload); }
                catch { return; }

                // The terminal chunk (with stream_options.include_usage) carries usage and an empty choices
                // array, so read usage before the choice null-guard below.
                var usage = root["usage"];
                if (usage != null && usage.Type != JTokenType.Null)
                {
                    _promptTokens = usage.Value<int?>("prompt_tokens") ?? _promptTokens;
                    _completionTokens = usage.Value<int?>("completion_tokens") ?? _completionTokens;
                }

                var choice = (root["choices"] as JArray)?.Count > 0 ? root["choices"][0] : null;
                if (choice == null) return;

                var reason = choice.Value<string>("finish_reason");
                if (!string.IsNullOrEmpty(reason)) _finishReason = reason;

                var delta = choice["delta"];
                if (delta == null) return;

                var content = delta.Value<string>("content");
                if (!string.IsNullOrEmpty(content)) { _text.Append(content); _onTextDelta?.Report(content); }

                if (delta["tool_calls"] is JArray toolCalls)
                {
                    foreach (var call in toolCalls)
                    {
                        var index = call.Value<int?>("index") ?? 0;
                        if (!_tools.TryGetValue(index, out var pending))
                        {
                            pending = new PendingTool();
                            _tools[index] = pending;
                        }

                        var id = call.Value<string>("id");
                        if (!string.IsNullOrEmpty(id)) pending.Id = id;

                        var fn = call["function"];
                        var name = fn?.Value<string>("name");
                        if (!string.IsNullOrEmpty(name)) pending.Name = name;
                        pending.Args.Append(fn?.Value<string>("arguments"));
                    }
                }
            }

            public LlmResponse Build()
            {
                var response = new LlmResponse
                {
                    Text = _text.ToString(),
                    StopReason = _finishReason,
                    PromptTokens = _promptTokens,
                    CompletionTokens = _completionTokens
                };
                foreach (var kv in _tools)
                {
                    var args = kv.Value.Args.Length > 0 ? kv.Value.Args.ToString() : "{}";
                    response.ToolCalls.Add(new LlmToolCall(kv.Value.Id, kv.Value.Name, args));
                }
                return response;
            }

            private sealed class PendingTool
            {
                public string Id;
                public string Name;
                public readonly StringBuilder Args = new StringBuilder();
            }
        }
    }
}
