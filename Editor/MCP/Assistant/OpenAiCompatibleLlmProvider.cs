using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// <see cref="ILlmProvider"/> for any OpenAI Chat Completions-compatible endpoint, called over raw
    /// HTTP via <see cref="UnityWebRequest"/>. Works with OpenAI itself and compatible vendors such as
    /// DeepSeek by pointing the base URL at the vendor (e.g. <c>https://api.deepseek.com</c>) and setting
    /// the model (e.g. <c>deepseek-chat</c>). Non-streaming, with OpenAI-style function calling.
    /// </summary>
    /// <remarks>
    /// Reports its <see cref="Kind"/> as <see cref="LlmProviderKind.OpenAI"/>. This is a deliberately
    /// vendor-neutral implementation of the OpenAI wire format — it is not Anthropic/Claude code.
    /// </remarks>
    public sealed class OpenAiCompatibleLlmProvider : ILlmProvider
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;

        /// <summary>Creates the provider with a base URL (no trailing slash needed) and API key.</summary>
        public OpenAiCompatibleLlmProvider(string baseUrl, string apiKey)
        {
            _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
            _apiKey = apiKey;
        }

        /// <inheritdoc/>
        public LlmProviderKind Kind => LlmProviderKind.OpenAI;

        /// <inheritdoc/>
        public async Awaitable<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken, IProgress<string> onTextDelta = null)
        {
            if (string.IsNullOrEmpty(_apiKey))
                throw new InvalidOperationException("No API key configured.");
            if (string.IsNullOrEmpty(_baseUrl))
                throw new InvalidOperationException("No base URL configured.");

            var streaming = onTextDelta != null;
            var url = _baseUrl + "/chat/completions";
            var body = BuildBody(request, streaming);
            var accumulator = streaming ? new OpenAiStreamAccumulator(onTextDelta) : null;

            using var web = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = streaming
                    ? (DownloadHandler)new SseDownloadHandler(accumulator.OnLine)
                    : new DownloadHandlerBuffer(),
                timeout = 120
            };
            web.SetRequestHeader("content-type", "application/json");
            web.SetRequestHeader("Authorization", "Bearer " + _apiKey);

            var op = web.SendWebRequest();
            while (!op.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(cancellationToken);
            }

            if (web.result != UnityWebRequest.Result.Success)
                throw new Exception(ExtractError(streaming ? null : web.downloadHandler.text, web));

            return streaming ? accumulator.Build() : ParseResponse(web.downloadHandler.text);
        }

        private static string BuildBody(LlmRequest request, bool streaming)
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
            if (streaming) root["stream"] = true;

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

        private static string ExtractError(string body, UnityWebRequest web)
        {
            try
            {
                var err = JObject.Parse(body)["error"]?.Value<string>("message");
                if (!string.IsNullOrEmpty(err))
                    return $"LLM API error ({web.responseCode}): {err}";
            }
            catch { /* fall through */ }
            return $"LLM request failed ({web.responseCode}): {web.error}";
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
