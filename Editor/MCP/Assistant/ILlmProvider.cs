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
    /// <see cref="Anthropic"/> is also implemented. <see cref="Local"/> is a reserved seam.
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
        /// <summary>A local/self-hosted endpoint — reserved; not implemented.</summary>
        Local
    }

    /// <summary>The author of a chat message in the provider-neutral conversation model.</summary>
    public enum LlmRole
    {
        /// <summary>An end-user turn.</summary>
        User,
        /// <summary>An assistant (model) turn.</summary>
        Assistant
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

        /// <summary>Creates a simple text message.</summary>
        public static LlmMessage UserText(string text) => new LlmMessage { Role = LlmRole.User, Text = text };
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
