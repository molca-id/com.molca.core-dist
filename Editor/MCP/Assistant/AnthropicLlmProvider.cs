using System;
using System.Linq;
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
    /// Targets <c>claude-opus-4-8</c> by default. <c>temperature</c> is never sent (Anthropic rejects it
    /// alongside thinking). Extended thinking is opt-in per turn (Sprint 76): when
    /// <see cref="LlmRequest.Reasoning"/> is on and the model supports it, a <c>thinking</c> block with a
    /// scaled <c>budget_tokens</c> is emitted (with the interleaved-thinking beta header) and the returned
    /// thinking blocks are preserved across tool-use turns; otherwise thinking is omitted. The visible answer
    /// is always the text block, never the reasoning.
    /// </remarks>
    public sealed class AnthropicLlmProvider : ILlmProvider
    {
        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private const string AnthropicVersion = "2023-06-01";

        /// <summary>
        /// Beta header enabling interleaved extended thinking across tool-use turns (Sprint 76), sent only when
        /// a thinking budget is actually requested for a capable model.
        /// </summary>
        private const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";

        /// <summary>Output tokens reserved for the visible answer when a thinking budget is fitted under max_tokens.</summary>
        private const int AnswerHeadroomTokens = 1024;

        /// <summary>Anthropic's minimum accepted thinking budget; below this, thinking is skipped entirely.</summary>
        private const int MinThinkingBudget = 1024;

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
            // Extended thinking across tool-use turns (Sprint 76): the interleaved-thinking beta header is sent
            // only when a budget is actually fitted for this request, so a non-reasoning turn is unaffected.
            if (EffectiveThinkingBudget(request) > 0)
                headers["anthropic-beta"] = InterleavedThinkingBeta;

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

        /// <summary>Builds the Anthropic Messages request body; internal for multimodal-translation tests (Sprint 73).</summary>
        internal static string BuildBody(LlmRequest request, bool streaming)
        {
            var root = new JObject
            {
                ["model"] = request.Model,
                ["max_tokens"] = request.MaxTokens
            };
            if (streaming) root["stream"] = true;

            // Extended thinking (Sprint 76): when a budget is fitted for this reasoning-capable model, emit the
            // thinking block. Anthropic requires max_tokens > budget_tokens, guaranteed by EffectiveThinkingBudget.
            var thinkingBudget = EffectiveThinkingBudget(request);
            if (thinkingBudget > 0)
                root["thinking"] = new JObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = thinkingBudget
                };

            // Prompt caching (Sprint 74): mark the stable prefix — tool specs then the system prompt — with
            // ephemeral cache_control breakpoints so a multi-round turn re-sends it as a cache read instead of
            // full-price input. Anthropic's cacheable order is tools → system → messages, and a breakpoint
            // caches everything up to and including its block; marking the LAST tool caches all tools, and
            // marking system caches tools+system (two breakpoints, well within the 4-breakpoint cap). The
            // growing tool-result/user message tail is deliberately left uncached.
            var cache = request.CacheStablePrefix;

            if (!string.IsNullOrEmpty(request.System))
            {
                if (cache)
                    // System must be a content-block array to carry cache_control; a plain string can't.
                    root["system"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = request.System,
                            ["cache_control"] = new JObject { ["type"] = "ephemeral" }
                        }
                    };
                else
                    root["system"] = request.System;
            }

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
                // Breakpoint on the last tool caches the whole tool block; incremental caching still hits the
                // unchanged leading tools when tiered exposure appends a newly-activated tool on a later round.
                if (cache && tools.Count > 0)
                    ((JObject)tools[tools.Count - 1])["cache_control"] = new JObject { ["type"] = "ephemeral" };
                root["tools"] = tools;
            }

            var messages = new JArray();
            foreach (var m in request.Messages)
                messages.Add(BuildMessage(m));
            root["messages"] = messages;

            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// The thinking-token budget actually usable for this request (Sprint 76), or <c>0</c> when reasoning
        /// is off, the model isn't reasoning-capable, or <c>max_tokens</c> is too small to leave answer headroom.
        /// Clamps the nominal level budget below <c>max_tokens</c> so Anthropic's <c>max_tokens &gt; budget_tokens</c>
        /// rule always holds; internal so tests can assert the mapping without a live call.
        /// </summary>
        internal static int EffectiveThinkingBudget(LlmRequest request)
        {
            if (request == null || request.Reasoning == ReasoningEffort.Off) return 0;
            if (!AssistantModelCatalog.IsReasoningModel(LlmProviderKind.Anthropic, request.Model)) return 0;
            var nominal = AssistantSettings.ThinkingBudgetFor(request.Reasoning);
            if (nominal <= 0) return 0;
            var ceiling = request.MaxTokens - AnswerHeadroomTokens;
            if (ceiling < MinThinkingBudget) return 0; // no room for a valid budget + a visible answer
            return Math.Min(nominal, ceiling);
        }

        private static JObject BuildMessage(LlmMessage m)
        {
            var content = new JArray();

            // Preserved reasoning blocks (Sprint 76) must lead the assistant content and be echoed verbatim —
            // including their signature — or Anthropic rejects a tool-use turn that followed thinking. Only
            // assistant turns carry them; user turns never do.
            if (m.Role == LlmRole.Assistant && m.HasThinking)
            {
                foreach (var block in m.ThinkingBlocks)
                {
                    if (block == null) continue;
                    if (block.Kind == LlmThinkingBlockKind.Redacted)
                    {
                        if (!string.IsNullOrEmpty(block.Data))
                            content.Add(new JObject { ["type"] = "redacted_thinking", ["data"] = block.Data });
                    }
                    else if (!string.IsNullOrEmpty(block.Signature))
                    {
                        // A thinking block without its signature can't be replayed; skip it rather than 400.
                        content.Add(new JObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = block.Text ?? string.Empty,
                            ["signature"] = block.Signature
                        });
                    }
                }
            }

            // Image parts (Sprint 73) precede the text block — Anthropic recommends images first for best
            // grounding. Emitted only for a vision-capable model (the controller strips images otherwise, so
            // a non-vision request never carries them).
            if (m.HasImages)
            {
                foreach (var part in m.Content)
                {
                    if (part == null || part.Kind != LlmContentPartKind.Image || string.IsNullOrEmpty(part.Base64Data))
                        continue;
                    content.Add(new JObject
                    {
                        ["type"] = "image",
                        ["source"] = new JObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = string.IsNullOrEmpty(part.MediaType) ? "image/png" : part.MediaType,
                            ["data"] = part.Base64Data
                        }
                    });
                }
            }

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
            var usage = root["usage"];
            // Anthropic reports non-cached input as input_tokens and the cached portions separately (Sprint 74).
            // Fold them into PromptTokens so it stays the full prompt size, and surface the cache counts so cost
            // can bill them at the discounted read / cache-write rates.
            var freshInput = usage?.Value<int>("input_tokens") ?? 0;
            var cacheRead = usage?.Value<int?>("cache_read_input_tokens") ?? 0;
            var cacheWrite = usage?.Value<int?>("cache_creation_input_tokens") ?? 0;
            var response = new LlmResponse
            {
                StopReason = root.Value<string>("stop_reason"),
                PromptTokens = freshInput + cacheRead + cacheWrite,
                CompletionTokens = usage?.Value<int>("output_tokens") ?? 0,
                CacheReadInputTokens = cacheRead,
                CacheCreationInputTokens = cacheWrite
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
                    else if (type == "thinking")
                    {
                        // Preserve reasoning blocks (Sprint 76) so a following tool-use turn can echo them back.
                        response.ThinkingBlocks.Add(LlmThinkingBlock.Thinking(
                            block.Value<string>("thinking"), block.Value<string>("signature")));
                    }
                    else if (type == "redacted_thinking")
                    {
                        response.ThinkingBlocks.Add(LlmThinkingBlock.Redacted(block.Value<string>("data")));
                    }
                }
            }
            response.Text = sb.ToString();
            // Anthropic doesn't report a separate reasoning-token count; estimate it from the thinking text so
            // telemetry can surface the reasoning share (Sprint 76). Redacted blocks contribute nothing readable.
            response.ReasoningTokens = EstimateReasoningTokens(response.ThinkingBlocks);
            return response;
        }

        /// <summary>
        /// Rough reasoning-token estimate (~4 chars/token) from preserved thinking text (Sprint 76), so a
        /// vendor that doesn't report a reasoning count still surfaces a non-zero share. Redacted blocks add 0.
        /// </summary>
        internal static int EstimateReasoningTokens(System.Collections.Generic.List<LlmThinkingBlock> blocks)
        {
            if (blocks == null) return 0;
            var chars = 0;
            foreach (var b in blocks)
                if (b != null && b.Kind == LlmThinkingBlockKind.Thinking) chars += b.Text?.Length ?? 0;
            return chars / 4;
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
            // Reasoning blocks streamed by index (Sprint 76): text arrives via thinking_delta, the signature via
            // signature_delta at the block's close; a redacted block carries its data on content_block_start.
            private readonly System.Collections.Generic.Dictionary<int, PendingThinking> _thinking =
                new System.Collections.Generic.Dictionary<int, PendingThinking>();
            private string _stopReason;
            private int _promptTokens;
            private int _completionTokens;
            private int _cacheReadTokens;
            private int _cacheWriteTokens;

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
                            // input_tokens is the non-cached portion; cache counts arrive here too (Sprint 74).
                            _cacheReadTokens = startUsage.Value<int?>("cache_read_input_tokens") ?? _cacheReadTokens;
                            _cacheWriteTokens = startUsage.Value<int?>("cache_creation_input_tokens") ?? _cacheWriteTokens;
                            _promptTokens = (startUsage.Value<int?>("input_tokens") ?? 0)
                                            + _cacheReadTokens + _cacheWriteTokens;
                            _completionTokens = startUsage.Value<int?>("output_tokens") ?? _completionTokens;
                        }
                        break;

                    case "content_block_start":
                        var block = evt["content_block"];
                        var blockType = block?.Value<string>("type");
                        if (blockType == "tool_use")
                            _tools[evt.Value<int>("index")] = new PendingTool
                            {
                                Id = block.Value<string>("id"),
                                Name = block.Value<string>("name")
                            };
                        else if (blockType == "thinking")
                            _thinking[evt.Value<int>("index")] = new PendingThinking();
                        else if (blockType == "redacted_thinking")
                            _thinking[evt.Value<int>("index")] = new PendingThinking { RedactedData = block.Value<string>("data") };
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
                        // Reasoning deltas (Sprint 76): thinking text is NOT forwarded to the visible stream —
                        // it accumulates for the preserved block and the collapsed "thought" affordance only.
                        else if (deltaType == "thinking_delta" && _thinking.TryGetValue(evt.Value<int>("index"), out var pt))
                        {
                            pt.Text.Append(delta.Value<string>("thinking"));
                        }
                        else if (deltaType == "signature_delta" && _thinking.TryGetValue(evt.Value<int>("index"), out var ps))
                        {
                            ps.Signature = (ps.Signature ?? string.Empty) + (delta.Value<string>("signature") ?? string.Empty);
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
                    CompletionTokens = _completionTokens,
                    CacheReadInputTokens = _cacheReadTokens,
                    CacheCreationInputTokens = _cacheWriteTokens
                };
                foreach (var kv in _tools)
                {
                    var json = kv.Value.Json.Length > 0 ? kv.Value.Json.ToString() : "{}";
                    // Normalize to compact JSON; fall back to "{}" on malformed partials.
                    try { json = JToken.Parse(json).ToString(Newtonsoft.Json.Formatting.None); }
                    catch { json = "{}"; }
                    response.ToolCalls.Add(new LlmToolCall(kv.Value.Id, kv.Value.Name, json));
                }
                // Reassemble reasoning blocks in stream order (Sprint 76). A thinking block missing its
                // signature can't be replayed, so drop it (rare — the signature always closes the block).
                foreach (var index in _thinking.Keys.OrderBy(i => i))
                {
                    var pt = _thinking[index];
                    if (pt.RedactedData != null)
                        response.ThinkingBlocks.Add(LlmThinkingBlock.Redacted(pt.RedactedData));
                    else if (!string.IsNullOrEmpty(pt.Signature))
                        response.ThinkingBlocks.Add(LlmThinkingBlock.Thinking(pt.Text.ToString(), pt.Signature));
                }
                response.ReasoningTokens = EstimateReasoningTokens(response.ThinkingBlocks);
                return response;
            }

            private sealed class PendingTool
            {
                public string Id;
                public string Name;
                public readonly StringBuilder Json = new StringBuilder();
            }

            private sealed class PendingThinking
            {
                public readonly StringBuilder Text = new StringBuilder();
                public string Signature;
                public string RedactedData;
            }
        }
    }
}
