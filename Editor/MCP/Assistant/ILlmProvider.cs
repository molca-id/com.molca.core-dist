using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Which LLM backend the assistant talks to. The chat panel and tool bridge never assume a specific
    /// vendor. <see cref="OpenAI"/> (the OpenAI Chat Completions wire format — OpenAI, DeepSeek, and any
    /// compatible endpoint via a configurable base URL) is the default and the primary option;
    /// <see cref="Anthropic"/> is also implemented. <see cref="Local"/> drives a self-hosted
    /// OpenAI-compatible runtime (e.g. Ollama) over the same wire format with an optional, usually empty key.
    /// </summary>
    /// <remarks>
    /// Numeric order is preserved for serialization stability (do not reorder existing members).
    /// </remarks>
    public enum LlmProviderKind
    {
        /// <summary>OpenAI-compatible Chat Completions (OpenAI, DeepSeek, …). The default.</summary>
        OpenAI,
        /// <summary>Anthropic Claude (Messages API).</summary>
        Anthropic,
        /// <summary>A local/self-hosted OpenAI-compatible endpoint (e.g. Ollama). Keyless by default.</summary>
        Local
    }

    /// <summary>
    /// Provider-neutral reasoning / extended-thinking budget (Sprint 76). Mapped per vendor by each provider:
    /// Anthropic → <c>thinking { type: enabled, budget_tokens }</c> (budget scaled by the level), OpenAI
    /// reasoning models → <c>reasoning_effort</c> (<c>low</c>/<c>medium</c>/<c>high</c>); non-reasoning models
    /// and the Local backend ignore it. <see cref="Off"/> is the default (no reasoning; lowest cost/latency).
    /// </summary>
    /// <remarks>Numeric order is preserved for serialization stability (do not reorder existing members).</remarks>
    public enum ReasoningEffort
    {
        /// <summary>No extended reasoning — the shipped default.</summary>
        Off,
        /// <summary>A small reasoning budget for lightly harder turns.</summary>
        Low,
        /// <summary>A moderate reasoning budget.</summary>
        Medium,
        /// <summary>A large reasoning budget for the hardest multi-step / plan-mode turns.</summary>
        High
    }

    /// <summary>
    /// A single reasoning block returned by a provider that supports extended thinking (Sprint 76). Anthropic
    /// requires these blocks — including their opaque <see cref="Signature"/> — to be echoed back verbatim on
    /// the next request when the assistant turn also called a tool, or the tool-use turn is rejected. Stored on
    /// the assistant <see cref="LlmMessage"/> so the round loop (and a reloaded session) preserves them.
    /// </summary>
    /// <remarks>
    /// The block text is <b>never</b> shown as the visible answer — it may only surface as a collapsed
    /// "thought" affordance. A <see cref="LlmThinkingBlockKind.Redacted"/> block carries only opaque
    /// <see cref="Data"/> (the vendor encrypted it); it is round-tripped but has no readable text.
    /// </remarks>
    [Serializable]
    public sealed class LlmThinkingBlock
    {
        /// <summary>Whether this is a readable thinking block or an opaque redacted one.</summary>
        public LlmThinkingBlockKind Kind { get; set; }

        /// <summary>The reasoning text for a <see cref="LlmThinkingBlockKind.Thinking"/> block; empty when redacted.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Anthropic's cryptographic signature for the block, echoed back verbatim across tool-use turns.</summary>
        public string Signature { get; set; }

        /// <summary>Opaque encrypted payload for a <see cref="LlmThinkingBlockKind.Redacted"/> block.</summary>
        public string Data { get; set; }

        /// <summary>Creates a readable thinking block.</summary>
        public static LlmThinkingBlock Thinking(string text, string signature)
            => new LlmThinkingBlock { Kind = LlmThinkingBlockKind.Thinking, Text = text ?? string.Empty, Signature = signature };

        /// <summary>Creates a redacted (opaque) thinking block.</summary>
        public static LlmThinkingBlock Redacted(string data)
            => new LlmThinkingBlock { Kind = LlmThinkingBlockKind.Redacted, Data = data };
    }

    /// <summary>Whether a <see cref="LlmThinkingBlock"/> carries readable text or an opaque redacted payload.</summary>
    /// <remarks>Numeric order is preserved for serialization stability (do not reorder existing members).</remarks>
    public enum LlmThinkingBlockKind
    {
        /// <summary>A readable reasoning block (text + signature).</summary>
        Thinking,
        /// <summary>An opaque, vendor-encrypted reasoning block (data only).</summary>
        Redacted
    }

    /// <summary>The author of a chat message in the provider-neutral conversation model.</summary>
    public enum LlmRole
    {
        /// <summary>An end-user turn.</summary>
        User,
        /// <summary>An assistant (model) turn.</summary>
        Assistant
    }

    /// <summary>What a single <see cref="LlmContentPart"/> carries (Sprint 73 — multimodal input).</summary>
    /// <remarks>Numeric order is preserved for serialization stability (do not reorder existing members).</remarks>
    public enum LlmContentPartKind
    {
        /// <summary>A run of plain text.</summary>
        Text,
        /// <summary>A base64-encoded image (see <see cref="LlmContentPart.MediaType"/>).</summary>
        Image
    }

    /// <summary>
    /// One ordered part of a multimodal message (Sprint 73). A message's parts model image input alongside
    /// its text; the parts list is the ordered image payload, while a message's visible text stays on
    /// <see cref="LlmMessage.Text"/> (the back-compat convenience every existing text-only path relies on).
    /// </summary>
    /// <remarks>
    /// In current use the controller populates <see cref="LlmContentPartKind.Image"/> parts only and leaves
    /// the message text on <see cref="LlmMessage.Text"/>; providers emit the images from these parts and the
    /// text from <see cref="LlmMessage.Text"/>. The <see cref="LlmContentPartKind.Text"/> kind exists for
    /// completeness and future interleaving. Images are held as base64 so a message round-trips through JSON
    /// session persistence unchanged.
    /// </remarks>
    [Serializable]
    public sealed class LlmContentPart
    {
        /// <summary>Whether this part is text or an image.</summary>
        public LlmContentPartKind Kind { get; set; }

        /// <summary>Text run for a <see cref="LlmContentPartKind.Text"/> part; <c>null</c> for images.</summary>
        public string Text { get; set; }

        /// <summary>Image media type (e.g. <c>image/png</c>, <c>image/jpeg</c>) for an image part.</summary>
        public string MediaType { get; set; }

        /// <summary>Base64-encoded image bytes for an image part (no <c>data:</c> prefix); <c>null</c> for text.</summary>
        public string Base64Data { get; set; }

        /// <summary>Optional pixel width, used for a per-image token estimate; <c>0</c> when unknown.</summary>
        public int PixelWidth { get; set; }

        /// <summary>Optional pixel height, used for a per-image token estimate; <c>0</c> when unknown.</summary>
        public int PixelHeight { get; set; }

        /// <summary>Creates a text part.</summary>
        public static LlmContentPart FromText(string text)
            => new LlmContentPart { Kind = LlmContentPartKind.Text, Text = text ?? string.Empty };

        /// <summary>Creates an image part from base64 bytes and a media type.</summary>
        public static LlmContentPart FromImage(string base64, string mediaType, int pixelWidth = 0, int pixelHeight = 0)
            => new LlmContentPart
            {
                Kind = LlmContentPartKind.Image,
                Base64Data = base64 ?? string.Empty,
                MediaType = string.IsNullOrEmpty(mediaType) ? "image/png" : mediaType,
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight
            };
    }

    /// <summary>
    /// A tool the model may call, in a provider-neutral shape. Built from an
    /// <see cref="McpToolDefinition"/> by <see cref="AssistantToolBridge"/> and translated to each
    /// vendor's function-calling format by the provider.
    /// </summary>
    public sealed class LlmToolSpec
    {
        /// <summary>Fully-qualified tool name (matches the registry tool name).</summary>
        public string Name { get; }
        /// <summary>Human/LLM-facing description.</summary>
        public string Description { get; }
        /// <summary>JSON Schema (object) for the tool input, as a JSON string.</summary>
        public string InputSchemaJson { get; }

        /// <summary>Creates a tool spec.</summary>
        public LlmToolSpec(string name, string description, string inputSchemaJson)
        {
            Name = name;
            Description = description;
            InputSchemaJson = inputSchemaJson;
        }
    }

    /// <summary>A model request to call a tool, surfaced from a provider response.</summary>
    public sealed class LlmToolCall
    {
        /// <summary>Vendor-assigned id used to correlate the result back to the call.</summary>
        public string Id { get; }
        /// <summary>The tool name the model wants to invoke.</summary>
        public string Name { get; }
        /// <summary>The raw JSON arguments object the model supplied.</summary>
        public string ArgumentsJson { get; }

        /// <summary>Creates a tool call.</summary>
        public LlmToolCall(string id, string name, string argumentsJson)
        {
            Id = id;
            Name = name;
            ArgumentsJson = argumentsJson;
        }
    }

    /// <summary>The result of executing a tool, fed back to the model on the next turn.</summary>
    public sealed class LlmToolResult
    {
        /// <summary>The <see cref="LlmToolCall.Id"/> this result answers.</summary>
        public string ToolCallId { get; }
        /// <summary>The tool's result payload (JSON string or plain text).</summary>
        public string Content { get; }
        /// <summary>True if the tool failed; surfaced to the model as an error result.</summary>
        public bool IsError { get; }

        /// <summary>Creates a tool result.</summary>
        public LlmToolResult(string toolCallId, string content, bool isError = false)
        {
            ToolCallId = toolCallId;
            Content = content;
            IsError = isError;
        }
    }

    /// <summary>
    /// One conversation turn. An assistant turn may carry both visible <see cref="Text"/> and one or
    /// more <see cref="ToolCalls"/>; a user turn may carry <see cref="Text"/> and/or
    /// <see cref="ToolResults"/> answering the previous assistant turn's calls.
    /// </summary>
    public sealed class LlmMessage
    {
        /// <summary>Who authored the turn.</summary>
        public LlmRole Role { get; set; }
        /// <summary>Visible text, if any.</summary>
        public string Text { get; set; }
        /// <summary>Tool calls requested by the model (assistant turns only).</summary>
        public List<LlmToolCall> ToolCalls { get; set; } = new List<LlmToolCall>();
        /// <summary>Tool results supplied by the harness (user turns only).</summary>
        public List<LlmToolResult> ToolResults { get; set; } = new List<LlmToolResult>();

        /// <summary>
        /// Ordered multimodal content parts for this message (Sprint 73) — in current use the image payload
        /// of a user turn. Empty for the common text-only message; <see cref="Text"/> stays authoritative for
        /// the message's visible text regardless.
        /// </summary>
        public List<LlmContentPart> Content { get; set; } = new List<LlmContentPart>();

        /// <summary>
        /// Reasoning blocks this assistant turn returned (Sprint 76), preserved so a tool-use turn can echo
        /// them back verbatim (Anthropic's requirement) and a reloaded session keeps them. Empty on user turns
        /// and on any turn from a provider that doesn't emit reasoning. Never rendered as the visible answer.
        /// </summary>
        public List<LlmThinkingBlock> ThinkingBlocks { get; set; } = new List<LlmThinkingBlock>();

        /// <summary>True when this message carries at least one preserved reasoning block (Sprint 76).</summary>
        public bool HasThinking => ThinkingBlocks != null && ThinkingBlocks.Count > 0;

        /// <summary>True when this message carries at least one image part (Sprint 73).</summary>
        public bool HasImages
        {
            get
            {
                if (Content == null) return false;
                foreach (var part in Content)
                    if (part != null && part.Kind == LlmContentPartKind.Image) return true;
                return false;
            }
        }

        /// <summary>Creates a simple text message.</summary>
        public static LlmMessage UserText(string text) => new LlmMessage { Role = LlmRole.User, Text = text };

        /// <summary>
        /// Creates a user message carrying <paramref name="text"/> plus one or more image parts (Sprint 73).
        /// The text stays on <see cref="Text"/>; the images populate <see cref="Content"/>.
        /// </summary>
        public static LlmMessage UserMultimodal(string text, IEnumerable<LlmContentPart> imageParts)
        {
            var message = new LlmMessage { Role = LlmRole.User, Text = text };
            if (imageParts != null)
                foreach (var part in imageParts)
                    if (part != null && part.Kind == LlmContentPartKind.Image)
                        message.Content.Add(part);
            return message;
        }
    }

    /// <summary>A full request to the model for one round-trip.</summary>
    public sealed class LlmRequest
    {
        /// <summary>System prompt establishing the assistant's behavior.</summary>
        public string System { get; set; }
        /// <summary>The conversation so far.</summary>
        public List<LlmMessage> Messages { get; set; } = new List<LlmMessage>();
        /// <summary>Tools the model may call (read-only registry tools).</summary>
        public List<LlmToolSpec> Tools { get; set; } = new List<LlmToolSpec>();
        /// <summary>Model id (e.g. claude-opus-4-8).</summary>
        public string Model { get; set; }
        /// <summary>Output token ceiling.</summary>
        public int MaxTokens { get; set; } = 16000;

        /// <summary>
        /// When <c>true</c>, ask the provider to treat the stable request prefix — the system prompt plus the
        /// tool specs — as cacheable (Sprint 74). Anthropic emits explicit <c>cache_control</c> breakpoints on
        /// that prefix so a multi-round turn re-sends it as a cache read instead of full-price input;
        /// OpenAI-compatible endpoints cache automatically past their prefix threshold and ignore this flag
        /// (there the controller's job is prefix stability, not markers); the Local backend no-ops. Additive
        /// and defaulted <c>false</c>, so a caller that doesn't opt in is unaffected.
        /// </summary>
        public bool CacheStablePrefix { get; set; }

        /// <summary>
        /// Requested reasoning / extended-thinking budget for this turn (Sprint 76), mapped per vendor by the
        /// provider. Defaulted to <see cref="ReasoningEffort.Off"/> so a caller that doesn't opt in is
        /// unaffected; a provider ignores it for a non-reasoning model and for the Local backend.
        /// </summary>
        public ReasoningEffort Reasoning { get; set; } = ReasoningEffort.Off;
    }

    /// <summary>The model's response for one round-trip.</summary>
    public sealed class LlmResponse
    {
        /// <summary>Visible assistant text (may be empty when the model only called tools).</summary>
        public string Text { get; set; } = string.Empty;
        /// <summary>Tool calls the model requested this turn.</summary>
        public List<LlmToolCall> ToolCalls { get; set; } = new List<LlmToolCall>();
        /// <summary>Vendor stop reason (e.g. "tool_use", "end_turn").</summary>
        public string StopReason { get; set; }

        /// <summary>
        /// Prompt (input) tokens the vendor reported for this request, or <c>0</c> when not reported
        /// (e.g. streaming) (Sprint 25.8). The controller caches the latest non-zero value to show a real
        /// token count instead of the character-count heuristic.
        /// </summary>
        public int PromptTokens { get; set; }

        /// <summary>
        /// Completion (output) tokens the vendor reported for this response, or <c>0</c> when not reported
        /// (Sprint 53). When present, the controller bills this real count instead of the <c>~4 chars/token</c>
        /// estimate; streaming responses report it on the terminal usage event when the vendor sends one.
        /// </summary>
        public int CompletionTokens { get; set; }

        /// <summary>
        /// Input tokens served from the provider's prompt cache at the discounted cache-read rate (Sprint 74),
        /// or <c>0</c> when caching was off, unsupported, or missed. Anthropic reports this as
        /// <c>cache_read_input_tokens</c>; OpenAI-compatible endpoints as
        /// <c>prompt_tokens_details.cached_tokens</c>. Counted distinctly so the cost estimate can bill it at
        /// the cached rate. Included in <see cref="PromptTokens"/> so that value stays the full prompt size.
        /// </summary>
        public int CacheReadInputTokens { get; set; }

        /// <summary>
        /// Input tokens written into the provider's prompt cache by this request, billed at the cache-write
        /// rate (Sprint 74), or <c>0</c>. Anthropic reports this as <c>cache_creation_input_tokens</c>;
        /// OpenAI-compatible endpoints don't bill cache writes and always report <c>0</c> here. Included in
        /// <see cref="PromptTokens"/> so that value stays the full prompt size.
        /// </summary>
        public int CacheCreationInputTokens { get; set; }

        /// <summary>
        /// Reasoning (thinking) output tokens the vendor reported for this response (Sprint 76), or <c>0</c>
        /// when reasoning was off/unsupported/unreported. A subset of <see cref="CompletionTokens"/> (reasoning
        /// bills as output), surfaced distinctly so telemetry can show the reasoning share of the spend.
        /// Anthropic doesn't report a separate count, so it is estimated from the returned thinking text;
        /// OpenAI reports it as <c>completion_tokens_details.reasoning_tokens</c>.
        /// </summary>
        public int ReasoningTokens { get; set; }

        /// <summary>
        /// Reasoning blocks the model returned this turn (Sprint 76), preserved so a tool-use turn can echo
        /// them back. Anthropic only; empty otherwise. Never the visible answer.
        /// </summary>
        public List<LlmThinkingBlock> ThinkingBlocks { get; set; } = new List<LlmThinkingBlock>();

        /// <summary>True if the model asked to call at least one tool.</summary>
        public bool WantsToolUse => ToolCalls != null && ToolCalls.Count > 0;
    }

    /// <summary>
    /// Pluggable LLM backend behind one function-calling shape (Sprint 16.1). The chat controller and
    /// tool bridge depend only on this interface, so swapping Claude for another vendor is a provider
    /// implementation change, not a retrofit.
    /// </summary>
    public interface ILlmProvider
    {
        /// <summary>Which backend this implements.</summary>
        LlmProviderKind Kind { get; }

        /// <summary>
        /// Sends one request and returns the model's response. Implementations run on the editor's async
        /// context and must honor <paramref name="cancellationToken"/>.
        /// </summary>
        /// <param name="onTextDelta">
        /// Optional streaming sink (Sprint 24.7). When non-null, the provider streams assistant text via
        /// SSE and reports incremental deltas here; the returned <see cref="LlmResponse"/> still carries
        /// the full text and any tool calls so the tool-use round-trip stays correct. When null, the
        /// provider makes a single non-streaming request.
        /// </param>
        Awaitable<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken, IProgress<string> onTextDelta = null);
    }
}
