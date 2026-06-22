using System;
using UnityEngine;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Editor state a tool requires in order to run. The registry gates invocation on this
    /// so a tool that reads runtime data can return a clear "needs Play mode" error instead of
    /// empty results (Sprint 15.7), and a tool that mutates assets is not run mid-play.
    /// </summary>
    public enum McpToolMode
    {
        /// <summary>Runnable regardless of play state (e.g. <c>molca_status</c>).</summary>
        Any,

        /// <summary>Requires the editor to be in Edit mode (not playing).</summary>
        Edit,

        /// <summary>Requires the editor to be in Play mode (live runtime state available).</summary>
        Play
    }

    /// <summary>
    /// Whether a tool only reads editor/runtime state or performs a mutating action.
    /// Action tools are withheld from front-ends until the guardrail track (Sprint 17) exists.
    /// </summary>
    public enum McpToolKind
    {
        /// <summary>Reads state only; safe to expose to any front-end.</summary>
        ReadOnly,

        /// <summary>Mutates project/editor state; requires allowlist + confirmation (Sprint 17).</summary>
        Action
    }

    /// <summary>
    /// How (or whether) an <see cref="McpToolKind.Action"/> tool's effect can be reverted. Surfaced in
    /// the confirmation prompt and used to decide whether a revert path exists (Sprint 17).
    /// </summary>
    public enum McpToolReversibility
    {
        /// <summary>The effect cannot be undone (e.g. a build runs an external process). Irreversible.</summary>
        Irreversible,

        /// <summary>The tool backs up the files it edits (via <see cref="McpUndoStack"/>) and can be reverted.</summary>
        FileSnapshot,

        /// <summary>The tool mutates in-memory objects through Unity's <c>Undo</c> stack (plain Ctrl+Z reverts).</summary>
        UnityUndo
    }

    /// <summary>
    /// The single metadata record describing one MCP tool. Drives mode-gating, the read-only vs.
    /// action guardrails, the bridge transport, and every front-end (IDE proxy and the in-editor
    /// assistant) from one registration.
    /// </summary>
    /// <remarks>
    /// Tool definitions are produced by <see cref="McpToolProvider.GetTools"/> and flattened into a
    /// single registry (<see cref="McpToolRegistry"/>). The <see cref="Execute"/> delegate is always
    /// invoked on the Unity main thread by the bridge — it may freely touch editor and runtime APIs.
    /// Definitions are immutable once constructed.
    /// </remarks>
    public sealed class McpToolDefinition
    {
        /// <summary>
        /// Fully-qualified, globally-unique tool name (e.g. <c>molca_status</c>). By convention the
        /// name is prefixed with the owning provider's <see cref="McpToolProvider.Namespace"/>.
        /// The registry rejects duplicates at load.
        /// </summary>
        public string Name { get; }

        /// <summary>Human/LLM-facing description of what the tool does, used by both front-ends.</summary>
        public string Description { get; }

        /// <summary>
        /// JSON Schema (draft 2020-12 object) describing the tool's input arguments, serialized as a
        /// JSON string. An empty-object schema (<c>{"type":"object","properties":{}}</c>) denotes a
        /// tool that takes no arguments.
        /// </summary>
        public string InputSchemaJson { get; }

        /// <summary>The editor state required to run this tool. See <see cref="McpToolMode"/>.</summary>
        public McpToolMode Mode { get; }

        /// <summary>Read-only vs. action classification. See <see cref="McpToolKind"/>.</summary>
        public McpToolKind Kind { get; }

        /// <summary>
        /// How this action's effect can be reverted. Meaningful only for <see cref="McpToolKind.Action"/>
        /// tools; read-only tools leave it at the default. See <see cref="McpToolReversibility"/>.
        /// </summary>
        public McpToolReversibility Reversibility { get; }

        /// <summary>
        /// Executes the tool synchronously on the Unity main thread. Receives the raw JSON arguments
        /// string (never null; <c>"{}"</c> when no arguments were supplied) and returns a JSON string
        /// result. Null when the tool is asynchronous — see <see cref="ExecuteAsync"/>.
        /// </summary>
        public Func<string, string> Execute { get; }

        /// <summary>
        /// Executes the tool asynchronously on the Unity main thread, for tools whose underlying work
        /// returns an <see cref="Awaitable"/> (e.g. the Doctor run). The bridge awaits this on the main
        /// thread without blocking it. Null when the tool is synchronous — see <see cref="Execute"/>.
        /// </summary>
        public Func<string, Awaitable<string>> ExecuteAsync { get; }

        /// <summary>True if this tool runs asynchronously (<see cref="ExecuteAsync"/> is set).</summary>
        public bool IsAsync => ExecuteAsync != null;

        /// <summary>
        /// Optional bridge invocation timeout override in milliseconds. Values less than or equal to zero
        /// use the bridge default for synchronous/asynchronous tools.
        /// </summary>
        public int InvocationTimeoutMs { get; }

        /// <summary>
        /// Constructs an immutable tool definition.
        /// </summary>
        /// <param name="name">Fully-qualified unique tool name. See <see cref="Name"/>.</param>
        /// <param name="description">Human/LLM-facing description.</param>
        /// <param name="inputSchemaJson">JSON Schema string for the input. See <see cref="InputSchemaJson"/>.</param>
        /// <param name="execute">Main-thread execution delegate. See <see cref="Execute"/>.</param>
        /// <param name="mode">Editor state required to run. Defaults to <see cref="McpToolMode.Any"/>.</param>
        /// <param name="kind">Read-only vs. action. Defaults to <see cref="McpToolKind.ReadOnly"/>.</param>
        /// <param name="reversibility">How an Action tool can be reverted. Ignored for read-only tools.</param>
        /// <param name="invocationTimeoutMs">Optional bridge invocation timeout override in milliseconds.</param>
        /// <exception cref="ArgumentException">Thrown if <paramref name="name"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="execute"/> is null.</exception>
        public McpToolDefinition(
            string name,
            string description,
            string inputSchemaJson,
            Func<string, string> execute,
            McpToolMode mode = McpToolMode.Any,
            McpToolKind kind = McpToolKind.ReadOnly,
            McpToolReversibility reversibility = McpToolReversibility.Irreversible,
            int invocationTimeoutMs = 0)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Tool name must be non-empty.", nameof(name));

            Name = name;
            Description = description ?? string.Empty;
            // Default to an empty-object schema so the transport always serializes a valid schema.
            InputSchemaJson = string.IsNullOrWhiteSpace(inputSchemaJson)
                ? "{\"type\":\"object\",\"properties\":{}}"
                : inputSchemaJson;
            Execute = execute ?? throw new ArgumentNullException(nameof(execute));
            Mode = mode;
            Kind = kind;
            Reversibility = reversibility;
            InvocationTimeoutMs = invocationTimeoutMs;
        }

        /// <summary>
        /// Constructs an immutable asynchronous tool definition.
        /// </summary>
        /// <param name="name">Fully-qualified unique tool name. See <see cref="Name"/>.</param>
        /// <param name="description">Human/LLM-facing description.</param>
        /// <param name="inputSchemaJson">JSON Schema string for the input. See <see cref="InputSchemaJson"/>.</param>
        /// <param name="executeAsync">Main-thread async execution delegate. See <see cref="ExecuteAsync"/>.</param>
        /// <param name="mode">Editor state required to run. Defaults to <see cref="McpToolMode.Any"/>.</param>
        /// <param name="kind">Read-only vs. action. Defaults to <see cref="McpToolKind.ReadOnly"/>.</param>
        /// <param name="reversibility">How an Action tool can be reverted. Ignored for read-only tools.</param>
        /// <param name="invocationTimeoutMs">Optional bridge invocation timeout override in milliseconds.</param>
        /// <exception cref="ArgumentException">Thrown if <paramref name="name"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="executeAsync"/> is null.</exception>
        public McpToolDefinition(
            string name,
            string description,
            string inputSchemaJson,
            Func<string, Awaitable<string>> executeAsync,
            McpToolMode mode = McpToolMode.Any,
            McpToolKind kind = McpToolKind.ReadOnly,
            McpToolReversibility reversibility = McpToolReversibility.Irreversible,
            int invocationTimeoutMs = 0)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Tool name must be non-empty.", nameof(name));

            Name = name;
            Description = description ?? string.Empty;
            InputSchemaJson = string.IsNullOrWhiteSpace(inputSchemaJson)
                ? "{\"type\":\"object\",\"properties\":{}}"
                : inputSchemaJson;
            ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            Mode = mode;
            Kind = kind;
            Reversibility = reversibility;
            InvocationTimeoutMs = invocationTimeoutMs;
        }
    }
}
