using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>Structured block kinds produced from assistant text for the UI Toolkit transcript.</summary>
    public enum AssistantTextBlockKind
    {
        /// <summary>Regular prose.</summary>
        Paragraph,
        /// <summary>A short markdown heading.</summary>
        Heading,
        /// <summary>An unordered list item.</summary>
        Bullet,
        /// <summary>An ordered list item.</summary>
        Numbered,
        /// <summary>A fenced code block.</summary>
        Code,
        /// <summary>A warning line.</summary>
        Warning,
        /// <summary>An error line.</summary>
        Error
    }

    /// <summary>One parsed, renderable assistant text block.</summary>
    public sealed class AssistantTextBlock
    {
        /// <summary>The block type.</summary>
        public AssistantTextBlockKind Kind { get; }

        /// <summary>The visible block text, with inline Markdown markers stripped (for plain-text/copy use).</summary>
        public string Text { get; }

        /// <summary>
        /// The block text with inline Markdown markers preserved, for the inline-span renderer
        /// (Sprint 24.2). Equal to <see cref="Text"/> when there is nothing to tokenize (e.g. code blocks).
        /// </summary>
        public string RawText { get; }

        /// <summary>The list number for <see cref="AssistantTextBlockKind.Numbered"/> blocks.</summary>
        public int Number { get; }

        /// <summary>Creates a text block whose raw and cleaned text are identical.</summary>
        public AssistantTextBlock(AssistantTextBlockKind kind, string text, int number = 0)
            : this(kind, text, text, number)
        {
        }

        /// <summary>Creates a text block, preserving the raw (un-stripped) text for inline rendering.</summary>
        public AssistantTextBlock(AssistantTextBlockKind kind, string text, string rawText, int number)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            RawText = rawText ?? Text;
            Number = number;
        }
    }

    /// <summary>The kind of an inline run within a text block (Sprint 24.2).</summary>
    public enum AssistantInlineKind
    {
        /// <summary>Plain text.</summary>
        Text,
        /// <summary>An <c>`inline code`</c> run.</summary>
        Code,
        /// <summary>A <c>**bold**</c> run.</summary>
        Bold,
        /// <summary>An <c>_italic_</c> run.</summary>
        Italic,
        /// <summary>A clickable file/path link, optionally with a line number.</summary>
        Link,
        /// <summary>A clickable assistant context action, encoded as a <c>molca-context://</c> URI.</summary>
        Context
    }

    /// <summary>One inline run produced by <see cref="AssistantTranscriptFormatter.ParseInline"/>.</summary>
    public sealed class AssistantInlineSpan
    {
        /// <summary>The run kind.</summary>
        public AssistantInlineKind Kind { get; }

        /// <summary>The visible text of the run.</summary>
        public string Text { get; }

        /// <summary>For <see cref="AssistantInlineKind.Link"/>: the file/asset path (no line suffix).</summary>
        public string LinkPath { get; }

        /// <summary>For <see cref="AssistantInlineKind.Link"/>: the 1-based line number, or 0 if none.</summary>
        public int LinkLine { get; }

        /// <summary>For <see cref="AssistantInlineKind.Context"/>: the context action URI.</summary>
        public string ContextUri { get; }

        /// <summary>Creates an inline run.</summary>
        public AssistantInlineSpan(AssistantInlineKind kind, string text, string linkPath = null, int linkLine = 0, string contextUri = null)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            LinkPath = linkPath;
            LinkLine = linkLine;
            ContextUri = contextUri;
        }
    }

    /// <summary>
    /// Small, deterministic formatter for Molca Assistant transcripts. It intentionally supports only
    /// the subset the in-editor UI needs instead of pulling a full Markdown parser into the package.
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

        private static readonly Regex BoldRegex = new Regex(
            "\\*\\*([^*]+)\\*\\*",
            RegexOptions.Compiled);

        private static readonly Regex InlineCodeRegex = new Regex(
            "`([^`]+)`",
            RegexOptions.Compiled);

        private static readonly Regex ContextLinkRegex = new Regex(
            @"\[(?<label>[^\]\r\n]+)\]\((?<uri>molca-context://[^)\s]+)\)",
            RegexOptions.Compiled);

        private static readonly Regex JsonErrorRegex = new Regex(
            "\"error\"\\s*:\\s*\"([^\"]*)\"",
            RegexOptions.Compiled);

        /// <summary>Parses assistant text into renderable blocks.</summary>
        public static IReadOnlyList<AssistantTextBlock> Parse(string text)
        {
            var blocks = new List<AssistantTextBlock>();
            if (string.IsNullOrWhiteSpace(text)) return blocks;

            var paragraph = new StringBuilder();
            var code = new StringBuilder();
            var inCode = false;

            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.TrimEnd();
                var trimmed = line.Trim();

                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    if (inCode)
                    {
                        blocks.Add(new AssistantTextBlock(AssistantTextBlockKind.Code, code.ToString().TrimEnd('\n')));
                        code.Length = 0;
                        inCode = false;
                    }
                    else
                    {
                        FlushParagraph(blocks, paragraph);
                        inCode = true;
                    }
                    continue;
                }

                if (inCode)
                {
                    code.AppendLine(line);
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    FlushParagraph(blocks, paragraph);
                    continue;
                }

                if (TryReadNumbered(trimmed, out var number, out var numberedText))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(MakeBlock(AssistantTextBlockKind.Numbered, numberedText, number));
                    continue;
                }

                if (trimmed.StartsWith("# ", StringComparison.Ordinal) || trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(MakeBlock(AssistantTextBlockKind.Heading, trimmed.TrimStart('#').Trim()));
                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(MakeBlock(AssistantTextBlockKind.Bullet, trimmed.Substring(2).Trim()));
                    continue;
                }

                if (StartsWithLabel(trimmed, "Warning:"))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(MakeBlock(AssistantTextBlockKind.Warning, trimmed));
                    continue;
                }

                if (StartsWithLabel(trimmed, "Error:"))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(MakeBlock(AssistantTextBlockKind.Error, trimmed));
                    continue;
                }

                // Accumulate the raw line; inline markers are stripped only when the paragraph is flushed.
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(trimmed);
            }

            if (inCode && code.Length > 0)
                blocks.Add(new AssistantTextBlock(AssistantTextBlockKind.Code, code.ToString().TrimEnd('\n')));
            FlushParagraph(blocks, paragraph);
            return blocks;
        }

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

        /// <summary>Removes lightweight inline Markdown markers that UI Toolkit labels cannot style inline.</summary>
        public static string CleanInlineMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var clean = BoldRegex.Replace(text, "$1");
            clean = InlineCodeRegex.Replace(clean, "$1");
            return clean.Replace("\\_", "_");
        }

        private static readonly Regex LinkRegex = new Regex(
            @"(?<path>(?:[A-Za-z]:[\\/])?[\w./\\\-]+\.(?:cs|jsonl|js|ts|tsx|jsx|json|md|asmdef|uxml|uss|shader|cginc|hlsl|txt|yaml|yml|xml|csproj|cs?proj))(?::(?<line>\d+))?",
            RegexOptions.Compiled);

        /// <summary>
        /// Tokenizes a single text block into inline runs (Sprint 24.2): <c>`code`</c>, <c>**bold**</c>,
        /// <c>_italic_</c>, and <c>file:line</c> / path links. Deterministic and dependency-free — it
        /// recognizes only the subset the in-editor renderer styles, leaving everything else as plain text.
        /// </summary>
        /// <param name="text">The raw block text, inline markers preserved (use <see cref="AssistantTextBlock.RawText"/>).</param>
        /// <returns>Ordered inline runs; a single <see cref="AssistantInlineKind.Text"/> run for plain input.</returns>
        public static IReadOnlyList<AssistantInlineSpan> ParseInline(string text)
        {
            var spans = new List<AssistantInlineSpan>();
            if (string.IsNullOrEmpty(text)) return spans;

            var buffer = new StringBuilder();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];

                if (c == '[' && TryReadContextLink(text, i, out var label, out var uri, out var endIndex))
                {
                    FlushTextRun(spans, buffer);
                    spans.Add(new AssistantInlineSpan(AssistantInlineKind.Context, label, contextUri: uri));
                    i = endIndex + 1;
                    continue;
                }

                // `inline code` — content is literal, no nested parsing.
                if (c == '`')
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        FlushTextRun(spans, buffer);
                        spans.Add(new AssistantInlineSpan(AssistantInlineKind.Code, text.Substring(i + 1, end - i - 1)));
                        i = end + 1;
                        continue;
                    }
                }

                // **bold**
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 1)
                    {
                        FlushTextRun(spans, buffer);
                        spans.Add(new AssistantInlineSpan(AssistantInlineKind.Bold, text.Substring(i + 2, end - i - 2)));
                        i = end + 2;
                        continue;
                    }
                }

                // _italic_ — only at a word boundary so snake_case identifiers are left intact.
                if (c == '_' && IsWordBoundaryBefore(text, i))
                {
                    var end = FindItalicClose(text, i + 1);
                    if (end > i)
                    {
                        FlushTextRun(spans, buffer);
                        spans.Add(new AssistantInlineSpan(AssistantInlineKind.Italic, text.Substring(i + 1, end - i - 1)));
                        i = end + 1;
                        continue;
                    }
                }

                buffer.Append(c);
                i++;
            }

            FlushTextRun(spans, buffer);
            return spans;
        }

        private static bool TryReadContextLink(string text, int start, out string label, out string uri, out int endIndex)
        {
            label = null;
            uri = null;
            endIndex = -1;

            var match = ContextLinkRegex.Match(text, start);
            if (!match.Success || match.Index != start) return false;

            label = match.Groups["label"].Value;
            uri = match.Groups["uri"].Value;
            endIndex = match.Index + match.Length - 1;
            return !string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(uri);
        }

        /// <summary>Flushes a plain-text buffer, splitting out any file/path links into their own runs.</summary>
        private static void FlushTextRun(ICollection<AssistantInlineSpan> spans, StringBuilder buffer)
        {
            if (buffer.Length == 0) return;
            var text = buffer.ToString();
            buffer.Length = 0;

            var last = 0;
            foreach (Match m in LinkRegex.Matches(text))
            {
                if (m.Index > last)
                    spans.Add(new AssistantInlineSpan(AssistantInlineKind.Text, text.Substring(last, m.Index - last)));

                var path = m.Groups["path"].Value;
                var line = m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var n) ? n : 0;
                spans.Add(new AssistantInlineSpan(AssistantInlineKind.Link, m.Value, path, line));
                last = m.Index + m.Length;
            }

            if (last < text.Length)
                spans.Add(new AssistantInlineSpan(AssistantInlineKind.Text, last == 0 ? text : text.Substring(last)));
        }

        private static bool IsWordBoundaryBefore(string text, int index)
        {
            if (index == 0) return true;
            var prev = text[index - 1];
            return !char.IsLetterOrDigit(prev) && prev != '_';
        }

        private static int FindItalicClose(string text, int start)
        {
            for (var j = start; j < text.Length; j++)
            {
                if (text[j] != '_') continue;
                var next = j + 1 < text.Length ? text[j + 1] : ' ';
                if (j > start && !char.IsLetterOrDigit(next) && next != '_')
                    return j;
            }
            return -1;
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

        private static void FlushParagraph(ICollection<AssistantTextBlock> blocks, StringBuilder paragraph)
        {
            if (paragraph.Length == 0) return;
            blocks.Add(MakeBlock(AssistantTextBlockKind.Paragraph, paragraph.ToString()));
            paragraph.Length = 0;
        }

        /// <summary>Builds a block holding both the cleaned text and the raw (marker-preserving) text.</summary>
        private static AssistantTextBlock MakeBlock(AssistantTextBlockKind kind, string raw, int number = 0)
            => new AssistantTextBlock(kind, CleanInlineMarkdown(raw), raw, number);

        private static bool TryReadNumbered(string line, out int number, out string text)
        {
            number = 0;
            text = null;

            var dot = line.IndexOf('.');
            if (dot <= 0 || dot + 1 >= line.Length || !char.IsWhiteSpace(line[dot + 1])) return false;
            for (var i = 0; i < dot; i++)
                if (!char.IsDigit(line[i]))
                    return false;

            if (!int.TryParse(line.Substring(0, dot), out number)) return false;
            text = line.Substring(dot + 1).Trim();
            return text.Length > 0;
        }

        private static bool StartsWithLabel(string text, string label)
            => text.StartsWith(label, StringComparison.OrdinalIgnoreCase);

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
