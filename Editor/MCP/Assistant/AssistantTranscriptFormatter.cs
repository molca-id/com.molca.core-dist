using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Assistant-specific transcript helpers: secret redaction, tool-call summaries, and plain-text export
    /// for clipboard/copy use. Markdown parsing and rendering now live in the shared
    /// <see cref="Molca.Editor.UI.MolcaMarkdown"/> utility (Editor/UI/) so any Hub or fork surface can render
    /// Markdown; the transcript view delegates to it. This class keeps only what is specific to the
    /// assistant's transcript model (<see cref="ChatTurn"/>/<see cref="ChatToolSummary"/>).
    /// </summary>
    public static class AssistantTranscriptFormatter
    {
        private static readonly Regex SensitiveJsonRegex = new Regex(
            "(?i)(\"(?:api[_-]?key|authorization|authToken|accessToken|refreshToken|token|password|secret)\"\\s*[:=]\\s*)(\"[^\"]*\"|[^,\\s}\\]]+)",
            RegexOptions.Compiled);

        private static readonly Regex SensitivePlainKeyRegex = new Regex(
            "(?i)\\b(?:api[_-]?key|authorization|authToken|accessToken|refreshToken|password|secret)\\b\\s*[:=]\\s*(\"[^\"]*\"|[^,\\s}\\]]+)",
            RegexOptions.Compiled);

        private static readonly Regex BearerRegex = new Regex(
            "(?i)\\bBearer\\s+[A-Za-z0-9._\\-+/=]+",
            RegexOptions.Compiled);

        private static readonly Regex JsonErrorRegex = new Regex(
            "\"error\"\\s*:\\s*\"([^\"]*)\"",
            RegexOptions.Compiled);

        /// <summary>Builds a compact visible summary for a completed tool call.</summary>
        public static string FormatToolSummary(ChatToolSummary summary)
        {
            if (summary == null) return "Tool result unavailable.";

            if (!summary.IsError) return "Completed.";

            var message = ExtractErrorMessage(summary.ResultContent);
            return string.IsNullOrWhiteSpace(message)
                ? "Failed."
                : "Failed.\n" + Truncate(RedactSecrets(message), 180);
        }

        /// <summary>Builds one compact visible summary for a completed chain of tool calls.</summary>
        public static string FormatToolChainSummary(IReadOnlyList<ChatToolSummary> summaries)
        {
            if (summaries == null || summaries.Count == 0) return "No tools ran.";
            if (summaries.Count == 1) return FormatToolSummary(summaries[0]);

            var failed = 0;
            for (var i = 0; i < summaries.Count; i++)
                if (summaries[i]?.IsError == true)
                    failed++;

            var sb = new StringBuilder();
            sb.Append(failed == 0
                ? $"Completed {summaries.Count} tool calls."
                : $"Ran {summaries.Count} tool calls: {failed} failed, {summaries.Count - failed} completed.");

            for (var i = 0; i < summaries.Count; i++)
            {
                var summary = summaries[i];
                if (summary == null) continue;

                sb.AppendLine();
                sb.Append("- ");
                sb.Append(string.IsNullOrWhiteSpace(summary.Name) ? "tool" : summary.Name);
                sb.Append(summary.IsError ? ": failed" : ": completed");

                if (!summary.IsError) continue;
                var message = ExtractErrorMessage(summary.ResultContent);
                if (!string.IsNullOrWhiteSpace(message))
                    sb.Append(" - ").Append(Truncate(RedactSecrets(NormalizeSingleLine(message)), 140));
            }

            return sb.ToString();
        }

        /// <summary>Copies the last assistant answer as redacted plain text.</summary>
        public static string LastAssistantAnswer(IEnumerable<ChatTurn> transcript)
        {
            if (transcript == null) return string.Empty;

            string last = string.Empty;
            foreach (var turn in transcript)
                if (turn != null && turn.Kind == ChatTurnKind.Assistant)
                    last = turn.Text;
            return RedactSecrets(last);
        }

        /// <summary>Converts a transcript to redacted plain text for clipboard/export use.</summary>
        public static string ToPlainText(IEnumerable<ChatTurn> transcript, bool includeToolPayloads = false)
        {
            if (transcript == null) return string.Empty;

            var sb = new StringBuilder();
            foreach (var turn in transcript)
            {
                if (turn == null) continue;
                if (sb.Length > 0) sb.AppendLine().AppendLine();

                sb.Append(turn.Kind switch
                {
                    ChatTurnKind.User => "You",
                    ChatTurnKind.Assistant => "Assistant",
                    ChatTurnKind.Tool => "Tool",
                    ChatTurnKind.Error => "Error",
                    ChatTurnKind.Work => "Worked",
                    _ => "Turn"
                });
                sb.AppendLine(":");
                sb.AppendLine(RedactSecrets(turn.Text));
                if (turn.Kind == ChatTurnKind.Work && turn.WorkItems != null)
                {
                    foreach (var item in turn.WorkItems)
                    {
                        if (string.IsNullOrWhiteSpace(item)) continue;
                        sb.AppendLine();
                        sb.AppendLine(RedactSecrets(item));
                    }
                }

                if (includeToolPayloads && turn.ToolSummaries != null)
                {
                    foreach (var summary in turn.ToolSummaries)
                    {
                        if (summary == null) continue;
                        sb.AppendLine("Raw tool payload:");
                        if (!string.IsNullOrWhiteSpace(summary.Name))
                            sb.AppendLine(summary.Name + ":");
                        sb.AppendLine(RedactSecrets(summary.ResultContent));
                    }
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Redacts common secret-bearing fields from copied transcript text and tool payloads.</summary>
        public static string RedactSecrets(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var redacted = BearerRegex.Replace(text, "Bearer [redacted]");
            redacted = SensitiveJsonRegex.Replace(redacted, "$1\"[redacted]\"");
            return SensitivePlainKeyRegex.Replace(redacted, m =>
            {
                var separator = m.Value.IndexOf(':') >= 0 ? ':' : '=';
                var prefix = m.Value.Substring(0, m.Value.IndexOf(separator) + 1);
                return prefix + " \"[redacted]\"";
            });
        }

        private static string ExtractErrorMessage(string resultContent)
        {
            if (string.IsNullOrWhiteSpace(resultContent)) return string.Empty;

            var match = JsonErrorRegex.Match(resultContent);
            return match.Success
                ? UnescapeJsonString(match.Groups[1].Value)
                : NormalizeSingleLine(resultContent);
        }

        private static string UnescapeJsonString(string value)
            => (value ?? string.Empty)
                .Replace("\\\"", "\"")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\\", "\\");

        private static string NormalizeSingleLine(string text)
            => string.IsNullOrEmpty(text)
                ? string.Empty
                : text.Replace("\r", " ").Replace("\n", " ").Trim();

        private static string Truncate(string text, int max)
            => string.IsNullOrEmpty(text) || text.Length <= max
                ? text
                : text.Substring(0, max) + "...";
    }
}
