using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// <see cref="ILlmProvider"/> backed by the Anthropic Claude Messages API, called over raw HTTP via
    /// <see cref="UnityWebRequest"/> (the editor's networking primitive — the official Anthropic C# SDK
    /// is not cleanly consumable inside a Unity editor assembly). Non-streaming: one request returns one
    /// response, which keeps the tool-use round-trip correct.
    /// </summary>
    /// <remarks>
    /// Targets <c>claude-opus-4-8</c> by default. Per that model's API surface, <c>temperature</c> and
    /// <c>budget_tokens</c> are not sent (they 400); thinking is omitted, and the system prompt asks for
    /// a final-answer-only style so reasoning doesn't leak into the visible reply.
    /// </remarks>
    public sealed class AnthropicLlmProvider : ILlmProvider
    {
        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private const string AnthropicVersion = "2023-06-01";

        private readonly string _apiKey;
        private readonly int _maxAttempts;

        /// <summary>Creates the provider with an API key (resolved by the caller from <see cref="AssistantApiAuth"/>).</summary>
        /// <param name="apiKey">Anthropic API key.</param>
        /// <param name="maxAttempts">Maximum total HTTP attempts per call including the first (Sprint 68); <c>1</c> disables retry.</param>
        public AnthropicLlmProvider(string apiKey, int maxAttempts = 1)
        {
            _apiKey = apiKey;
            _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
        }

        /// <inheritdoc/>
        public LlmProviderKind Kind => LlmProviderKind.Anthropic;

        /// <inheritdoc/>
        public async Awaitable<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken, IProgress<string> onTextDelta = null)
        {
            if (string.IsNullOrEmpty(_apiKey))
                throw new InvalidOperationException("No Anthropic API key configured.");

            var streaming = onTextDelta != null;
            var body = BuildBody(request, streaming);
            // Reassignable so a streaming retry (Sprint 68) starts from a clean accumulator; the SSE callback
            // closes over the variable, not the instance.
            var accumulator = streaming ? new AnthropicStreamAccumulator(onTextDelta) : null;

            // Pause-independent transport (Sprint 65): HttpClient on a background task, pumped via the editor
            // update loop so a turn can stream and complete while Play mode is paused. The SSE line callback
            // is invoked on the main thread, so the accumulator forwards deltas to the UI safely.
            var headers = new System.Collections.Generic.Dictionary<string, string>
            {
                ["x-api-key"] = _apiKey,
                ["anthropic-version"] = AnthropicVersion
            };

            var result = await AssistantHttp.PostAsync(
                Endpoint, headers, body, streaming,
                streaming ? (Action<string>)(line => accumulator.OnLine(line)) : null,
                timeoutSeconds: 120, cancellationToken,
                maxAttempts: _maxAttempts,
                onStreamRestart: streaming ? () => accumulator = new AnthropicStreamAccumulator(onTextDelta) : null);

            if (!result.IsSuccess)
                throw new Exception(ExtractError(result.Body, result.StatusCode));

            return streaming ? accumulator.Build() : ParseResponse(result.Body);
        }

        private static string BuildBody(LlmRequest request, bool streaming)
        {
            var root = new JObject
            {
                ["model"] = request.Model,
                ["max_tokens"] = request.MaxTokens
            };
            if (streaming) root["stream"] = true;
            if (!string.IsNullOrEmpty(request.System))
                root["system"] = request.System;

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var tools = new JArray();
                foreach (var t in request.Tools)
                {
                    tools.Add(new JObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["input_schema"] = JToken.Parse(string.IsNullOrWhiteSpace(t.InputSchemaJson)
                            ? "{\"type\":\"object\",\"properties\":{}}"
                            : t.InputSchemaJson)
                    });
                }
                root["tools"] = tools;
            }

            var messages = new JArray();
            foreach (var m in request.Messages)
                messages.Add(BuildMessage(m));
            root["messages"] = messages;

            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JObject BuildMessage(LlmMessage m)
        {
            var content = new JArray();

            if (!string.IsNullOrEmpty(m.Text))
                content.Add(new JObject { ["type"] = "text", ["text"] = m.Text });

            // Assistant tool_use blocks.
            if (m.Role == LlmRole.Assistant && m.ToolCalls != null)
            {
                foreach (var c in m.ToolCalls)
                {
                    content.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = c.Id,
                        ["name"] = c.Name,
                        ["input"] = JToken.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson)
                    });
                }
            }

            // User tool_result blocks.
            if (m.Role == LlmRole.User && m.ToolResults != null)
            {
                foreach (var r in m.ToolResults)
                {
                    var block = new JObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = r.ToolCallId,
                        ["content"] = r.Content ?? string.Empty
                    };
                    if (r.IsError) block["is_error"] = true;
                    content.Add(block);
                }
            }

            return new JObject
            {
                ["role"] = m.Role == LlmRole.Assistant ? "assistant" : "user",
                ["content"] = content
            };
        }

        /// <summary>Parses a non-streaming Anthropic Messages response; internal for usage-parsing tests (Sprint 53).</summary>
        internal static LlmResponse ParseResponse(string json)
        {
            var root = JObject.Parse(json);
            var response = new LlmResponse
            {
                StopReason = root.Value<string>("stop_reason"),
                PromptTokens = root["usage"]?.Value<int>("input_tokens") ?? 0,
                CompletionTokens = root["usage"]?.Value<int>("output_tokens") ?? 0
            };

            var sb = new StringBuilder();
            if (root["content"] is JArray content)
            {
                foreach (var block in content)
                {
                    var type = block.Value<string>("type");
                    if (type == "text")
                    {
                        sb.Append(block.Value<string>("text"));
                    }
                    else if (type == "tool_use")
                    {
                        response.ToolCalls.Add(new LlmToolCall(
                            block.Value<string>("id"),
                            block.Value<string>("name"),
                            block["input"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}"));
                    }
                }
            }
            response.Text = sb.ToString();
            return response;
        }

        private static string ExtractError(string body, int statusCode)
        {
            try
            {
                var err = JObject.Parse(body)["error"]?.Value<string>("message");
                if (!string.IsNullOrEmpty(err))
                    return $"Anthropic API error ({statusCode}): {err}";
            }
            catch { /* fall through to raw */ }
            return $"Anthropic request failed (HTTP {statusCode}).";
        }

        /// <summary>
        /// Reassembles a non-streaming-equivalent <see cref="LlmResponse"/> from the Anthropic Messages
        /// SSE stream (Sprint 24.7): text deltas are forwarded live while tool_use blocks accumulate their
        /// JSON input so the tool-call round-trip stays correct after the stream ends.
        /// </summary>
        private sealed class AnthropicStreamAccumulator
        {
            private readonly IProgress<string> _onTextDelta;
            private readonly StringBuilder _text = new StringBuilder();
            private readonly System.Collections.Generic.Dictionary<int, PendingTool> _tools =
                new System.Collections.Generic.Dictionary<int, PendingTool>();
            private string _stopReason;
            private int _promptTokens;
            private int _completionTokens;

            public AnthropicStreamAccumulator(IProgress<string> onTextDelta) { _onTextDelta = onTextDelta; }

            public void OnLine(string line)
            {
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data:", StringComparison.Ordinal)) return;
                var payload = line.Substring(5).Trim();
                if (payload.Length == 0 || payload == "[DONE]") return;

                JObject evt;
                try { evt = JObject.Parse(payload); }
                catch { return; }

                switch (evt.Value<string>("type"))
                {
                    case "message_start":
                        // The opening event carries input usage; output usage trickles in via message_delta.
                        var startUsage = evt["message"]?["usage"];
                        if (startUsage != null)
                        {
                            _promptTokens = startUsage.Value<int?>("input_tokens") ?? _promptTokens;
                            _completionTokens = startUsage.Value<int?>("output_tokens") ?? _completionTokens;
                        }
                        break;

                    case "content_block_start":
                        var block = evt["content_block"];
                        if (block?.Value<string>("type") == "tool_use")
                            _tools[evt.Value<int>("index")] = new PendingTool
                            {
                                Id = block.Value<string>("id"),
                                Name = block.Value<string>("name")
                            };
                        break;

                    case "content_block_delta":
                        var delta = evt["delta"];
                        var deltaType = delta?.Value<string>("type");
                        if (deltaType == "text_delta")
                        {
                            var t = delta.Value<string>("text");
                            if (!string.IsNullOrEmpty(t)) { _text.Append(t); _onTextDelta?.Report(t); }
                        }
                        else if (deltaType == "input_json_delta" && _tools.TryGetValue(evt.Value<int>("index"), out var pending))
                        {
                            pending.Json.Append(delta.Value<string>("partial_json"));
                        }
                        break;

                    case "message_delta":
                        var reason = evt["delta"]?.Value<string>("stop_reason");
                        if (!string.IsNullOrEmpty(reason)) _stopReason = reason;
                        // Cumulative output usage is reported on the closing message_delta event.
                        var deltaUsage = evt["usage"];
                        if (deltaUsage != null)
                            _completionTokens = deltaUsage.Value<int?>("output_tokens") ?? _completionTokens;
                        break;
                }
            }

            public LlmResponse Build()
            {
                var response = new LlmResponse
                {
                    Text = _text.ToString(),
                    StopReason = _stopReason,
                    PromptTokens = _promptTokens,
                    CompletionTokens = _completionTokens
                };
                foreach (var kv in _tools)
                {
                    var json = kv.Value.Json.Length > 0 ? kv.Value.Json.ToString() : "{}";
                    // Normalize to compact JSON; fall back to "{}" on malformed partials.
                    try { json = JToken.Parse(json).ToString(Newtonsoft.Json.Formatting.None); }
                    catch { json = "{}"; }
                    response.ToolCalls.Add(new LlmToolCall(kv.Value.Id, kv.Value.Name, json));
                }
                return response;
            }

            private sealed class PendingTool
            {
                public string Id;
                public string Name;
                public readonly StringBuilder Json = new StringBuilder();
            }
        }
    }
}
