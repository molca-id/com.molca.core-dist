using System;
using System.Collections.Concurrent;

namespace Molca.Editor.Mcp
{
    /// <summary>The decision the guard reaches for a tool invocation.</summary>
    public enum McpActionDecision
    {
        /// <summary>The tool may execute now (read-only, or an allowlisted action with a valid confirmation).</summary>
        Allowed,

        /// <summary>An allowlisted action that needs explicit confirmation; a token has been issued.</summary>
        NeedsConfirmation,

        /// <summary>The action is not on the allowlist and must not run.</summary>
        Refused
    }

    /// <summary>The result of a guard evaluation.</summary>
    public readonly struct McpActionGate
    {
        /// <summary>What the caller should do.</summary>
        public McpActionDecision Decision { get; }
        /// <summary>One-time confirmation token (set only for <see cref="McpActionDecision.NeedsConfirmation"/>).</summary>
        public string ConfirmationToken { get; }
        /// <summary>Human-facing message: the confirmation prompt, or the refusal reason.</summary>
        public string Message { get; }

        internal McpActionGate(McpActionDecision decision, string token, string message)
        {
            Decision = decision;
            ConfirmationToken = token;
            Message = message;
        }
    }

    /// <summary>
    /// Enforces the Sprint 17 guardrails for <see cref="McpToolKind.Action"/> tools: an action runs only
    /// if it is on the allowlist <b>and</b> a one-time, short-lived confirmation token is presented.
    /// Read-only tools are always allowed. The logic is pure (no GUI/transport), so it is shared by every
    /// front-end — the bridge, the in-editor chat, and forks — and is directly unit-testable.
    /// </summary>
    public static class McpActionGuard
    {
        // Issued-but-unused confirmation tokens. One-time: consumed on a successful confirm.
        private static readonly ConcurrentDictionary<string, Pending> Tokens = new ConcurrentDictionary<string, Pending>();

        /// <summary>How long an issued confirmation token remains valid.</summary>
        public static TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

        private readonly struct Pending
        {
            public readonly string Tool;
            public readonly DateTime IssuedUtc;
            public Pending(string tool, DateTime issuedUtc) { Tool = tool; IssuedUtc = issuedUtc; }
        }

        /// <summary>
        /// Evaluates whether a tool may run.
        /// </summary>
        /// <param name="tool">The resolved tool definition.</param>
        /// <param name="isAllowlisted">Whether the tool is on the action allowlist (ignored for read-only tools).</param>
        /// <param name="confirmationToken">A previously-issued token the caller is presenting, or null.</param>
        /// <param name="argumentsJson">The invocation arguments, used to build the confirmation prompt.</param>
        /// <returns>The gate decision.</returns>
        public static McpActionGate Evaluate(McpToolDefinition tool, bool isAllowlisted,
            string confirmationToken, string argumentsJson)
        {
            if (tool == null)
                return new McpActionGate(McpActionDecision.Refused, null, "Unknown tool.");

            // Read-only tools are never gated.
            if (tool.Kind != McpToolKind.Action)
                return new McpActionGate(McpActionDecision.Allowed, null, null);

            if (!isAllowlisted)
                return new McpActionGate(McpActionDecision.Refused, null,
                    $"Action tool '{tool.Name}' is not on the MCP action allowlist. " +
                    "Add it under Project Settings > Molca > MCP to enable it.");

            // Presented a token? Consume it if valid for this tool.
            if (!string.IsNullOrEmpty(confirmationToken)
                && Tokens.TryRemove(confirmationToken, out var pending)
                && pending.Tool == tool.Name
                && DateTime.UtcNow - pending.IssuedUtc <= TokenLifetime)
            {
                return new McpActionGate(McpActionDecision.Allowed, null, null);
            }

            // Otherwise issue a fresh token and ask for confirmation.
            var token = Guid.NewGuid().ToString("N");
            Tokens[token] = new Pending(tool.Name, DateTime.UtcNow);
            PurgeExpired();
            return new McpActionGate(McpActionDecision.NeedsConfirmation, token, BuildPrompt(tool, argumentsJson));
        }

        /// <summary>Builds the human-facing confirmation prompt for an action.</summary>
        public static string BuildPrompt(McpToolDefinition tool, string argumentsJson)
            => McpActionPromptFormatter.BuildConfirmationPrompt(tool, argumentsJson);

        private static void PurgeExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in Tokens)
                if (now - kvp.Value.IssuedUtc > TokenLifetime)
                    Tokens.TryRemove(kvp.Key, out _);
        }
    }
}
