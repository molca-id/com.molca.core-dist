using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Molca.Editor.Mcp
{
    /// <summary>Builds readable, redacted confirmation prompts for MCP action tools.</summary>
    public static class McpActionPromptFormatter
    {
        private static readonly Regex SensitiveRegex = new Regex(
            "(?i)(\"(?:api[_-]?key|authorization|authToken|accessToken|refreshToken|token|password|secret)\"\\s*[:=]\\s*)(\"[^\"]*\"|[^,\\s}\\]]+)",
            RegexOptions.Compiled);

        /// <summary>Path where action-tool invocations are audited.</summary>
        public const string AuditLogPath = "Library/Molca/mcp-action-audit.jsonl";

        /// <summary>Builds a multi-line prompt suitable for an editor dialog or MCP elicitation.</summary>
        public static string BuildConfirmationPrompt(McpToolDefinition tool, string argumentsJson)
        {
            if (tool == null) return "Unknown MCP action.";

            return
                $"Run action: {tool.Name}\n" +
                $"{tool.Description}\n" +
                $"Args: {FormatArguments(argumentsJson)}\n" +
                $"Undo: {DescribeReversibility(tool.Reversibility)}\n" +
                $"Audit: {AuditLogPath}\n\n" +
                "Run this action?";
        }

        /// <summary>Builds one compact confirmation prompt for a batch of action calls.</summary>
        public static string BuildBatchConfirmationPrompt(IReadOnlyList<McpActionPromptItem> items)
        {
            if (items == null || items.Count == 0) return "Run these actions?";
            if (items.Count == 1) return BuildConfirmationPrompt(items[0].Tool, items[0].ArgumentsJson);

            var sb = new StringBuilder();
            sb.Append("Run ");
            sb.Append(items.Count);
            sb.AppendLine(" actions?");
            sb.AppendLine($"Audit: {AuditLogPath}");
            sb.AppendLine();

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                sb.Append(i + 1);
                sb.Append(". ");
                sb.Append(item.Tool?.Name ?? "Unknown action");
                sb.Append(" - ");
                sb.Append(FormatArguments(item.ArgumentsJson));
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.Append("Run all?");
            return sb.ToString();
        }

        private static string DescribeReversibility(McpToolReversibility reversibility)
        {
            return reversibility switch
            {
                McpToolReversibility.FileSnapshot => "MCP undo stack",
                McpToolReversibility.UnityUndo => "Unity Undo (Ctrl+Z)",
                _ => "not automatic"
            };
        }

        private static string FormatArguments(string argumentsJson)
            => string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson == "{}"
                ? "(no arguments)"
                : Redact(argumentsJson);

        private static string Redact(string text)
            => SensitiveRegex.Replace(text ?? string.Empty, "$1\"[redacted]\"");
    }

    /// <summary>One action call displayed in a grouped confirmation prompt.</summary>
    public readonly struct McpActionPromptItem
    {
        /// <summary>The action tool being requested.</summary>
        public McpToolDefinition Tool { get; }

        /// <summary>The raw JSON arguments for this call.</summary>
        public string ArgumentsJson { get; }

        /// <summary>Creates one grouped confirmation item.</summary>
        public McpActionPromptItem(McpToolDefinition tool, string argumentsJson)
        {
            Tool = tool;
            ArgumentsJson = argumentsJson ?? "{}";
        }
    }
}
